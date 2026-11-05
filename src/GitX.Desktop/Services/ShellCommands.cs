namespace GitX.Desktop.Services;

internal enum ShellCommandId
{
    OpenRepository, RecentRepositories, Worktrees, RepositoryDetails, RevealRepository, CloseWindow,
    ProjectTool, CommitTool, GitTool, Output, RevealFile, ProjectFilter, ResetLayout, Refresh,
    HistoryBranches, HistoryFiles, QuickSearch, RecentFiles, FindFile, FindText, Structure, Notes, FileHistory,
    Branches, Sync, Stashes, Stage, Unstage, RestoreDiscard, Amend, Conflicts,
    Shortcuts, Updates, About, Minimize, Zoom, GoToLine, SaveFile, DraftRecovery
}

internal sealed record ShellCommandDefinition(ShellCommandId Id, string Group, string Header,
    string? Icon = null, string? WindowsGesture = null, string? MacGesture = null, bool SeparatorBefore = false)
{
    public string? Gesture(bool mac) => mac ? MacGesture : WindowsGesture;
}

// Both menus use these identities, labels and gestures. Enablement and execution
// live in one binding too; a displayed gesture is never a second event handler.
internal static class ShellCommands
{
    public static readonly IReadOnlyList<ShellCommandDefinition> All = [
        new(ShellCommandId.OpenRepository, "文件", "打开本地仓库…", "FolderIcon", "Ctrl+O", "Meta+O"),
        new(ShellCommandId.RecentRepositories, "文件", "切换最近项目…", "FolderIcon"),
        new(ShellCommandId.Worktrees, "文件", "工作树 · Worktrees", "BranchIcon"),
        new(ShellCommandId.RepositoryDetails, "文件", "仓库详情…", SeparatorBefore: true),
        new(ShellCommandId.RevealRepository, "文件", "在文件管理器中打开", "FolderIcon"),
        new(ShellCommandId.SaveFile, "文件", "保存当前文件", "EditIcon", "Ctrl+S", "Meta+S"),
        new(ShellCommandId.DraftRecovery, "文件", "本地草稿恢复…"),
        new(ShellCommandId.CloseWindow, "文件", "关闭窗口", MacGesture: "Meta+W", SeparatorBefore: true),
        new(ShellCommandId.ProjectTool, "视图", "项目工具窗口", "FolderIcon", "Ctrl+D1", "Meta+D1"),
        new(ShellCommandId.CommitTool, "视图", "提交工具窗口", "ChangesIcon", "Ctrl+D2", "Meta+D2"),
        new(ShellCommandId.GitTool, "视图", "Git 工具窗口", "HistoryIcon", "Ctrl+D3", "Meta+D3"),
        new(ShellCommandId.Output, "视图", "Git 输出", "OutputIcon"),
        new(ShellCommandId.RevealFile, "视图", "定位当前文件", "RevealIcon", "Ctrl+Shift+E", "Meta+Shift+E"),
        new(ShellCommandId.ProjectFilter, "视图", "筛选项目文件（项目树）", "FilterIcon", MacGesture: "Meta+Alt+F"),
        new(ShellCommandId.ResetLayout, "视图", "恢复默认布局", "PanelIcon", "Shift+F12", "Shift+F12"),
        new(ShellCommandId.Refresh, "视图", "刷新", "RefreshIcon", "F5", "F5", true),
        new(ShellCommandId.HistoryBranches, "视图", "显示 / 隐藏分支栏", "BranchIcon"),
        new(ShellCommandId.HistoryFiles, "视图", "显示 / 隐藏提交文件与详情栏", "PanelIcon"),
        new(ShellCommandId.QuickSearch, "搜索", "快速搜索…（双击 Shift）", "SearchIcon"),
        new(ShellCommandId.RecentFiles, "搜索", "最近文件…", "HistoryIcon", "Ctrl+E", "Meta+E"),
        new(ShellCommandId.FindFile, "搜索", "查找文件…", "FileIcon", "Ctrl+Shift+N", "Meta+Shift+O"),
        new(ShellCommandId.FindText, "搜索", "跨文件搜索内容…", "SearchIcon", "Ctrl+Shift+F", "Meta+Shift+F"),
        new(ShellCommandId.GoToLine, "搜索", "跳转到行…", "RevealIcon", "Ctrl+G", "Meta+L"),
        new(ShellCommandId.Structure, "搜索", "当前文件结构…", "FileIcon", "Ctrl+F12", "Meta+F12"),
        new(ShellCommandId.Notes, "搜索", "本地代码笔记…", "CommentIcon"),
        new(ShellCommandId.FileHistory, "搜索", "当前文件历史…", "HistoryIcon"),
        new(ShellCommandId.Branches, "Git", "分支…", "BranchIcon"),
        new(ShellCommandId.Sync, "Git", "同步 · Fetch / Pull / Push", "RemoteIcon"),
        new(ShellCommandId.Stashes, "Git", "Stash…", "StashIcon"),
        new(ShellCommandId.Stage, "Git", "暂存当前文件", "PlusIcon", SeparatorBefore: true),
        new(ShellCommandId.Unstage, "Git", "取消暂存当前文件", "MinusIcon"),
        new(ShellCommandId.RestoreDiscard, "Git", "恢复已丢弃内容…", "UndoIcon"),
        new(ShellCommandId.Amend, "Git", "修改上次提交（Amend）…", "EditIcon"),
        new(ShellCommandId.Conflicts, "Git", "冲突文件…", "MergeIcon", SeparatorBefore: true),
        new(ShellCommandId.Minimize, "窗口", "最小化", MacGesture: "Meta+M"),
        new(ShellCommandId.Zoom, "窗口", "缩放"),
        new(ShellCommandId.Shortcuts, "帮助", "快捷键…"),
        new(ShellCommandId.Updates, "帮助", "检查更新…", "RefreshIcon"),
        new(ShellCommandId.About, "帮助", "关于 Grove…")
    ];
    // A deliberately small navigation palette. Git writes, repository switching,
    // file saving and window lifecycle actions remain in their existing menus.
    // The same live menu predicate is required both here and before dispatch.
    public static IReadOnlyList<ShellCommandDefinition> SearchActions(string query, bool mac,
        Func<ShellCommandId, bool> canRun)
    {
        var words = query.TrimStart('/').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return All.Where(c => IsSearchAction(c.Id) && canRun(c.Id) && words.All(word =>
            $"{c.Header} {c.Group} {c.Id} {c.Gesture(mac)}".Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private static bool IsSearchAction(ShellCommandId id) => id is
        ShellCommandId.ProjectTool or ShellCommandId.CommitTool or ShellCommandId.GitTool or
        ShellCommandId.Output or ShellCommandId.RevealFile or ShellCommandId.ProjectFilter or
        ShellCommandId.HistoryBranches or ShellCommandId.HistoryFiles or ShellCommandId.RecentFiles or
        ShellCommandId.FindFile or ShellCommandId.FindText or ShellCommandId.GoToLine or
        ShellCommandId.Structure or ShellCommandId.Notes or ShellCommandId.FileHistory or
        ShellCommandId.Worktrees or ShellCommandId.RepositoryDetails or ShellCommandId.Shortcuts or
        ShellCommandId.About;

    public static IEnumerable<ShellCommandDefinition> WindowMenu(bool mac) => All.Where(c =>
        mac ? c.Id is not (ShellCommandId.About or ShellCommandId.Updates) : c.Group != "窗口");
}
