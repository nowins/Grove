using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;
public sealed class BlameTests
{
    [Fact]
    public async Task Blame_actions_follow_original_paths_and_rename_changes_without_writing()
    {
        using var repo = new TestRepository();
        const string original = "旧 名.java", renamed = "新 名.java";
        var initial = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        repo.Write(original, initial); repo.Commit("initial"); var rootSha = repo.Git("rev-parse", "HEAD").Trim();
        repo.Git("mv", original, renamed);
        var changed = initial.Replace("line 20\n", "changed\n"); repo.Write(renamed, changed); repo.Commit("rename and edit");
        var head = repo.Git("rev-parse", "HEAD").Trim();
        var working = changed.Replace("line 2\n", "working\n"); repo.Write(renamed, working);
        var index = repo.Git("write-tree");
        var reader = new BlameReader(); var doc = await reader.ReadAsync(repo.Root, renamed);
        var old = doc.Blame![0]; Assert.Equal(rootSha, old.Sha); Assert.Equal(original, old.OriginalPath);
        var rootDiff = await reader.ReadChangeAsync(repo.Root, old);
        Assert.False(rootDiff.Diff!.Old.Exists); Assert.Equal(initial, rootDiff.Diff.New.Text);
        var renamedLine = doc.Blame[19]; Assert.Equal(head, renamedLine.Sha); Assert.Equal(renamed, renamedLine.OriginalPath);
        var renameDiff = await reader.ReadChangeAsync(repo.Root, renamedLine);
        Assert.Equal(original, renameDiff.Diff!.Old.Path); Assert.Equal(renamed, renameDiff.Diff.New.Path);
        Assert.Equal(initial, renameDiff.Diff.Old.Text); Assert.Equal(changed, renameDiff.Diff.New.Text);
        Assert.False(doc.Blame[1].IsCommitted);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadChangeAsync(repo.Root, doc.Blame[1]));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD").Trim()); Assert.Equal(index, repo.Git("write-tree"));
        Assert.Equal(working, File.ReadAllText(Path.Combine(repo.Root, renamed)));
    }

    [Fact]
    public void Blame_paths_decode_git_quotes_and_survive_compact_metadata()
    {
        var sha = new string('a', 40);
        var output = string.Join('\n', $"{sha} 1 1 2", "author Author", "summary initial", "filename \"\\344\\270\\255\\346\\226\\207 name.java\"", "\tfirst", $"{sha} 2 2", "\tsecond", "");
        var lines = BlameReader.Parse(output);
        Assert.Equal(2, lines.Count); Assert.All(lines, line => Assert.Equal("中文 name.java", line.OriginalPath));
        Assert.All(lines, line => Assert.True(line.IsCommitted));
    }
    [Fact]
    public async Task Blame_matches_committed_and_uncommitted_lines_with_unicode()
    {
        using var repo = new TestRepository(); repo.Write("中文.cs", "first\nsecond\nthird"); repo.Commit("初始"); var first = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("中文.cs", "first\nmodified\nthird"); repo.Commit("修改第二行"); var second = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("中文.cs", "first\nmodified\nworking");
        var doc = await new BlameReader().ReadAsync(repo.Root, "中文.cs");
        Assert.Equal(3, doc.Blame!.Count); Assert.Equal(first, doc.Blame[0].Sha); Assert.Equal(second, doc.Blame[1].Sha);
        Assert.Equal(new string('0', 40), doc.Blame[2].Sha); Assert.Equal("未提交", doc.Blame[2].Author);
        Assert.Equal("修改第二行", doc.Blame[1].Summary); Assert.Equal("first\nmodified\nworking", doc.Text);
        repo.Write("new.cs", "新内容"); var untracked = await new BlameReader().ReadAsync(repo.Root, "new.cs"); Assert.Equal("未提交", untracked.Blame![0].Author);
        repo.Write("new.cs", "新内容\n"); Assert.Single((await new BlameReader().ReadAsync(repo.Root, "new.cs")).Blame!);
        repo.Write("new.cs", ""); Assert.Empty((await new BlameReader().ReadAsync(repo.Root, "new.cs")).Blame!);
        repo.Write("binary", "a\0b"); var binary = await new BlameReader().ReadAsync(repo.Root, "binary"); Assert.True(binary.IsNotice); Assert.Null(binary.Blame);
    }

    [Fact]
    public async Task Compact_metadata_survives_separated_hunks_and_long_author_on_4999_lines()
    {
        using var repo = new TestRepository();
        var author = new string('A', 120); var message = new string('B', 160);
        repo.Git("config", "user.name", author);
        var text = string.Join('\n', Enumerable.Range(1, 4999).Select(i => $"line {i}"));
        repo.Write("a.txt", text); repo.Commit(message);
        var doc = await new BlameReader().ReadAsync(repo.Root, "a.txt");
        Assert.Equal(4999, doc.Blame!.Count);
        Assert.All(doc.Blame, line => { Assert.Equal(author, line.Author); Assert.Equal(message, line.Summary); });
        repo.Write("a.txt", text.Replace("line 2500\n", "changed\n")); repo.Commit("middle");
        doc = await new BlameReader().ReadAsync(repo.Root, "a.txt");
        Assert.Equal(message, doc.Blame![0].Summary); Assert.Equal("middle", doc.Blame[2499].Summary);
        Assert.Equal(message, doc.Blame[4998].Summary); Assert.Equal(author, doc.Blame[4998].Author);
        Assert.Equal(doc.Blame[0].When, doc.Blame[4998].When);
        repo.Write("a.txt", string.Join('\n', Enumerable.Repeat("x", 5001)));
        var error = await Assert.ThrowsAsync<IOException>(() => new BlameReader().ReadAsync(repo.Root, "a.txt"));
        Assert.Contains("5,000", error.Message);
    }
}
