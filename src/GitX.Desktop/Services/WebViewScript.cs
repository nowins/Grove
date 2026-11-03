using System.Text.Json;
using System.Text;

namespace GitX.Desktop.Services;

// Command expressions are trusted host code; arguments are JSON-serialized at
// call sites. Commands never expose Promise/DOM/object results to native APIs.
internal static class WebViewScript
{
    public static string Command(string expression) => $"window.groveNative.command(() => ({expression}))";
    public static string Json(string expression) => $"window.groveNative.json(() => ({expression}))";

    // WebView2 can return a JSON-encoded JS string; WKWebView can return the
    // string itself. Accept either representation, but only a bounded object.
    public static string? DecodeObject(string? result)
    {
        if (string.IsNullOrWhiteSpace(result) || result.Length > 1024 * 1024) return null;
        try
        {
            using var outer = JsonDocument.Parse(result, new JsonDocumentOptions { MaxDepth = 32 });
            if (outer.RootElement.ValueKind == JsonValueKind.Object) return SafeJson(outer.RootElement);
            if (outer.RootElement.ValueKind != JsonValueKind.String) return null;
            var value = outer.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            using var inner = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            return inner.RootElement.ValueKind == JsonValueKind.Object ? SafeJson(inner.RootElement) : null;
        }
        catch (JsonException) { return null; }
    }

    private static string SafeJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) element.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
