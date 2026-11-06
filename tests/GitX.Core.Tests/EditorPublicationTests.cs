using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using LibGit2Sharp;
using Xunit;
namespace GitX.Core.Tests;
public sealed class EditorPublicationTests
{
    [Fact]
    public void Side_by_side_transport_omits_only_the_redundant_patch()
    {
        var before = new DiffSide("before.cs", "HEAD", "one\r\ntwo\r\n", true);
        var after = new DiffSide("after.cs", "工作区", "one\nchanged\n", true);
        var document = new RepositoryDocument("after.cs", "patch text", "csharp", "diff", "detail", IsDiff: true,
            Diff: new(before, after, [new(1, 1, 1, 1, [], [new(2, 2)])], false));
        using var json = JsonDocument.Parse(EditorServer.Serialize(17, document));
        var payload = json.RootElement.GetProperty("document");
        Assert.Equal("", payload.GetProperty("text").GetString());
        Assert.Equal(before.Text, payload.GetProperty("diff").GetProperty("old").GetProperty("text").GetString());
        Assert.Equal(after.Text, payload.GetProperty("diff").GetProperty("new").GetProperty("text").GetString());
        Assert.Single(payload.GetProperty("diff").GetProperty("blocks").EnumerateArray());
        Assert.Equal("patch text", document.Text);
        foreach (var fallback in new[] { document with { Diff = null }, document with { IsNotice = true }, document with { IsDiff = false } })
        {
            using var kept = JsonDocument.Parse(EditorServer.Serialize(18, fallback));
            Assert.Equal("patch text", kept.RootElement.GetProperty("document").GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Deferred_publication_serializes_once_on_demand_and_reloads_the_latest_tabs()
    {
        using var server = new EditorServer(); using var client = new HttpClient();
        var tabs = new CountedTabs();
        var document = new RepositoryDocument("a.cs", new string('中', 100000), "csharp", "", "");
        var toolbar = new EditorToolbarState("a.cs", false, "", SourceTabs: tabs, ActiveSourceTabId: "a");
        server.PublishDeferred(7, document, toolbar);
        Assert.Equal(0, tabs.Reads);
        var uri = new Uri(server.Address, "document");
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetStringAsync(uri)));
        Assert.All(responses, value => Assert.Equal(responses[0], value));
        Assert.Equal(1, tabs.Reads);
        var skipped = new CountedTabs();
        server.PublishDeferred(7, document, toolbar with { SourceTabs = skipped });
        server.PublishDeferred(7, document, toolbar with { SourceTabs = [], ActiveSourceTabId = null });
        using var latest = JsonDocument.Parse(await client.GetStringAsync(uri));
        Assert.Empty(latest.RootElement.GetProperty("toolbar").GetProperty("sourceTabs").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, latest.RootElement.GetProperty("toolbar").GetProperty("activeSourceTabId").ValueKind);
        Assert.Equal(document.Text, latest.RootElement.GetProperty("document").GetProperty("text").GetString());
        Assert.Equal(0, skipped.Reads);
        server.Publish(EditorServer.Serialize(8, document with { Text = "replacement" }));
        using var replaced = JsonDocument.Parse(await client.GetStringAsync(uri));
        Assert.Equal(8, replaced.RootElement.GetProperty("version").GetInt64());
        Assert.Equal("replacement", replaced.RootElement.GetProperty("document").GetProperty("text").GetString());
    }

    private sealed class CountedTabs : IReadOnlyList<SourceTabInfo>
    {
        public int Reads;
        public int Count => 0;
        public SourceTabInfo this[int index] => throw new IndexOutOfRangeException();
        public IEnumerator<SourceTabInfo> GetEnumerator() { Interlocked.Increment(ref Reads); return Enumerable.Empty<SourceTabInfo>().GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(ComparisonKind.WorkingFile)] [InlineData(ComparisonKind.Worktree)] [InlineData(ComparisonKind.Commit)]
    public void Existing_comment_snapshot_matches_the_exact_legacy_JSON_bytes(ComparisonKind kind)
    {
        using var fixture=new TestRepository();fixture.Write("a.txt","base");fixture.Commit("base");using var repo=new Repository(fixture.Root);
        foreach(var text in new[]{"", "中文 <>&\\\"\r\n😀", new string('中',32767),new string('中',32768)})
        {
            var request=new DocumentRequest(fixture.Root,"目录/a.txt",kind);var oldSide=kind==ComparisonKind.WorkingFile?null:new DiffSide("目录/a.txt","before",text,true);var newSide=new DiffSide("目录/a.txt","after",text,true);var revision=repo.Head.Tip.Sha;
            var legacy=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{request.Kind,request.Path,oldSide,newSide,revision}));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(legacy)).ToLowerInvariant(),CommentContext.Create(repo,request,revision,oldSide,newSide).Snapshot);
        }
    }
}
