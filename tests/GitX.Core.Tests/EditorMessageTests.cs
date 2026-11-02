using System.Text.Json;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class EditorMessageTests
{
    private static readonly EditorMessageState Current = new(false, 7, 7, "repo");

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{\"type\":null}")]
    [InlineData("{\"type\":7}")]
    [InlineData("""{"type":"view","index":4}""")]
    [InlineData("""{"type":"view","index":3}""")]
    [InlineData("""{"type":"code-search","version":7,"filesOnly":"true"}""")]
    [InlineData("""{"type":"code-search","version":7}""")]
    [InlineData("""{"type":"code-search","version":7,"filesOnly":true,"quick":"true"}""")]
    [InlineData("""{"type":"source-history"}""")]
    [InlineData("""{"type":"hide-tool"}""")]
    [InlineData("""{"type":"reset-layout"}""")]
    [InlineData("""{"type":"history-back","version":"7"}""")]
    [InlineData("""{"type":"whitespace","value":"bad"}""")]
    [InlineData("""{"type":"whitespace","version":7,"value":3}""")]
    [InlineData("""{"type":"include-file","version":"7"}""")]
    [InlineData("""{"type":"include-file","version":9223372036854775808}""")]
    [InlineData("""{"type":"include-file","version":7.5}""")]
    [InlineData("""{"type":"include-file","version":-2}""")]
    [InlineData("""{"type":"include-line","version":7,"side":2,"line":1,"include":true}""")]
    [InlineData("""{"type":"include-line","version":7,"side":1,"line":0,"include":true}""")]
    [InlineData("""{"type":"include-line","version":7,"side":1,"line":1,"include":"true"}""")]
    [InlineData("""{"type":"include-deletion","version":7,"block":-1,"include":true}""")]
    [InlineData("""{"type":"source-tab","version":7,"id":"tab","action":"other"}""")]
    [InlineData("""{"type":"source-tab","version":7,"id":[],"action":"close"}""")]
    [InlineData("""{"type":"blame-view","version":7,"field":"date","value":1}""")]
    [InlineData("""{"type":"blame-view","version":7,"field":"unknown","value":true}""")]
    [InlineData("""{"type":"blame-action","version":7,"line":-1,"action":"previous"}""")]
    [InlineData("""{"type":"stage-selection","version":7,"side":1,"block":-1,"startLine":1,"endLine":2}""")]
    [InlineData("""{"type":"stage-selection","version":7,"side":1,"block":null,"startLine":2,"endLine":1}""")]
    [InlineData("""{"type":"focus-out","version":7,"direction":0}""")]
    [InlineData("""{"type":"caret","version":7,"side":1,"line":0,"column":1}""")]
    [InlineData("""{"type":"caret","version":7,"side":1,"line":1,"column":"x"}""")]
    [InlineData("""{"type":"error","message":{}}""")]
    [InlineData("""{"type":"comment","action":"save","id":"draft","body":null}""")]
    [InlineData("""{"type":"comment","action":"delete","repositoryId":"r","id":"comment"}""")]
    [InlineData("""{"type":"comment","action":"open-source","repositoryId":"r","id":"comment","revision":"record"}""")]
    [InlineData("""{"type":"comment","action":"open-source","version":7,"repositoryId":"r","id":"comment"}""")]
    public async Task Malformed_messages_never_reach_an_action_and_report_one_error(string body)
    {
        var calls = 0; var errors = new List<string>();
        await EditorMessage.DispatchAsync(body, () => Current, _ => { calls++; return Task.CompletedTask; }, errors.Add);
        Assert.Equal(0, calls); Assert.Equal("编辑器消息格式无效。", Assert.Single(errors));
    }

    [Theory]
    [InlineData("""{"type":"ready"}""")]
    [InlineData("""{"type":"loaded","version":7,"models":2,"hunks":4}""")]
    [InlineData("""{"type":"view","index":0}""")]
    [InlineData("""{"type":"refresh"}""")]
    [InlineData("""{"type":"open"}""")]
    [InlineData("""{"type":"code-search","version":7,"filesOnly":false}""")]
    [InlineData("""{"type":"code-search","version":7,"filesOnly":true,"quick":true}""")]
    [InlineData("""{"type":"source-history","version":7}""")]
    [InlineData("""{"type":"history-back","version":7}""")]
    [InlineData("""{"type":"hide-tool","version":7}""")]
    [InlineData("""{"type":"reset-layout","version":7}""")]
    [InlineData("""{"type":"blame","version":7}""")]
    [InlineData("""{"type":"source-tab","version":7,"id":"历史标签","action":"select"}""")]
    [InlineData("""{"type":"source-tab","version":7,"id":"tab","action":"close"}""")]
    [InlineData("""{"type":"blame-action","version":7,"line":3,"action":"diff"}""")]
    [InlineData("""{"type":"blame-view","version":7,"field":"date","value":true}""")]
    [InlineData("""{"type":"stage-selection","version":7,"side":1,"block":null,"startLine":1,"endLine":2}""")]
    [InlineData("""{"type":"revert-block","version":7,"block":0}""")]
    [InlineData("""{"type":"include-line","version":7,"side":0,"line":1,"include":false}""")]
    [InlineData("""{"type":"include-file","version":7}""")]
    [InlineData("""{"type":"include-deletion","version":7,"block":0,"include":true}""")]
    [InlineData("""{"type":"whitespace","version":7,"value":2}""")]
    [InlineData("""{"type":"close-document","version":7}""")]
    [InlineData("""{"type":"focus-navigation","version":7}""")]
    [InlineData("""{"type":"focus-out","version":7,"direction":-1}""")]
    [InlineData("""{"type":"error","version":7,"message":"读取失败"}""")]
    [InlineData("""{"type":"comment","action":"begin","version":7,"snapshot":"s","side":1,"start":1,"end":2}""")]
    [InlineData("""{"type":"comment","action":"save","id":"draft","body":"中文\n第二行"}""")]
    [InlineData("""{"type":"comment","action":"open-source","version":7,"repositoryId":"r","id":"comment","revision":"record"}""")]
    public async Task Valid_messages_keep_their_payload_alive_across_await(string body)
    {
        var calls = 0; var errors = new List<string>();
        await EditorMessage.DispatchAsync(body, () => Current, async message =>
        {
            await Task.Yield();
            Assert.Equal(message.Type, message.Payload.GetProperty("type").GetString()); calls++;
        }, errors.Add);
        Assert.Equal(1, calls); Assert.Empty(errors);
    }

    [Theory]
    [InlineData("""{"type":"include-file","version":6}""", 7, 7)]
    [InlineData("""{"type":"code-search","version":6,"filesOnly":true}""", 7, 7)]
    [InlineData("""{"type":"source-history","version":6}""", 7, 7)]
    [InlineData("""{"type":"history-back","version":7}""", 7, 8)]
    [InlineData("""{"type":"hide-tool","version":6}""", 7, 7)]
    [InlineData("""{"type":"reset-layout","version":6}""", 7, 7)]
    [InlineData("""{"type":"include-file","version":7}""", 6, 7)]
    [InlineData("""{"type":"include-file","version":7}""", 7, 8)]
    [InlineData("""{"type":"source-tab","version":6,"action":null}""", 7, 7)]
    [InlineData("""{"type":"whitespace","version":6,"value":2}""", 7, 7)]
    [InlineData("""{"type":"comment","action":"begin","version":6}""", 7, 7)]
    [InlineData("""{"type":"comment","action":"open-source","version":6}""", 7, 7)]
    [InlineData("""{"type":"error","version":6,"message":"旧错误"}""", 7, 7)]
    public async Task Old_or_unpublished_documents_are_ignored_before_action_validation(string body, long published, long current)
    {
        var calls = 0; var errors = new List<string>();
        await EditorMessage.DispatchAsync(body, () => Current with { PublishedVersion = published, DocumentVersion = current },
            _ => { calls++; return Task.CompletedTask; }, errors.Add);
        Assert.Equal(0, calls); Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sync_and_async_action_failures_are_observed(bool afterAwait)
    {
        var errors = new List<string>();
        async Task FailLater(EditorMessage _) { await Task.Yield(); throw new InvalidOperationException("handler failure"); }
        Task FailNow(EditorMessage _) => throw new InvalidOperationException("handler failure");
        await EditorMessage.DispatchAsync("""{"type":"include-file","version":7}""", () => Current, afterAwait ? FailLater : FailNow, errors.Add);
        Assert.Equal("编辑器消息处理失败：handler failure", Assert.Single(errors));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("repository")]
    [InlineData("document")]
    [InlineData("publication")]
    public async Task Late_action_errors_cannot_overwrite_a_closed_or_changed_context(string change)
    {
        var state = Current; var errors = new List<string>();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = EditorMessage.DispatchAsync("""{"type":"include-file","version":7}""", () => state, _ => pending.Task, errors.Add);
        Assert.False(task.IsCompleted);
        state = change switch
        {
            "close" => state with { Closed = true }, "repository" => state with { Root = "other" },
            "document" => state with { DocumentVersion = 8 }, _ => state with { PublishedVersion = 8 }
        };
        pending.SetException(new IOException("late failure")); await task;
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Cancellation_unknown_commands_closed_windows_and_null_bodies_are_quiet()
    {
        var calls = 0; var errors = new List<string>();
        Task Handle(EditorMessage _) { calls++; return Task.CompletedTask; }
        await EditorMessage.DispatchAsync(null, () => Current, Handle, errors.Add);
        await EditorMessage.DispatchAsync("bad", () => Current with { Closed = true }, Handle, errors.Add);
        await EditorMessage.DispatchAsync("""{"type":"future-command","version":"other"}""", () => Current, Handle, errors.Add);
        await EditorMessage.DispatchAsync("""{"type":"ready"}""", () => Current, _ => Task.FromCanceled(new CancellationToken(true)), errors.Add);
        Assert.Equal(0, calls); Assert.Empty(errors);
    }

    [Fact]
    public async Task Excessive_size_and_nesting_are_rejected_and_a_later_message_still_works()
    {
        var errors = new List<string>(); var calls = 0;
        Task Handle(EditorMessage _) { calls++; return Task.CompletedTask; }
        await EditorMessage.DispatchAsync(new string(' ', EditorMessage.MaxCharacters + 1), () => Current, Handle, errors.Add);
        await EditorMessage.DispatchAsync(new string('[', 18) + "0" + new string(']', 18), () => Current, Handle, errors.Add);
        await EditorMessage.DispatchAsync("""{"type":"refresh"}""", () => Current, Handle, errors.Add);
        Assert.Equal(2, errors.Count); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("""{"type":"focus-out","version":-1,"direction":1}""")]
    [InlineData("""{"type":"whitespace","version":-1,"value":1}""")]
    public async Task Initial_empty_editor_retains_focus_and_display_controls(string body)
    {
        var calls = 0; var errors = new List<string>();
        await EditorMessage.DispatchAsync(body, () => new(false, 0, 0, null), _ => { calls++; return Task.CompletedTask; }, errors.Add);
        Assert.Equal(1, calls); Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(7, 7)]
    public async Task Focus_can_leave_the_visible_editor_while_another_document_loads(long published, long version)
    {
        var calls = 0; var errors = new List<string>();
        await EditorMessage.DispatchAsync(JsonSerializer.Serialize(new { type = "focus-out", version, direction = 1 }),
            () => new(false, published, published + 1, "repo"), _ => { calls++; return Task.CompletedTask; }, errors.Add);
        Assert.Equal(1, calls); Assert.Empty(errors);
    }

    [Fact]
    public async Task Only_current_valid_file_inclusion_reaches_the_real_view_model_without_staging()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base\n"); repo.Commit("base"); repo.Write("a.txt", "working\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1); model.SelectedEntry = Assert.Single(model.Entries);
        for (var i = 0; model.CurrentDocument is null && i < 400; i++) await Task.Delay(10);
        Assert.NotNull(model.CurrentDocument);
        var version = model.DocumentVersion; var published = version - 1; var index = repo.Git("write-tree"); var errors = new List<string>();
        Task Handle(EditorMessage message) { model.ToggleIncludeFile("a.txt"); return Task.CompletedTask; }
        var body = JsonSerializer.Serialize(new { type = "include-file", version });
        await EditorMessage.DispatchAsync(body, () => new(false, published, model.DocumentVersion, model.Root), Handle, errors.Add);
        Assert.False(model.InclusionState("a.txt"));
        published = version;
        await EditorMessage.DispatchAsync(body, () => new(false, published, model.DocumentVersion, model.Root), Handle, errors.Add);
        Assert.True(model.InclusionState("a.txt")); Assert.Empty(errors); Assert.Equal(index, repo.Git("write-tree"));
        Assert.Equal("working\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }
}
