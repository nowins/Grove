using System.Text.Json;

namespace GitX.Desktop.Services;

internal sealed record EditorMessageState(bool Closed, long PublishedVersion, long DocumentVersion, string? Root);

// The main editor's protocol boundary. Domain checks (anchors, selected tabs,
// repository tokens and write permissions) remain in their existing handlers.
internal sealed record EditorMessage(string Type, JsonElement Payload, long? Version)
{
    internal const int MaxCharacters = 128 * 1024;

    public static async Task DispatchAsync(string? body, Func<EditorMessageState> state, Func<EditorMessage, Task> handle, Action<string> report)
    {
        var origin = state();
        if (origin.Closed || body is null) return;
        try
        {
            var message = Read(body, origin);
            if (message is not null) await handle(message);
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { Report("编辑器消息格式无效。"); }
        catch (Exception ex) { Report("编辑器消息处理失败：" + ex.Message); }

        // A failed old operation must not replace the current document's error.
        void Report(string error) { if (state() == origin) report(error); }
    }

    private static EditorMessage? Read(string body, EditorMessageState state)
    {
        if (body.Length > MaxCharacters) throw new JsonException();
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Text(root, "type")) throw new JsonException();
        var type = root.GetProperty("type").GetString()!;
        if (type is not ("ready" or "loaded" or "refresh" or "comment" or "open" or "view" or "blame" or
            "blame-action" or "blame-view" or "source-tab" or "source-save" or "source-edit-state" or "editor-status" or "markdown-open" or "stage-selection" or "revert-block" or "include-line" or
            "include-file" or "include-deletion" or "caret" or "whitespace" or "close-document" or "hide-tool" or "reset-layout" or "focus-navigation" or "focus-out" or "recent-files" or "code-search" or "source-history" or "project-reveal" or "history-back" or "reading-history" or "continuous-diff" or "error")) return null;

        var needsVersion = type is "editor-status" or "continuous-diff" or "reading-history" or "source-save" or "source-edit-state" or "markdown-open" or "project-reveal" or "source-history" or "history-back" or "recent-files" or "code-search" or "loaded" or "blame" or "blame-action" or "blame-view" or "source-tab" or "stage-selection" or
            "revert-block" or "include-line" or "include-file" or "include-deletion" or "caret" or "whitespace" or "close-document" or "hide-tool" or "reset-layout" or "focus-navigation" or "focus-out"
            || type == "comment" && Choice(root, "action", "begin") || type == "comment" && Choice(root, "action", "open-source");
        long? version = null;
        if (needsVersion || type == "error" && root.TryGetProperty("version", out _))
        {
            if (!root.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < -1)
                throw new JsonException();
            version = number;
            // Leaving the visible embedded editor is valid while the next
            // document is loading. Native focus ownership is checked again.
            var initial = number == -1 && state.PublishedVersion == 0 &&
                (type == "focus-out" || state.DocumentVersion == 0 && (type is "whitespace" or "error"));
            if (!initial && (number < 0 || number != state.PublishedVersion || type != "focus-out" && number != state.DocumentVersion)) return null;
        }

        var valid = type switch
        {
            "view" => Integer(root, "index", 0, 2),
            "code-search" => Boolean(root, "filesOnly") && (!root.TryGetProperty("quick", out _) || Boolean(root, "quick")),
            "source-save" => Text(root, "id"),
            "source-edit-state" => Text(root, "id") && Boolean(root, "editable") && Boolean(root, "dirty"),
            "editor-status" => root.TryGetProperty("text", out var statusText) && statusText.ValueKind == JsonValueKind.String && statusText.GetString()!.Length <= 1024,
            "source-tab" => Choice(root, "action", "select", "close", "close-others", "close-all", "reveal", "terminal") && Text(root, "id"),
            "markdown-open" => Text(root, "href") && root.GetProperty("href").GetString()!.Length <= 4096,
            "blame-action" => Choice(root, "action", "close", "previous", "copy", "diff", "history") && Integer(root, "line", 1),
            "blame-view" => Choice(root, "field", "revision", "date", "author", "commitNumber") && Boolean(root, "value"),
            "stage-selection" => root.TryGetProperty("block", out var block) && (block.ValueKind == JsonValueKind.Null || Integer(root, "block", 0))
                && Integer(root, "side", 0, 1) && Range(root, "startLine", "endLine"),
            "revert-block" => Integer(root, "block", 0),
            "include-line" => Integer(root, "side", 0, 1) && Integer(root, "line", 1) && Boolean(root, "include"),
            "include-deletion" => Integer(root, "block", 0) && Boolean(root, "include"),
            "caret" => Integer(root, "side", 0, 1) && Integer(root, "line", 1) && Integer(root, "column", 1)
                && (!root.TryGetProperty("navigation", out _) || Boolean(root, "navigation")),
            "reading-history" => Integer(root, "direction", -1, 1) && root.GetProperty("direction").GetInt32() != 0,
            "continuous-diff" => Choice(root, "action", "open") && (!root.TryGetProperty("offset", out _) || Integer(root, "offset", 0)) || Choice(root, "action", "file") && Text(root, "id") &&
                root.GetProperty("id").GetString()!.Length == 32 && Integer(root, "index", 0, 199) && Integer(root, "side", 0, 1) && Integer(root, "line", 1),
            "whitespace" => Integer(root, "value", 0, 2),
            "focus-out" => Integer(root, "direction", -1, 1) && root.GetProperty("direction").GetInt32() != 0,
            "error" => Text(root, "message"),
            "comment" => Comment(root),
            _ => true
        };
        if (!valid) throw new JsonException();
        return new(type, root.Clone(), version);
    }

    private static bool Comment(JsonElement root)
    {
        if (!Text(root, "action")) return false;
        return root.GetProperty("action").GetString() switch
        {
            "list" or "cancel" => true,
            "begin" => Text(root, "snapshot") && Integer(root, "side", 0, 1) && Range(root, "start", "end"),
            "save" => Text(root, "id") && Text(root, "body", allowEmpty: true),
            "edit" or "delete" or "open-source" => Text(root, "repositoryId") && Text(root, "id") && Text(root, "revision"),
            "copy" or "export" => Text(root, "repositoryId"),
            _ => false
        };
    }

    private static bool Text(JsonElement root, string name, bool allowEmpty = false)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && (allowEmpty || value.GetString()!.Length > 0);
    private static bool Choice(JsonElement root, string name, params string[] choices)
        => Text(root, name) && choices.Contains(root.GetProperty(name).GetString(), StringComparer.Ordinal);
    private static bool Integer(JsonElement root, string name, int minimum, int maximum = int.MaxValue)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum;
    private static bool Boolean(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Range(JsonElement root, string start, string end)
        => Integer(root, start, 1) && Integer(root, end, 1) && root.GetProperty(end).GetInt32() >= root.GetProperty(start).GetInt32();
}
