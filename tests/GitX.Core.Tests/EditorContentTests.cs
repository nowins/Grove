using System.Text.Json;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class EditorContentTests
{
    private static readonly EditorToolbarState Toolbar = new("a.py", true, "注解", SourceTabs: [new("tab", "a.py", null, LocalFile: true)], ActiveSourceTabId: "tab", SourceMode: true);
    private static readonly RepositoryDocument Source = new("a.py", new string('中', 100000) + "\r\n🙂", "python", "工作区", "before");

    [Fact]
    public async Task Unchanged_body_transfers_fresh_metadata_and_full_reload_remains_self_contained()
    {
        using var server = new EditorServer(); using var http = new HttpClient();
        server.Publish(server.PreparePublication(1, Source, Toolbar, repository: "repo"));
        using var first = await Read(http, server);
        var id = first.RootElement.GetProperty("contentId").GetString()!;
        Assert.Equal(Source.Text, Text(first)); Assert.False(Reuse(first));
        var changed = Source with { Detail = "new metadata", PreserveView = true, RevealLine = 8, Blame = [new(1, "sha", "作者", "today", "message", 1)] };
        server.Publish(server.PreparePublication(2, changed, Toolbar with { BlameLabel = "隐藏注解" }, repository: "repo"));
        var bodies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => http.GetByteArrayAsync(new Uri(server.Address, "document?content=" + id))));
        Assert.All(bodies, bytes => Assert.True(bytes.Length < 4096));
        using var delta = JsonDocument.Parse(bodies[0]);
        Assert.True(Reuse(delta)); Assert.Equal("", Text(delta));
        Assert.Equal(id, delta.RootElement.GetProperty("contentId").GetString());
        Assert.Equal(2, delta.RootElement.GetProperty("version").GetInt32());
        var doc = delta.RootElement.GetProperty("document");
        Assert.Equal(8, doc.GetProperty("revealLine").GetInt32()); Assert.Single(doc.GetProperty("blame").EnumerateArray());
        Assert.Equal("new metadata", doc.GetProperty("detail").GetString());
        using var reload = await Read(http, server);
        Assert.False(Reuse(reload)); Assert.Equal(Source.Text, Text(reload));
        Assert.Equal("new metadata", reload.RootElement.GetProperty("document").GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("text")] [InlineData("root")] [InlineData("tab")] [InlineData("path")]
    [InlineData("revision")] [InlineData("language")] [InlineData("notice")] [InlineData("diff")]
    public async Task Content_identity_does_not_cross_changed_bytes_or_document_boundaries(string change)
    {
        using var server = new EditorServer(); using var http = new HttpClient();
        server.Publish(server.PreparePublication(1, Source, Toolbar, repository: "repo"));
        using var original = await Read(http, server); var id = original.RootElement.GetProperty("contentId").GetString()!;
        var next = change switch {
            "text" => Source with { Text = Source.Text + "\n" }, "path" => Source with { Path = "other.py" },
            "revision" => Source with { SourceRevision = "commit" }, "language" => Source with { Language = "plaintext" },
            "notice" => Source with { IsNotice = true }, "diff" => Source with { IsDiff = true }, _ => Source
        };
        server.Publish(server.PreparePublication(2, next, change == "tab" ? Toolbar with { ActiveSourceTabId = "other" } : Toolbar, repository: change == "root" ? "other" : "repo"));
        using var response = await Read(http, server, id);
        Assert.False(Reuse(response)); Assert.Equal(next.Text, Text(response));
        Assert.NotEqual(id, response.RootElement.GetProperty("contentId").GetString());
    }

    [Fact]
    public async Task Empty_sources_legacy_publish_and_prepared_but_unpublished_results_are_unambiguous()
    {
        using var server = new EditorServer(); using var http = new HttpClient();
        var empty = Source with { Text = "" };
        server.Publish(server.PreparePublication(1, empty, Toolbar, repository: "repo"));
        using var first = await Read(http, server); var id = first.RootElement.GetProperty("contentId").GetString()!;
        _ = server.PreparePublication(2, Source, Toolbar, repository: "repo");
        using var same = await Read(http, server, id);
        Assert.Equal(1, same.RootElement.GetProperty("version").GetInt32()); Assert.True(Reuse(same)); Assert.Equal("", Text(same));
        server.Publish(EditorServer.Serialize(3, Source));
        using var legacy = await Read(http, server, id);
        Assert.Equal(Source.Text, Text(legacy)); Assert.False(legacy.RootElement.TryGetProperty("reuseContent", out _));
        server.Publish(server.PreparePublication(4, empty, Toolbar, repository: "repo"));
        using var renewed = await Read(http, server, id);
        Assert.False(Reuse(renewed)); Assert.NotEqual(id, renewed.RootElement.GetProperty("contentId").GetString());
    }

    private static bool Reuse(JsonDocument document) => document.RootElement.GetProperty("reuseContent").GetBoolean();
    private static string? Text(JsonDocument document) => document.RootElement.GetProperty("document").GetProperty("text").GetString();
    private static async Task<JsonDocument> Read(HttpClient http, EditorServer server, string? id = null)
        => JsonDocument.Parse(await http.GetByteArrayAsync(new Uri(server.Address, "document" + (id is null ? "" : "?content=" + id))));
}
