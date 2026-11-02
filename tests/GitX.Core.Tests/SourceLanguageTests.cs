using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class SourceLanguageTests
{
    [Theory]
    [InlineData(".py", "python", "python", "PY")]
    [InlineData(".pyw", "python", "python", "PY")]
    [InlineData(".PYI", "python", "python", "PY")]
    [InlineData(".js", "javascript", "javascript", "JS")]
    [InlineData(".mjs", "javascript", "javascript", "JS")]
    [InlineData(".cjs", "javascript", "javascript", "JS")]
    [InlineData(".JSX", "javascript", "javascript", "JS")]
    [InlineData(".ts", "typescript", "typescript", "TS")]
    [InlineData(".mts", "typescript", "typescript", "TS")]
    [InlineData(".cts", "typescript", "typescript", "TS")]
    [InlineData(".d.ts", "typescript", "typescript", "TS")]
    [InlineData(".d.mts", "typescript", "typescript", "TS")]
    [InlineData(".d.cts", "typescript", "typescript", "TS")]
    [InlineData(".TSX", "typescript", "tsx", "TS")]
    public async Task New_language_paths_are_consistent_across_working_index_local_diff_commit_and_historical_source(string extension, string language, string grammar, string fileType)
    {
        using var repo = new TestRepository(); var path = "src/example" + extension;
        var text = language == "python" ? "def work(value):\n    return value\n" : "export function work(value) { return value; }\n";
        repo.Write(path, text); repo.Commit("language fixture"); var head = repo.Git("rev-parse", "HEAD").Trim();
        using var reader = new RepositoryReader();
        Assert.Equal(language, (await reader.DocumentAsync(new(repo.Root, path, ComparisonKind.WorkingFile))).Language);
        Assert.Equal(language, (await reader.DocumentAsync(new(repo.Root, path, ComparisonKind.Commit, head))).Language);
        var revision = await reader.DocumentAsync(new(repo.Root, path, ComparisonKind.RevisionFile, head));
        Assert.Equal(language, revision.Language); Assert.Equal(text, revision.Text);
        repo.Write(path, text + (language == "python" ? "# local change\n" : "// local change\n"));
        var local = await reader.DocumentAsync(new(repo.Root, path, ComparisonKind.LocalChanges));
        Assert.Equal(language, local.Language); Assert.NotNull(local.Diff);
        repo.Git("add", path);
        Assert.Equal(language, (await reader.DocumentAsync(new(repo.Root, path, ComparisonKind.Index))).Language);
        Assert.Equal(grammar, SourceLanguage.StructureGrammar(path)); Assert.True(SourceLanguage.SupportsStructure(language));
        Assert.Equal(fileType, new NavigationEntry(path, "", ComparisonKind.WorkingFile).FileType);
    }

    [Fact]
    public void Unknown_and_existing_paths_keep_their_expected_language_and_structure_availability()
    {
        Assert.Equal("plaintext", SourceLanguage.Identify("folder.py/README"));
        Assert.Equal("plaintext", SourceLanguage.Identify("unknown.txt"));
        Assert.Null(SourceLanguage.StructureGrammar("notes.md"));
        Assert.False(SourceLanguage.SupportsStructure("json")); Assert.False(SourceLanguage.SupportsStructure(null));
        Assert.Equal("java", SourceLanguage.StructureGrammar("Example.java"));
        Assert.Equal("c-sharp", SourceLanguage.StructureGrammar("Example.cs"));
        Assert.Equal("xml", SourceLanguage.Identify("MainWindow.axaml"));
    }
}
