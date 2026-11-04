using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop;
using GitX.Desktop.ViewModels;

internal static partial class Program
{
    private static string _fixtureRoot = "", _fixtureRepository = "", _linkedRepository = "", _oldRevision = "";

    private static string FixtureGit(string repository, params string[] args)
    {
        var boundary = Path.GetFullPath(_fixtureRoot) + Path.DirectorySeparatorChar;
        Check(Path.GetFullPath(repository).StartsWith(boundary, StringComparison.Ordinal), "Git probe escaped its generated fixture.");
        var info = new ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Check(process.ExitCode == 0, "Generated fixture Git failed: " + string.Join(' ', args) + ": " + error);
        return output.Trim();
    }
    private static void PrepareNavigationFixture()
    {
        FixtureGit(_fixtureRepository, "init", "-b", "main");
        FixtureGit(_fixtureRepository, "config", "user.name", "Grove probe");
        FixtureGit(_fixtureRepository, "config", "user.email", "fixture@example.invalid");
        FixtureGit(_fixtureRepository, "config", "commit.gpgsign", "false");
        static string Lines(int count) => string.Join('\n', Enumerable.Range(1, count).Select(i => "// generated line " + i));
        File.WriteAllText(Path.Combine(_fixtureRepository, "sample.cs"), Lines(7));
        File.WriteAllText(Path.Combine(_fixtureRepository, "other.ts"), "export const fixture = 42;\n");
        FixtureGit(_fixtureRepository, "add", "--", "sample.cs", "other.ts");
        FixtureGit(_fixtureRepository, "commit", "-m", "Generated seven-line fixture");
        _oldRevision = FixtureGit(_fixtureRepository, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_fixtureRepository, "sample.cs"), Lines(36));
        FixtureGit(_fixtureRepository, "add", "--", "sample.cs");
        FixtureGit(_fixtureRepository, "commit", "-m", "Generated current source");
        FixtureGit(_fixtureRepository, "update-ref", "refs/remotes/origin/main", "HEAD");
        FixtureGit(_fixtureRepository, "tag", "main");
        FixtureGit(_fixtureRepository, "worktree", "add", "-b", "probe-linked", _linkedRepository, "HEAD");
    }
    private static string FixtureState(string root) => string.Join('|',
        FixtureGit(root, "rev-parse", "HEAD"), FixtureGit(root, "symbolic-ref", "HEAD"),
        FixtureGit(root, "write-tree"), FixtureGit(root, "status", "--porcelain=v1", "--untracked-files=all"),
        FixtureGit(root, "hash-object", "sample.cs"));

    private static async Task VerifyNativeNavigationAsync(MainWindow main, MainViewModel model, nint window, nint view)
    {
        var web = Private<NativeWebView>(main, "_webView");
        var before = FixtureState(_fixtureRepository); var linkedBefore = FixtureState(_linkedRepository);
        try
        {
            await model.OpenEditorEntryAsync(new("sample.cs", "sample.cs", ComparisonKind.WorkingFile));
            await NavigationReady(main, model);
            foreach (var width in new[] { 1380d, 880d })
            {
                main.Width = width; await Task.Delay(150); main.UpdateLayout();
                await FocusSource(main, web, window);
                var focus = ReadFocus(window);
                Check(TestKey(window, "l", 37, 1) == 0, "Cannot queue native Command+L.");
                await UntilAsync(async () => (await LineState(web)).GetProperty("open").GetBoolean());
                var state = await LineState(web);
                Check(state.GetProperty("focused").GetBoolean() && state.GetProperty("range").GetString()!.Contains("1–36"), "Command+L did not focus the current-source line input.");
                Check(state.GetProperty("dialogWidth").GetDouble() <= 360.5, "Line dialog lost its compact width.");
                var initial = state.GetProperty("line").GetInt32();
                await LineInput(web, "99999"); QueueKey("\r", 36);
                await UntilAsync(async () => (await LineState(web)).GetProperty("invalid").GetBoolean());
                Check((await LineState(web)).GetProperty("line").GetInt32() == initial, "Invalid line moved the caret.");
                QueueKey("\x1b", 53);
                await UntilAsync(async () => !(await LineState(web)).GetProperty("open").GetBoolean());
                Check((await LineState(web)).GetProperty("editorFocused").GetBoolean(), "Escape did not restore source focus.");
                await FocusSource(main, web, window);
                Check(TestKey(window, "g", 5, 2) == 0, "Cannot queue explicit Control+G on macOS.");
                await UntilAsync(async () => (await LineState(web)).GetProperty("open").GetBoolean());
                await LineInput(web, "36"); QueueKey("\r", 36);
                await UntilAsync(async () => (await LineState(web)).GetProperty("line").GetInt32() == 36);
                Check(!(await LineState(web)).GetProperty("open").GetBoolean(), "Enter left the line dialog open.");
                Samples.Add(new { phase = "native-line-navigation", width, focus, commandL = true, controlG = true, invalidRejected = true, escapeRestoredFocus = true, lastLine = 36, textEntry = "programmatic DOM value; native NSEvent Enter/Escape and shortcuts" });
            }
            await model.OpenRevisionSourceAsync("sample.cs", _oldRevision); await NavigationReady(main, model);
            await FocusSource(main, web, window); Check(TestKey(window, "l", 37, 1) == 0, "Cannot queue historical Command+L.");
            await UntilAsync(async () => (await LineState(web)).GetProperty("open").GetBoolean());
            Check((await LineState(web)).GetProperty("range").GetString()!.Contains("1–7"), "Historical source used the worktree line count.");
            await LineInput(web, "8"); QueueKey("\r", 36);
            await UntilAsync(async () => (await LineState(web)).GetProperty("invalid").GetBoolean());
            await LineInput(web, "7"); QueueKey("\r", 36);
            await UntilAsync(async () => (await LineState(web)).GetProperty("line").GetInt32() == 7);
            await FocusSource(main, web, window); Check(TestKey(window, "l", 37, 1) == 0, "Cannot queue stale-document Command+L.");
            await UntilAsync(async () => (await LineState(web)).GetProperty("open").GetBoolean());
            await model.OpenEditorEntryAsync(new("other.ts", "other.ts", ComparisonKind.WorkingFile)); await NavigationReady(main, model);
            Check(!(await LineState(web)).GetProperty("open").GetBoolean(), "A changed document retained the old line request.");
            Samples.Add(new { phase = "native-line-history-and-document-guard", historicalLines = 7, currentLines = 36, staleRequestClosed = true });

            RunProbeCommand(main, "GitTool"); await Task.Delay(150); main.UpdateLayout();
            var popup = await OpenHistoryPicker(main, window, view);
            Check(HasHistoryGroup(popup.List, "本地分支") && HasHistoryGroup(popup.List, "远端分支") && HasHistoryGroup(popup.List, "标签"), "History kinds are not separated.");
            foreach (var key in new[] { "all", "HEAD", "refs/heads/main", "refs/remotes/origin/main", "refs/tags/main" })
                Check(popup.List.Items.Cast<object>().Any(r => RowProperty<string>(r, "Key") == key), "History identity missing: " + key);
            // Search text is set programmatically. The focus transition, Space,
            // Enter and Escape travel through the real owned AppKit key window.
            popup.Search.Text = "origin main"; await Task.Delay(100);
            Check(popup.List.Items.Cast<object>().Count(r => !RowProperty<bool>(r, "IsHeading")) == 1, "Expected a single canonical remote result.");
            Check(popup.Search.IsKeyboardFocusWithin, "Opening/searching did not retain search focus.");
            QueueKey("\uf701", 125); await UntilAsync(() => popup.List.IsKeyboardFocusWithin);
            QueueKey(" ", 49); await UntilAsync(() => HasHistoryGroup(popup.List, "收藏"));
            Check(model.HistoryReferenceKey is null && Popup(main)?.IsOpen == true, "Favorite changed the history scope or dismissed the picker.");
            await UntilAsync(() => popup.List.IsKeyboardFocusWithin);
            QueueKey("\r", 36); await UntilAsync(() => model.HistoryReferenceKey == "refs/remotes/origin/main");
            await UntilAsync(() => SavedHistoryRecent("refs/remotes/origin/main"));
            popup = await OpenHistoryPicker(main, window, view);
            Check(HasHistoryGroup(popup.List, "最近使用") && HasHistoryGroup(popup.List, "收藏"), "History preferences did not survive reopening.");
            QueueKey("\x1b", 53); await UntilAsync(() => Popup(main) is null);
            Check(model.HistoryReferenceKey == "refs/remotes/origin/main", "Escape changed the history scope.");
            Samples.Add(new { phase = "native-history-favorite-recent", canonicalLocalRemoteTag = true, singleMatchDownSpace = true, persisted = true, cancelledWithoutScopeChange = true });

            var linkedRoot = await RepositoryReader.ResolveWorkingDirectoryAsync(_linkedRepository);
            await model.OpenAsync(_linkedRepository); await UntilAsync(() => !model.Busy && model.Root == linkedRoot);
            RunProbeCommand(main, "GitTool"); await Task.Delay(150); main.UpdateLayout();
            popup = await OpenHistoryPicker(main, window, view);
            Check(!HasHistoryGroup(popup.List, "收藏") && !HasHistoryGroup(popup.List, "最近使用"), "Worktree preferences leaked across working directories.");
            var stale = popup.List.Items.Cast<object>().First(r => RowProperty<string>(r, "Key") == "refs/remotes/origin/main");
            FixtureGit(_fixtureRepository, "update-ref", "-d", "refs/remotes/origin/main");
            await model.RefreshAsync(); await UntilAsync(() => model.HistoryReferenceCandidates.All(r => r.CanonicalName != "refs/remotes/origin/main"));
            popup.List.SelectedItem = stale;
            QueueKey("\r", 36); await Task.Delay(150);
            Check(model.HistoryReferenceKey is null && Popup(main)?.IsOpen == true, "A deleted reference was applied or dismissed the picker.");
            Check(!popup.List.Items.Cast<object>().Any(r => RowProperty<string>(r, "Key") == "refs/remotes/origin/main"), "A deleted reference remained offered.");
            QueueKey("\x1b", 53); await UntilAsync(() => Popup(main) is null);
            Check(FixtureState(_fixtureRepository) == before && FixtureState(_linkedRepository) == linkedBefore, "Navigation changed fixture HEAD, branch, index or source files.");
            Samples.Add(new { phase = "native-history-worktree-and-stale-ref", preferencesIsolated = true, staleRefRejected = true, headIndexAndFilesUnchanged = true });
        }
        catch
        {
            Samples.Add(new { phase = "native-navigation-failure", model.Error, model.Root, model.Mode, model.DocumentVersion, hostLoaded = Private<long>(main, "_loadedEditorVersion"), historyScope = model.HistoryReferenceKey, focus = ReadFocus(window), popupOpen = Popup(main)?.IsOpen, line = await LineState(web) });
            throw;
        }
    }
    private static async Task FocusSource(MainWindow main, NativeWebView web, nint window)
    {
        Check(ActivateTestWindow(window) == 0, "Cannot activate owned navigation window."); await UntilAsync(() => main.IsActive);
        web.Focus(); await NavigationJS(web, "window.gitx.focus()");
        Check(FocusTestWebView(window) == 0, "Cannot focus owned WKWebView for navigation.");
        await UntilAsync(() => ReadFocus(window).GetProperty("firstResponder").GetString() == "ManagedWKWebView");
    }
    private static async Task NavigationReady(MainWindow main, MainViewModel model)
        => await UntilAsync(() => !model.Busy && model.CurrentDocument is { IsNotice: false } && Private<long>(main, "_loadedEditorVersion") == model.DocumentVersion);
    private static async Task NavigationJS(NativeWebView web, string js) => await web.InvokeScript("window.groveNative.command(() => (" + js + "))");
    private static async Task<JsonElement> LineState(NativeWebView web)
    {
        var result = await web.InvokeScript("window.groveNative.json(() => ({open:!document.querySelector('.goto-line-overlay').hidden,focused:document.activeElement===document.querySelector('.goto-line-dialog input'),editorFocused:!!document.activeElement.closest?.('.monaco-editor'),invalid:document.querySelector('.goto-line-dialog input').getAttribute('aria-invalid')==='true',dialogWidth:document.querySelector('.goto-line-dialog').getBoundingClientRect().width,range:document.querySelector('#goto-line-status').textContent,line:window.gitx.captureDockView().views[1]?.cursorState?.[0]?.position.lineNumber??0}))");
        using var outer = JsonDocument.Parse(result!);
        using var value = JsonDocument.Parse(outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString()! : result!);
        return value.RootElement.Clone();
    }
    private static Task LineInput(NativeWebView web, string value) => NavigationJS(web, "(()=>{const i=document.querySelector('.goto-line-dialog input');i.value=" + JsonSerializer.Serialize(value) + ";i.dispatchEvent(new Event('input',{bubbles:true}));})()");
    private static async Task UntilAsync(Func<Task<bool>> condition)
    { for (var i = 0; i < 160; i++) { if (await condition()) return; await Task.Delay(50); } throw new TimeoutException("Native navigation state did not complete."); }
    private static void QueueKey(string key, ushort code)
    {
        var own = OwnedKeyWindow(); Check(own != 0 && TestKey(own, key, code, 0) == 0, "Cannot queue a key in the owned active key window.");
    }
    private static async Task<(Control Panel, ListBox List, TextBox Search)> OpenHistoryPicker(MainWindow main, nint window, nint view)
    {
        Check(ActivateTestWindow(window) == 0, "Cannot activate history fixture."); await UntilAsync(() => main.IsActive);
        var anchor = main.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "历史分支筛选");
        var origin = anchor.TranslatePoint(default, main)!.Value;
        Check(ClickHeader(window, view, origin.X + anchor.Bounds.Width / 2, origin.Y + anchor.Bounds.Height / 2) == 0, "Native history filter pointer failed.");
        await UntilAsync(() => Popup(main) is { IsOpen: true });
        var panel = (Control)Popup(main)!.Content!;
        var list = panel.GetVisualDescendants().OfType<ListBox>().Single(); var search = panel.GetVisualDescendants().OfType<TextBox>().Single();
        await UntilAsync(() => search.IsKeyboardFocusWithin);
        return (panel, list, search);
    }
    private static Flyout? Popup(MainWindow main) => Private<PopupFlyoutBase?>(main, "_shellPopup") as Flyout;
    private static bool HasHistoryGroup(ListBox list, string group) => list.Items.Cast<object>().Any(r => RowProperty<bool>(r, "IsHeading") && RowProperty<string>(r, "Name") == group);
    private static T RowProperty<T>(object row, string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;
    private static T Private<T>(object row, string name) => (T)row.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!;
    private static void RunProbeCommand(MainWindow main, string name)
    {
        var method = typeof(MainWindow).GetMethod("RunShellCommand", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(main, [Enum.Parse(method.GetParameters()[0].ParameterType, name), main]);
    }
    private static bool SavedHistoryRecent(string key)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("GITX_DATA_DIR")!, "history-ref-preferences");
        if (!Directory.Exists(dir)) return false;
        return Directory.GetFiles(dir, "*.json").Any(file =>
        {
            using var value = JsonDocument.Parse(File.ReadAllText(file));
            return value.RootElement.GetProperty("Recent").EnumerateArray().Any(r => r.GetString() == key);
        });
    }
    [DllImport("grove-chrome-probe", EntryPoint = "grove_test_key")] private static extern int TestKey(nint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, ushort code, uint modifiers);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_owned_key_window")] private static extern nint OwnedKeyWindow();
}
