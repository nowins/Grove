using System.Text.Json;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class WebViewScriptTests
{
    [Theory]
    [InlineData("window.gitx.load(7)")]
    [InlineData("window.gitx.navigate(-1)")]
    [InlineData("window.gitx.refreshComments()")]
    [InlineData("window.gitxMerge.layout(12)")]
    public void Native_commands_are_dispatched_through_the_string_only_boundary(string expression) =>
        Assert.Equal($"window.groveNative.command(() => ({expression}))", WebViewScript.Command(expression));

    [Fact]
    public void Snapshot_round_trips_both_WebView2_and_WebKit_string_representations()
    {
        var raw = "{\"version\":8,\"path\":\"目录/a.ts\",\"views\":[{\"scrollTop\":120}],\"side\":1}";
        var direct = WebViewScript.DecodeObject(raw);
        var encoded = WebViewScript.DecodeObject(JsonSerializer.Serialize(raw));
        Assert.Equal(direct, encoded);
        using var parsed = JsonDocument.Parse(direct!);
        Assert.Equal("目录/a.ts", parsed.RootElement.GetProperty("path").GetString());
        Assert.Equal("window.groveNative.json(() => (window.gitx.captureDockView()))", WebViewScript.Json("window.gitx.captureDockView()"));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("null")] [InlineData("\"null\"")]
    [InlineData("[]")] [InlineData("42")] [InlineData("\"not JSON\"")] [InlineData("{bad")]
    public void Invalid_or_non_object_snapshots_are_not_injected_as_script(string? result) => Assert.Null(WebViewScript.DecodeObject(result));

    [Fact]
    public void Oversized_snapshots_are_rejected() => Assert.Null(WebViewScript.DecodeObject(new string('x', 1024 * 1024 + 1)));
}
