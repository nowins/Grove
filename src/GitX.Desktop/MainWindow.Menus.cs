using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly HashSet<ShellCommandId> _queuedMenuCommands = [];
    private bool _applicationQuitQueued;

    private bool CanRunShellCommand(ShellCommandId id, Window owner)
    {
        if (_closed || !IsEnabled || !owner.IsEnabled || !owner.IsVisible) return false;
        var repository = _model.Root is not null;
        var ready = repository && !_model.IsWriting;
        return id switch
        {
            ShellCommandId.OpenRepository or ShellCommandId.RecentRepositories => !_model.IsWriting && _mergeWindow is null && !_commentBusy && _commentDraft is null,
            ShellCommandId.Branches or ShellCommandId.Sync or ShellCommandId.Stashes => ready,
            ShellCommandId.Amend => ready && !_model.HasOperation,
            ShellCommandId.Conflicts => ready && _model.HasOperation,
            ShellCommandId.DraftRecovery => !_model.IsWriting && !_textGuardPending && !_draftDialogOpen,
            ShellCommandId.SaveFile => ReferenceEquals(owner, this) && CanSaveText && _fileHistoryPanel?.IsKeyboardFocusWithin != true,
            ShellCommandId.Stage => _model.CanStageFile,
            ShellCommandId.Unstage => _model.CanUnstageFile,
            ShellCommandId.RestoreDiscard => _model.CanRestoreDiscard,
            ShellCommandId.RevealFile => _model.CurrentProjectFile is not null,
            ShellCommandId.GoToLine => _ready && _webView is not null && _model.Mode == 0 && _model.CurrentDocument is { IsNotice: false } && _loadedEditorVersion == _model.DocumentVersion,
            ShellCommandId.ContinuousDiff => ReferenceEquals(owner, this) && _webView is not null && _model.CanOpenContinuousDiff,
            ShellCommandId.ReadBack => ReferenceEquals(owner, this) && !_model.IsWriting && !ReadingCompositionActive() && _fileHistoryPanel?.IsKeyboardFocusWithin != true && _model.CanReadBack,
            ShellCommandId.ReadForward => ReferenceEquals(owner, this) && !_model.IsWriting && !ReadingCompositionActive() && _fileHistoryPanel?.IsKeyboardFocusWithin != true && _model.CanReadForward,
            ShellCommandId.Structure => _model.Mode == 0 && _model.CurrentDocument is { IsNotice: false } document && SourceLanguage.SupportsStructure(document.Language),
            ShellCommandId.FileHistory => _model.Mode == 0 && _model.CurrentDocument is { IsNotice: false },
            ShellCommandId.HistoryBranches or ShellCommandId.HistoryFiles => _bottomOpen,
            ShellCommandId.Minimize => owner.CanMinimize && owner.WindowState != WindowState.Minimized,
            ShellCommandId.Zoom => owner.CanMaximize,
            ShellCommandId.Worktrees or ShellCommandId.RepositoryDetails or ShellCommandId.RevealRepository or
                ShellCommandId.ProjectFilter or ShellCommandId.QuickSearch or ShellCommandId.RecentFiles or
                ShellCommandId.FindFile or ShellCommandId.FindText or ShellCommandId.Notes => repository,
            _ => true
        };
    }

    private void RunShellCommand(ShellCommandId id, Window owner)
    {
        if (!CanRunShellCommand(id, owner)) return;
        CloseShellPopup();
        switch (id)
        {
            case ShellCommandId.DraftRecovery: _ = ShowDraftRecoveryAsync(); break;
            case ShellCommandId.OpenRepository: _ = PickRepositoryAsync(); break;
            case ShellCommandId.RecentRepositories: RepositoriesClicked(RepositoryButton, new RoutedEventArgs()); break;
            case ShellCommandId.Worktrees: _ = OpenWorktreesAsync(); break;
            case ShellCommandId.RepositoryDetails: ShowRepositoryDetails(); break;
            case ShellCommandId.RevealRepository: RevealRepository(); break;
            case ShellCommandId.CloseWindow: owner.Close(); break;
            case ShellCommandId.ProjectTool: OpenTool(0, true, true); break;
            case ShellCommandId.CommitTool: OpenTool(1, true, true); break;
            case ShellCommandId.GitTool: OpenTool(2, true, true); break;
            case ShellCommandId.Output: ShowOutput(); break;
            case ShellCommandId.RevealFile: RevealProjectFile(); break;
            case ShellCommandId.ProjectFilter: ToggleProjectFilter(); break;
            case ShellCommandId.ResetLayout: if (_dock is not null) _ = _dock.ResetAsync(); break;
            case ShellCommandId.Refresh: _ = RefreshCurrentAsync(); break;
            case ShellCommandId.HistoryBranches: HistoryBranchesToggled(null, new RoutedEventArgs()); break;
            case ShellCommandId.HistoryFiles: HistoryFilesToggled(null, new RoutedEventArgs()); break;
            case ShellCommandId.QuickSearch: _ = OpenCodeSearchAsync(true, quick: true); break;
            case ShellCommandId.RecentFiles: _ = OpenRecentFilesAsync(); break;
            case ShellCommandId.FindFile: _ = OpenCodeSearchAsync(true); break;
            case ShellCommandId.FindText: _ = OpenCodeSearchAsync(false); break;
            case ShellCommandId.GoToLine: _webView?.Focus(); _ = InvokeEditorAsync("window.gitx.goToLine()"); break;
            case ShellCommandId.ContinuousDiff: _webView?.Focus(); _ = _model.OpenContinuousDiffAsync(); break;
            case ShellCommandId.ReadBack: _ = NavigateReadingAsync(-1); break;
            case ShellCommandId.ReadForward: _ = NavigateReadingAsync(1); break;
            case ShellCommandId.Structure: _ = OpenSourceSymbolsAsync(); break;
            case ShellCommandId.Notes: _ = OpenLocalNotesAsync(); break;
            case ShellCommandId.FileHistory: _ = OpenSourceHistoryAsync(_model.DocumentVersion); break;
            case ShellCommandId.Branches: BranchesClicked(BranchButton, new RoutedEventArgs()); break;
            case ShellCommandId.Sync: _ = OpenRemoteMenuAsync(RemoteButton); break;
            case ShellCommandId.Stashes: _ = OpenStashesAsync(); break;
            case ShellCommandId.SaveFile: _ = SaveTextAsync(); break;
            case ShellCommandId.Stage: StageClicked(null, new RoutedEventArgs()); break;
            case ShellCommandId.Unstage: UnstageClicked(null, new RoutedEventArgs()); break;
            case ShellCommandId.RestoreDiscard: RestoreDiscardClicked(null, new RoutedEventArgs()); break;
            case ShellCommandId.Amend: _ = AmendAsync(); break;
            case ShellCommandId.Conflicts: ShowConflictsClicked(null, new RoutedEventArgs()); break;
            case ShellCommandId.Shortcuts: ShowShellText("快捷键", ShellShortcutHelp()); break;
            case ShellCommandId.Updates: ShowUpdates(); break;
            case ShellCommandId.About: ShowAbout(); break;
            case ShellCommandId.Minimize: if (owner.CanMinimize) owner.WindowState = WindowState.Minimized; break;
            case ShellCommandId.Zoom: if (owner.CanMaximize) owner.WindowState = owner.WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal; break;
        }
    }

    private string ShellShortcutHelp()
    {
        var mac = OperatingSystem.IsMacOS();
        var commands = ShellCommands.All.Where(c => c.Gesture(mac) is not null)
            .Select(c => c.Gesture(mac)!.Replace("Meta", "Command").Replace("D1", "1").Replace("D2", "2").Replace("D3", "3") + "  " + c.Header);
        return string.Join("\n", commands) + "\n双击 Shift  快速搜索\nShift+Esc  隐藏当前工具\nF7 / Shift+F7  下一处 / 上一处差异\nEsc  返回文件列表" +
            (mac ? "\nCtrl+G  跳转到行（另支持 Command+L）\nCommand+Q  退出 Grove（保留退出保护）" : "\nAlt+M  主菜单\nCtrl+F（项目树）  筛选项目文件");
    }

    private void InitializePlatformMenus()
    {
        if (!OperatingSystem.IsMacOS()) return;
        MainMenuButton.IsVisible = false;
        AttachNativeWindowMenu(this);
        if (_dock is not null) _dock.FloatingCreated += AttachNativeWindowMenu;
        if (Application.Current is not { } app) return;
        // Mutate the existing application menu: replacing it discards Avalonia's
        // Services/Hide/Quit callbacks. Its Quit callback uses TryShutdown, which
        // runs all existing Closing guards rather than forcing process exit.
        var menu = NativeMenu.GetMenu(app);
        if (menu is null) { menu = new NativeMenu(); NativeMenu.SetMenu(app, menu); }
        foreach (var item in menu.Items.OfType<NativeMenuItem>().Where(i => i.Header == "About Avalonia").ToArray()) menu.Items.Remove(item);
        var about = ShellCommands.All.Single(c => c.Id == ShellCommandId.About);
        var updates = ShellCommands.All.Single(c => c.Id == ShellCommandId.Updates);
        menu.Items.Insert(0, NativeCommand(about, this));
        menu.Items.Insert(1, NativeCommand(updates, this));
        void LocalizeStandardItems()
        {
            foreach (var item in menu.Items.OfType<NativeMenuItem>())
            {
                switch (item.Header)
                {
                    case "Services": item.Header = "服务"; break;
                    case "Hide Grove": item.Header = "隐藏 Grove"; break;
                    case "Hide Others": item.Header = "隐藏其他"; item.Gesture = KeyGesture.Parse("Meta+Alt+H"); break;
                    case "Show All": item.Header = "显示全部"; break;
                    case "Quit": item.Header = "退出 Grove"; break;
                }
            }
        }
        menu.NeedsUpdate += (_, _) =>
        {
            LocalizeStandardItems();
            foreach (var command in menu.Items.OfType<NativeMenuItem>().Select(i => i.Command).OfType<ShellMenuCommand>()) command.Refresh();
        };
        LocalizeStandardItems();
    }

    private void AttachNativeWindowMenu(Window owner)
    {
        owner.AddHandler(KeyDownEvent, RouteMacMenuShortcut, RoutingStrategies.Bubble);
        owner.Closed += (_, _) => owner.RemoveHandler(KeyDownEvent, RouteMacMenuShortcut);
        var root = new NativeMenu();
        foreach (var group in ShellCommands.WindowMenu(true).GroupBy(c => c.Group))
        {
            var submenu = new NativeMenu();
            foreach (var command in group)
            {
                if (command.SeparatorBefore && submenu.Items.Count > 0) submenu.Items.Add(new NativeMenuItemSeparator());
                submenu.Items.Add(NativeCommand(command, owner));
            }
            submenu.NeedsUpdate += (_, _) =>
            {
                foreach (var command in submenu.Items.OfType<NativeMenuItem>().Select(i => i.Command).OfType<ShellMenuCommand>()) command.Refresh();
            };
            root.Items.Add(new NativeMenuItem(group.Key) { Menu = submenu });
        }
        NativeMenu.SetMenu(owner, root);
    }

    private void RouteMacMenuShortcut(object? sender, KeyEventArgs e)
    {
        // AppKit gets the first opportunity. Embedded native views may instead
        // forward an unhandled menu key into Avalonia. Reuse the very same
        // commands, enablement, context checks and queue as the native menu.
        if (!OperatingSystem.IsMacOS() || e.Handled || _closed || sender is not Window owner) return;
        if ((e.KeyModifiers & KeyModifiers.Meta) != 0)
        {
            var item = NativeMenu.GetMenu(owner)?.Items.OfType<NativeMenuItem>()
                .SelectMany(group => group.Menu?.Items.OfType<NativeMenuItem>() ?? [])
                .FirstOrDefault(action => action.Gesture?.Matches(e) == true);
            if (item is not null)
            {
                e.Handled = true;
                if (item.Command is { } command && command.CanExecute(null)) command.Execute(null);
                return;
            }
        }
        // Application Quit is supplied by the framework's application menu.
        // Its fallback uses the same guarded action; never force shutdown.
        if (e.Key != Key.Q || e.KeyModifiers != KeyModifiers.Meta ||
            Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime) return;
        e.Handled = true;
        if (_applicationQuitQueued) return;
        _applicationQuitQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _applicationQuitQueued = false;
            if (!_closed) lifetime.TryShutdown();
        }, DispatcherPriority.Background);
    }

    private NativeMenuItem NativeCommand(ShellCommandDefinition definition, Window owner)
    {
        var command = new ShellMenuCommand(this, owner, definition.Id);
        var item = new NativeMenuItem(definition.Header) { Command = command };
        if (definition.MacGesture is { } gesture) item.Gesture = KeyGesture.Parse(gesture);
        // NeedsUpdate runs before both opening and native key-equivalent lookup.
        // No Click handler: the native exporter invokes Click AND Command.
        owner.PropertyChanged += Update;
        _model.PropertyChanged += ModelUpdate;
        owner.Closed += (_, _) => { owner.PropertyChanged -= Update; _model.PropertyChanged -= ModelUpdate; };
        void Update(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == IsEnabledProperty || args.Property == IsVisibleProperty || args.Property == WindowStateProperty) command.Refresh();
        }
        void ModelUpdate(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => command.Refresh();
        return item;
    }

    private void RefreshSaveMenuState()
    {
        // Editor session events do not raise a view-model property change.
        // Push enablement to AppKit too, before its key-equivalent lookup.
        foreach (var command in NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>()
            .SelectMany(group => group.Menu?.Items.OfType<NativeMenuItem>() ?? [])
            .Select(item => item.Command).OfType<ShellMenuCommand>() ?? [])
            if (command.Id == ShellCommandId.SaveFile) command.Refresh();
    }

    private sealed class ShellMenuCommand(MainWindow main, Window owner, ShellCommandId id) : ICommand
    {
        public ShellCommandId Id => id;
        public bool CanExecute(object? parameter) => main.CanRunShellCommand(id, owner);
        public event EventHandler? CanExecuteChanged;
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        public void Execute(object? parameter)
        {
            if (!CanExecute(null) || !main._queuedMenuCommands.Add(id)) return;
            var root = main._model.Root; var version = main._model.DocumentVersion;
            Dispatcher.UIThread.Post(() =>
            {
                main._queuedMenuCommands.Remove(id);
                // A stale native menu must not act on a different repository or document.
                var documentCommand = id is ShellCommandId.SaveFile or ShellCommandId.Stage or ShellCommandId.Unstage or ShellCommandId.RevealFile or ShellCommandId.Structure or ShellCommandId.FileHistory or ShellCommandId.GoToLine;
                var globalCommand = id is ShellCommandId.About or ShellCommandId.Updates or ShellCommandId.Shortcuts or ShellCommandId.CloseWindow or ShellCommandId.Minimize or ShellCommandId.Zoom or ShellCommandId.OpenRepository or ShellCommandId.RecentRepositories;
                if (!CanExecute(null) || !globalCommand && root != main._model.Root || documentCommand && version != main._model.DocumentVersion) return;
                main.RunShellCommand(id, owner);
            }, DispatcherPriority.Background);
        }
    }
}
