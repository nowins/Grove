using Avalonia.Controls;

namespace GitX.Desktop.Services;

internal static class NativeWebViewCommands
{
    public static async Task InvokeCommandAsync(this NativeWebView view, string expression) =>
        _ = await view.InvokeScript(WebViewScript.Command(expression));

    public static async Task<string?> InvokeJsonQueryAsync(this NativeWebView view, string expression) =>
        WebViewScript.DecodeObject(await view.InvokeScript(WebViewScript.Json(expression)));
}
