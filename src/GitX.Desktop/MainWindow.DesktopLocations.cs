using Avalonia.Controls;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly DesktopLocations _desktopLocations = new();
    private async Task OpenDesktopLocationAsync(string root, string relative, bool directory, DesktopLocationAction action, long generation)
    {
        bool Current() => !_closed && _model.Root == root && _model.RepositoryGeneration == generation;
        if (!Current()) return;
        try
        {
            var target = await Task.Run(() => DesktopLocations.Resolve(root, relative, directory));
            if (Current()) await _desktopLocations.OpenAsync(target, action);
        }
        catch (Exception error) { if (Current()) _model.ReportError("未能打开系统位置：" + error.Message); }
    }
    private Task OpenTabDesktopLocationAsync(string id, DesktopLocationAction action)
    {
        if (_model.Root is not { } root || _model.SourceTabs.FirstOrDefault(tab => tab.Id == id) is not { LocalFile: true } tab)
        { _model.ReportError("此标签不是当前工作树文件，未打开其他版本的同名路径。"); return Task.CompletedTask; }
        return OpenDesktopLocationAsync(root, tab.Path, false, action, _model.RepositoryGeneration);
    }
    private MenuFlyout? ShowProjectLocationMenu(Control anchor, FileNavigationRow target, bool atPointer)
    {
        if (_model.Root is not { } root || target.IsGroup || (!target.IsDirectory && !target.IsFile)) return null;
        var generation = _model.RepositoryGeneration; var menu = new MenuFlyout();
        if (target.Entry is { } entry)
        {
            var history = new MenuItem { Header = "文件历史", Icon = IconContent.Icon("HistoryIcon") };
            history.Click += async (_, _) => { menu.Hide(); if (!_closed && root == _model.Root && generation == _model.RepositoryGeneration) await OpenFileHistoryAsync(new(root, entry.Path)); };
            menu.Items.Add(history); menu.Items.Add(new Separator());
        }
        foreach (var (label, action, icon) in new[] { (DesktopLocations.RevealLabel, DesktopLocationAction.Reveal, "FolderIcon"), ("在终端中打开", DesktopLocationAction.Terminal, "OutputIcon") })
        {
            var item = new MenuItem { Header = label, Icon = IconContent.Icon(icon) };
            item.Click += async (_, _) => { menu.Hide(); await OpenDesktopLocationAsync(root, target.Path, target.IsDirectory, action, generation); };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor, atPointer);
        return menu;
    }
}
