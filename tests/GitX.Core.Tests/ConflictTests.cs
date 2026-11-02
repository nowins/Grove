using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ConflictTests
{
    [Fact]
    public async Task Add_add_session_has_empty_ancestor_and_keeps_exact_side_line_ranges()
    {
        using var repo = new TestRepository(); repo.Write("seed", "seed"); repo.Commit("seed");
        repo.Git("switch", "-c", "topic"); repo.Write("new.java", "incoming\n"); repo.Commit("incoming");
        repo.Git("switch", "main"); repo.Write("new.java", "ours without newline"); repo.Commit("ours");
        Assert.Throws<IOException>(() => repo.Git("merge", "topic"));
        var document = await new ConflictReader().ReadAsync(repo.Root, "new.java");
        var session = await ConflictReader.PrepareSessionAsync(document);
        Assert.Equal("", session.Result); var block = Assert.Single(session.Blocks);
        Assert.True(block.IsConflict); Assert.Equal(0, block.StartOffset); Assert.Equal(0, block.Length);
        Assert.Equal("ours without newline", block.LeftText); Assert.Equal("incoming\n", block.RightText);
        Assert.Equal((0, 1, 0, 1), (block.LeftStart, block.LeftCount, block.RightStart, block.RightCount));
        Assert.Single(await ConflictReader.ListAsync(repo.Root));
    }
    [Fact]
    public async Task Interactive_session_starts_at_ancestor_and_separates_clean_changes_from_conflicts()
    {
        using var repo = Fixture();
        var doc = await new ConflictReader().ReadAsync(repo.Root, "a.txt");
        var session = await ConflictReader.PrepareSessionAsync(doc);
        Assert.Equal(doc.Base.Text, session.Result);
        Assert.Equal(2, session.Blocks.Count(b => b.IsConflict));
        Assert.Equal(2, session.Blocks.Count(b => !b.IsConflict));
        var clean = session.Blocks.Where(b => !b.IsConflict).ToArray();
        Assert.Contains(clean, b => b.LeftChanged && !b.RightChanged && b.AutomaticText!.Contains("clean current 3"));
        Assert.Contains(clean, b => !b.LeftChanged && b.RightChanged && b.AutomaticText!.Contains("clean incoming 36"));
        var result = Accept(session, b => b.IsConflict ? b.LeftText : b.AutomaticText!);
        Assert.Contains("current 8", result); Assert.Contains("current 22", result);
        Assert.Contains("clean current 3", result); Assert.Contains("clean incoming 36", result);
        Assert.False(result.EndsWith('\n'));
        Assert.NotEmpty(repo.Git("ls-files", "-u"));
    }

    [Fact]
    public async Task Saving_current_result_without_clean_changes_preserves_manual_text_and_does_not_auto_apply()
    {
        using var repo = Fixture();
        var session = await ConflictReader.PrepareSessionAsync(await new ConflictReader().ReadAsync(repo.Root, "a.txt"));
        var head = repo.Git("rev-parse", "HEAD");
        var current = Accept(session, block => block.IsConflict ? block.LeftText : block.BaseText)
            .Replace("line 13", "manual result 13", StringComparison.Ordinal);
        var writer = new ConflictWriter(); var expected = await writer.SaveAsync(session, session.Token, current);
        Assert.NotEmpty(repo.Git("ls-files", "-u")); // Saving alone does not resolve the index.
        await writer.ResolveAsync(session, expected);
        Assert.Equal(current, repo.Git("show", ":a.txt"));
        Assert.Equal(current, File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Contains("manual result 13", current); Assert.Contains("current 8", current); Assert.Contains("current 22", current);
        Assert.DoesNotContain("clean current 3", current); Assert.DoesNotContain("clean incoming 36", current);
        Assert.Contains("line 3\n", current); Assert.Contains("line 36\n", current);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Empty(repo.Git("ls-files", "-u"));
    }

    [Fact]
    public async Task Applying_clean_changes_before_save_keeps_both_sides_and_manual_edits_in_the_index()
    {
        using var repo = Fixture();
        var session = await ConflictReader.PrepareSessionAsync(await new ConflictReader().ReadAsync(repo.Root, "a.txt"));
        var head = repo.Git("rev-parse", "HEAD");
        var result = Accept(session, block => block.IsConflict ? block.LeftText : block.AutomaticText!)
            .Replace("line 13", "manual result 13", StringComparison.Ordinal);
        var writer = new ConflictWriter(); var expected = await writer.SaveAsync(session, session.Token, result);
        await writer.ResolveAsync(session, expected);
        var indexed = repo.Git("show", ":a.txt");
        Assert.Equal(result, indexed); Assert.Equal(result, File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Contains("clean current 3", indexed); Assert.Contains("clean incoming 36", indexed); Assert.Contains("manual result 13", indexed);
        Assert.Contains("current 8", indexed); Assert.Contains("current 22", indexed);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Empty(repo.Git("ls-files", "-u"));
    }
    private static TestRepository Fixture(string operation = "merge", string newline = "\n")
    {
        var repo = new TestRepository();
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToArray();
        repo.Write("a.txt", string.Join(newline, lines)); repo.Commit("base");
        repo.Git("checkout", "-b", "topic");
        var incoming = lines.ToArray(); incoming[7] = "incoming 8"; incoming[21] = "incoming 22"; incoming[35] = "clean incoming 36";
        repo.Write("a.txt", string.Join(newline, incoming)); repo.Commit("incoming");
        repo.Git("checkout", "main");
        var current = lines.ToArray(); current[7] = "current 8"; current[21] = "current 22"; current[2] = "clean current 3";
        repo.Write("a.txt", string.Join(newline, current)); repo.Commit("current");
        Assert.Throws<IOException>(() => repo.Git(operation, "topic"));
        return repo;
    }
    private static string Accept(ConflictDocument document, Func<MergeBlock, string> choose)
    {
        var result = document.Result!;
        foreach (var block in document.Blocks.Reverse()) result = result.Remove(block.StartOffset, block.Length).Insert(block.StartOffset, choose(block));
        return result;
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    public async Task Three_stage_read_save_resolve_and_continue_are_separate(string operation)
    {
        using var repo = Fixture(operation);
        var reader = new ConflictReader(); var writer = new ConflictWriter();
        var document = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.Null(document.Limitation); Assert.Equal(operation, document.Operation!.Kind);
        Assert.Equal(2, document.Blocks.Count); Assert.Contains("clean current 3", document.Result); Assert.Contains("clean incoming 36", document.Result);
        Assert.Contains("line 8", document.Result); Assert.NotNull(document.Base.Sha);
        if (operation == "rebase") { Assert.Contains("变基目标", document.Left.Label); Assert.Contains("incoming 8", document.Left.Text); }
        else Assert.Contains("current 8", document.Left.Text);
        var staged = repo.Git("ls-files", "-u"); var result = Accept(document, block => block.RightText);
        var expected = await writer.SaveAsync(document, document.Token, result);
        Assert.Equal(result, File.ReadAllText(Path.Combine(repo.Root, "a.txt"))); Assert.Equal(staged, repo.Git("ls-files", "-u"));
        var reopened = await reader.ReadAsync(repo.Root, "a.txt"); Assert.True(reopened.ExistingResult); Assert.Equal(result, reopened.Result);
        await writer.ResolveAsync(document, expected); Assert.Empty(repo.Git("ls-files", "-u"));
        var snapshot = await new RepositoryReader().OpenAsync(repo.Root);
        Assert.NotNull(snapshot.Operation); await writer.OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, false);
        Assert.Null((await new RepositoryReader().OpenAsync(repo.Root)).Operation);
        Assert.Equal(result, repo.Git("show", "HEAD:a.txt"));
    }

    [Fact]
    public async Task Save_preserves_crlf_and_no_final_newline_and_rejects_external_edits()
    {
        using var repo = Fixture(newline: "\r\n");
        var reader = new ConflictReader(); var writer = new ConflictWriter(); var doc = await reader.ReadAsync(repo.Root, "a.txt");
        var result = Accept(doc, b => b.LeftText);
        var expected = await writer.SaveAsync(doc, doc.Token, result);
        Assert.Equal(result.Replace("\n", "\r\n"), File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.False(result.EndsWith('\n'));
        repo.Write("a.txt", "external content");
        await Assert.ThrowsAsync<IOException>(() => writer.SaveAsync(doc, expected, "must not overwrite"));
        await Assert.ThrowsAsync<IOException>(() => writer.ResolveAsync(doc, expected));
        Assert.Equal("external content", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task Markerless_result_does_not_resolve_index_and_markers_cannot_be_staged_as_resolved()
    {
        using var repo = Fixture(); var doc = await new ConflictReader().ReadAsync(repo.Root, "a.txt"); var writer = new ConflictWriter();
        await Assert.ThrowsAsync<IOException>(() => writer.ResolveAsync(doc, doc.Token));
        var expected = await writer.SaveAsync(doc, doc.Token, doc.Result!);
        Assert.NotEmpty(repo.Git("ls-files", "-u"));
        var snapshot = await new RepositoryReader().OpenAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => writer.OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, false));
        await writer.OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, true);
        Assert.Null((await new RepositoryReader().OpenAsync(repo.Root)).Operation);
        Assert.Contains("current 8", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.True(Directory.Exists(Path.Combine(repo.Root, ".git", "gitx-recovery")));
    }

    [Fact]
    public async Task Delete_modify_and_binary_conflicts_use_whole_side_without_resolving_early()
    {
        using var repo = new TestRepository(); repo.Write("gone.txt", "base"); File.WriteAllBytes(Path.Combine(repo.Root, "b.bin"), [0, 1]); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("gone.txt", "incoming"); File.WriteAllBytes(Path.Combine(repo.Root, "b.bin"), [0, 2]); repo.Commit("incoming");
        repo.Git("checkout", "main"); File.Delete(Path.Combine(repo.Root, "gone.txt")); File.WriteAllBytes(Path.Combine(repo.Root, "b.bin"), [0, 3]); repo.Commit("current");
        Assert.Throws<IOException>(() => repo.Git("merge", "topic"));
        var reader = new ConflictReader(); var writer = new ConflictWriter();
        foreach (var path in new[] { "gone.txt", "b.bin" })
        {
            var doc = await reader.ReadAsync(repo.Root, path); Assert.NotNull(doc.Limitation);
            var expected = await writer.ChooseSideAsync(doc, doc.Token, true); Assert.NotEmpty(repo.Git("ls-files", "-u", "--", path));
            await writer.ResolveAsync(doc, expected); Assert.Empty(repo.Git("ls-files", "-u", "--", path));
        }
        Assert.False(File.Exists(Path.Combine(repo.Root, "gone.txt"))); Assert.Equal(new byte[] { 0, 3 }, File.ReadAllBytes(Path.Combine(repo.Root, "b.bin")));
    }

    [Fact]
    public async Task Nonconflicting_merge_matches_git_and_conflict_at_eof_keeps_no_newline()
    {
        var (text, blocks) = await ConflictReader.MergeAsync("a\nb\nc", "A\nb\nc", "a\nb\nC");
        Assert.Equal("A\nb\nC", text); Assert.Empty(blocks);
        (text, blocks) = await ConflictReader.MergeAsync("base", "ours", "theirs");
        Assert.Equal("base", text); Assert.Single(blocks); Assert.Equal("ours", blocks[0].LeftText); Assert.Equal("theirs", blocks[0].RightText);
    }

    [Fact]
    public async Task Consecutive_rebase_conflicts_advance_step_and_refresh_stages()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base\n"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("a.txt", "incoming\n"); repo.Commit("incoming");
        repo.Git("checkout", "main"); repo.Write("a.txt", "first\n"); repo.Commit("first"); repo.Write("a.txt", "second\n"); repo.Commit("second");
        Assert.Throws<IOException>(() => repo.Git("rebase", "topic"));
        var writer = new ConflictWriter();
        for (var step = 1; step <= 2; step++)
        {
            var doc = await new ConflictReader().ReadAsync(repo.Root, "a.txt");
            Assert.Contains($"第 {step}/2", doc.Operation!.Detail);
            Assert.Contains(step == 1 ? "first" : "second", doc.Right.Text);
            var expected = await writer.SaveAsync(doc, doc.Token, $"resolved round {step}\n");
            await writer.ResolveAsync(doc, expected);
            var snapshot = await new RepositoryReader().OpenAsync(repo.Root);
            await writer.OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, false);
        }
        Assert.Null((await new RepositoryReader().OpenAsync(repo.Root)).Operation);
        Assert.Equal("resolved round 2\n", repo.Git("show", "HEAD:a.txt"));
    }

    [Theory]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    public async Task Abort_restores_head_and_keeps_recovery_copy(string operation)
    {
        using var repo = Fixture(operation); var snapshot = await new RepositoryReader().OpenAsync(repo.Root);
        await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, true);
        Assert.Null((await new RepositoryReader().OpenAsync(repo.Root)).Operation);
        Assert.Empty(repo.Git("ls-files", "-u"));
        Assert.Contains("current 8", repo.Git("show", "HEAD:a.txt"));
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    public async Task Abort_ignores_build_directories_and_backs_up_nested_untracked_files(string operation)
    {
        using var repo = Fixture(operation);
        repo.Write(".git/info/exclude", "bin/\nobj/\nnode_modules/\n");
        foreach (var directory in new[] { "bin", "obj", "node_modules" }) repo.Write(directory + "/artifact", "ignored");
        repo.Write("notes/nested/draft.txt", "keep draft");
        var before = File.ReadAllText(Path.Combine(repo.Root, "a.txt"));
        var snapshot = await new RepositoryReader().OpenAsync(repo.Root);
        await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, true);
        Assert.Null((await new RepositoryReader().OpenAsync(repo.Root)).Operation); Assert.Empty(repo.Git("ls-files", "-u"));
        foreach (var directory in new[] { "bin", "obj", "node_modules" }) Assert.Equal("ignored", File.ReadAllText(Path.Combine(repo.Root, directory, "artifact")));
        Assert.Equal("keep draft", File.ReadAllText(Path.Combine(repo.Root, "notes/nested/draft.txt")));
        var backups = Directory.GetDirectories(Path.Combine(repo.Root, ".git/gitx-recovery"));
        Assert.Contains(backups, p => File.ReadAllText(Path.Combine(p, "path.txt")) == "a.txt" && File.ReadAllText(Path.Combine(p, "content")) == before);
        Assert.Contains(backups, p => File.ReadAllText(Path.Combine(p, "path.txt")) == "notes/nested/draft.txt");
    }

    [Fact]
    public async Task Rename_rename_conflicts_expose_missing_sides_and_allow_per_path_resolution()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base\n"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Git("mv", "a.txt", "right.txt"); repo.Commit("right");
        repo.Git("checkout", "main"); repo.Git("mv", "a.txt", "left.txt"); repo.Commit("left");
        Assert.Throws<IOException>(() => repo.Git("merge", "topic"));
        foreach (var path in new[] { "a.txt", "left.txt", "right.txt" })
        {
            var doc = await new ConflictReader().ReadAsync(repo.Root, path); Assert.False(doc.IsText);
            var writer = new ConflictWriter(); var expected = await writer.ChooseSideAsync(doc, doc.Token, true);
            if (!File.Exists(Path.Combine(repo.Root, path)))
                Assert.Contains("在工作区不存在", (await new RepositoryReader().DocumentAsync(new(repo.Root, path, ComparisonKind.Conflict))).Text);
            await writer.ResolveAsync(doc, expected);
        }
        Assert.Empty(repo.Git("ls-files", "-u")); Assert.Equal("base\n", repo.Git("show", ":left.txt"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "right.txt")));
    }

    [Fact]
    public async Task Custom_attributes_fall_back_and_changes_after_open_block_text_save()
    {
        using var repo = Fixture(); var doc = await new ConflictReader().ReadAsync(repo.Root, "a.txt");
        repo.Write(".gitattributes", "a.txt filter=custom\n");
        Assert.False((await new ConflictReader().ReadAsync(repo.Root, "a.txt")).IsText);
        await Assert.ThrowsAsync<IOException>(() => new ConflictWriter().SaveAsync(doc, doc.Token, "unsafe"));
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }
}
