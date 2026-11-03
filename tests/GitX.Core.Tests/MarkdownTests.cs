using System.Net;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class MarkdownTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZ8AAAAASUVORK5CYII=");

    [Theory]
    [InlineData("../../secret")]
    [InlineData("%2e%2e/%2e%2e/secret")]
    [InlineData("../.git/config")]
    [InlineData("/absolute")]
    [InlineData("//host/file")]
    [InlineData("file:///secret")]
    [InlineData("javascript:bad")]
    [InlineData("a%5Cb")]
    [InlineData("a%00b")]
    [InlineData("x?query")]
    [InlineData("%x")]
    [InlineData("NUL.png")]
    public void Links_cannot_escape_repository_or_address_metadata(string href)
        => Assert.Throws<IOException>(() => MarkdownResources.Resolve("docs/guide.md", href));

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.markdown")]
    [InlineData("docs/README.MDOWN")]
    [InlineData("docs/readme.mkd")]
    public void Conventional_markdown_extensions_use_the_same_reader(string path)
        => Assert.Equal("markdown", SourceLanguage.Identify(path));

    [Theory]
    [InlineData(" https://example.invalid")]
    [InlineData("https://example.invalid ")]
    [InlineData("https:\\example.invalid")]
    [InlineData("https:example.invalid")]
    [InlineData("https://example.invalid/\u0085hidden")]
    [InlineData("ftp://example.invalid")]
    public void External_links_reject_ambiguous_or_non_web_addresses(string href)
        => Assert.Null(MarkdownResources.ExternalLink(href));

    [Fact]
    public void Relative_links_keep_unicode_paths_and_fragments()
    {
        Assert.Equal(new("README.md", "使用"), MarkdownResources.Resolve("docs/guide.md", "../README.md#%E4%BD%BF%E7%94%A8"));
        Assert.Equal(new("docs/中文.md", ""), MarkdownResources.Resolve("docs/guide.md", "%E4%B8%AD%E6%96%87.md"));
        Assert.Equal(new("docs/guide.md", "header"), MarkdownResources.Resolve("docs/guide.md", "#header"));
        Assert.NotNull(MarkdownResources.ExternalLink("https://example.invalid/a"));
        Assert.Null(MarkdownResources.ExternalLink("file:///secret")); Assert.Null(MarkdownResources.ExternalLink("https://user:secret@example.invalid"));
    }

    [Fact]
    public async Task Images_follow_the_document_revision_and_enforce_type_and_size()
    {
        using var repo = new TestRepository(); repo.Write("docs/guide.md", "![image](a.png)");
        File.WriteAllBytes(Path.Combine(repo.Root, "docs/a.png"), Png); repo.Commit("base"); var sha = repo.Git("rev-parse", "HEAD").Trim();
        var image = await MarkdownResources.ReadImageAsync(repo.Root, "docs/guide.md", sha, "a.png"); Assert.Equal(Png, image.Bytes); Assert.Equal("image/png", image.ContentType);
        File.WriteAllText(Path.Combine(repo.Root, "docs/a.png"), "<html>bad</html>");
        await Assert.ThrowsAsync<IOException>(() => MarkdownResources.ReadImageAsync(repo.Root, "docs/guide.md", null, "a.png"));
        Assert.Equal(Png, (await MarkdownResources.ReadImageAsync(repo.Root, "docs/guide.md", sha, "a.png")).Bytes);
        await Assert.ThrowsAsync<IOException>(() => MarkdownResources.ReadImageAsync(repo.Root, "docs/guide.md", sha, "../missing.png"));
        await Assert.ThrowsAsync<IOException>(() => MarkdownResources.ReadImageAsync(repo.Root, "docs/guide.md", sha, "a.svg"));
        await Assert.ThrowsAsync<IOException>(() => MarkdownResources.ReadImageAsync(repo.Root, "docs/guide.md", "HEAD", "a.png"));
        await File.WriteAllBytesAsync(Path.Combine(repo.Root, "docs/a.png"), new byte[MarkdownResources.MaxImageBytes + 1]);
        await Assert.ThrowsAsync<IOException>(() => MarkdownResources.ReadImageAsync(repo.Root, "docs/guide.md", null, "a.png"));
    }

    [Fact]
    public async Task Image_endpoint_requires_current_version_and_random_window_capability()
    {
        using var repo = new TestRepository(); repo.Write("guide.md", "image"); File.WriteAllBytes(Path.Combine(repo.Root, "a.png"), Png);
        using var server = new EditorServer(); using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        server.Publish("{}"u8.ToArray(), new(7, repo.Root, "guide.md", null));
        var address = new Uri(server.Address, "markdown-image?version=7&href=a.png");
        var response = await http.GetAsync(address); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType); Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(server.Address, "markdown-image?version=6&href=a.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(server.Address, "markdown-image?version=7&href=..%2Fsecret.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(server.Address, "/markdown-image?version=7&href=a.png"))).StatusCode);
        server.Publish("{}"u8.ToArray()); Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(address)).StatusCode);
    }

    [Fact]
    public async Task Link_navigation_is_read_only_preserves_history_and_rejects_stale_messages()
    {
        using var repo = new TestRepository(); repo.Write("docs/guide.md", "# Guide"); repo.Write("README.md", "# Original"); repo.Commit("base");
        var sha = repo.Git("rev-parse", "HEAD").Trim(); repo.Write("README.md", "# Working tree");
        var status = repo.Git("status", "--porcelain"); using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenRevisionSourceAsync("docs/guide.md", sha); await model.OpenMarkdownLinkAsync("../README.md", model.DocumentVersion);
        Assert.Equal(sha, model.CurrentDocument!.SourceRevision); Assert.Equal("# Original", model.CurrentDocument.Text);
        var version = model.DocumentVersion; await model.OpenMarkdownLinkAsync("missing.md", version - 1); Assert.Equal(version, model.DocumentVersion);
        await Assert.ThrowsAsync<IOException>(() => model.OpenMarkdownLinkAsync("missing.md", version)); Assert.Equal("README.md", model.CurrentDocument.Path);
        Assert.True(await model.OpenCodeSearchMatchAsync(repo.Root, new("docs/guide.md", 1, 1, 0, "# Guide")));
        await model.OpenMarkdownLinkAsync("../README.md", model.DocumentVersion);
        Assert.Null(model.CurrentDocument!.SourceRevision); Assert.Equal("# Working tree", model.CurrentDocument.Text);
        Assert.Equal(sha, repo.Git("rev-parse", "HEAD").Trim()); Assert.Equal(status, repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Historical_image_never_follows_a_symbolic_link_entry()
    {
        using var repo = new TestRepository(); File.WriteAllBytes(Path.Combine(repo.Root, "link.png"), Png);
        var blob = repo.Git("hash-object", "-w", "link.png").Trim();
        repo.Git("update-index", "--add", "--cacheinfo", $"120000,{blob},link.png"); repo.Git("commit", "-m", "symlink fixture");
        var sha = repo.Git("rev-parse", "HEAD").Trim();
        await Assert.ThrowsAsync<IOException>(() => MarkdownResources.ReadImageAsync(repo.Root, "README.md", sha, "link.png"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarkdownResources.ReadImageAsync(repo.Root, "README.md", null, "link.png", cancelled.Token));
    }

    [Fact]
    public async Task Images_use_the_selected_worktree_not_the_common_repository_directory()
    {
        using var repo = new TestRepository(); repo.Write("README.md", "image"); File.WriteAllBytes(Path.Combine(repo.Root, "a.png"), Png); repo.Commit("base");
        var worktree = Path.Combine(repo.Container, "linked"); repo.Git("worktree", "add", "-b", "preview", worktree);
        var local = Png.Concat(new byte[] { 0 }).ToArray(); File.WriteAllBytes(Path.Combine(worktree, "a.png"), local);
        Assert.Equal(local, (await MarkdownResources.ReadImageAsync(worktree, "README.md", null, "a.png")).Bytes);
        Assert.Equal(Png, (await MarkdownResources.ReadImageAsync(repo.Root, "README.md", null, "a.png")).Bytes);
        Assert.Equal(Png, (await MarkdownResources.ReadImageAsync(worktree, "README.md", repo.Git("rev-parse", "HEAD").Trim(), "a.png")).Bytes);
    }

    [Fact]
    public async Task Markdown_bridge_requires_a_current_version_and_a_bounded_link()
    {
        var state = new EditorMessageState(false, 7, 7, "repo"); var calls = 0; var errors = new List<string>();
        Task Handle(EditorMessage _) { calls++; return Task.CompletedTask; }
        await EditorMessage.DispatchAsync("""{"type":"markdown-open","version":7,"href":"docs/a.md"}""", () => state, Handle, errors.Add);
        await EditorMessage.DispatchAsync("""{"type":"markdown-open","version":6,"href":"docs/a.md"}""", () => state, Handle, errors.Add);
        await EditorMessage.DispatchAsync("""{"type":"markdown-open","version":7,"href":1}""", () => state, Handle, errors.Add);
        Assert.Equal(1, calls); Assert.Single(errors);
    }
}
