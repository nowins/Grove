using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed record HistoryBranchNode(string Key, string Name, HistoryReference? Reference = null, IReadOnlyList<HistoryBranchNode>? Nodes = null)
{
    public IReadOnlyList<HistoryBranchNode> Children => Nodes ?? [];
    public bool Current => Reference?.Current == true;
    public bool IsGroup => Children.Count > 0;
    public bool IsReference => Reference is not null || Key == "HEAD";
    public string Tooltip => Reference is { } r ? $"{r.CanonicalName}\n{r.Sha}\n单击筛选历史，工作区分支不变" : Name;
}

public sealed partial class MainViewModel
{
    public Task WriteHistoryFilesAsync(HistoryFileWritePlan plan) => ExecuteWriteAsync(plan.Selection.Root, async () =>
    {
        _recovery = await HistoryFileActions.ExecuteAsync(plan);
        RecoveryChanged();
    }, plan.Operation switch { HistoryFileOperation.Revert => "撤销所选历史文件更改", HistoryFileOperation.CherryPick => "拣选所选历史文件更改", _ => "从修订获取文件" });
    private CancellationTokenSource _historyRead = new();
    private string? _historyReference;
    private HistoryQuery _historyQuery = new();
    private string _historyText = "", _historyAuthor = "", _historyFrom = "", _historyUntil = "", _historyPath = "";
    private CommitEntry? _historyCompareOld, _historyCompareNew;
    private bool _historyCompareWorktree;
    private bool _historyMatchCase, _historyUseRegex;
    private bool _historyShowAuthor = true, _historyShowDate = true, _historyShowReferences = true;
    private string _historyBranchSearch = "";
    public bool HistoryMatchCase { get => _historyMatchCase; set => Set(ref _historyMatchCase, value); }
    public bool HistoryUseRegex { get => _historyUseRegex; set => Set(ref _historyUseRegex, value); }
    public bool HistoryShowAuthor { get => _historyShowAuthor; set => Set(ref _historyShowAuthor, value); }
    public bool HistoryShowDate { get => _historyShowDate; set => Set(ref _historyShowDate, value); }
    public bool HistoryShowReferences { get => _historyShowReferences; set => Set(ref _historyShowReferences, value); }
    public string HistoryBranchSearch { get => _historyBranchSearch; set { if (Set(ref _historyBranchSearch, value)) BuildHistoryBranches(); } }
    public string HistoryBranchLabel => _historyReference is null ? "分支" : "分支: " + HistoryScope;
    public string HistoryAuthorLabel => _historyQuery.Author.Length == 0 ? "作者" : "作者: " + _historyQuery.Author;
    public string HistoryDateLabel => _historyQuery.From is null && _historyQuery.Until is null ? "日期" : "日期: " +
        (_historyQuery.From?.ToString("MM-dd") ?? "…") + " – " + (_historyQuery.Until?.AddDays(-1).ToString("MM-dd") ?? "…");
    public string HistoryPathLabel => _historyQuery.Path.Length == 0 ? "路径" : "路径: " + _historyQuery.Path;
    public bool HasHistoryBranchFilter => _historyReference is not null;
    public bool HasHistoryAuthorFilter => _historyQuery.Author.Length > 0;
    public bool HasHistoryDateFilter => _historyQuery.From is not null || _historyQuery.Until is not null;
    public bool HasHistoryPathFilter => _historyQuery.Path.Length > 0;
    private void HistoryFilterLabelsChanged()
    {
        foreach (var name in new[] { nameof(HistoryBranchLabel), nameof(HistoryAuthorLabel), nameof(HistoryDateLabel), nameof(HistoryPathLabel),
            nameof(HasHistoryBranchFilter), nameof(HasHistoryAuthorFilter), nameof(HasHistoryDateFilter), nameof(HasHistoryPathFilter) }) Changed(name);
    }
    public string HistoryText { get => _historyText; set => Set(ref _historyText, value); }
    public string HistoryAuthor { get => _historyAuthor; set => Set(ref _historyAuthor, value); }
    public string HistoryFrom { get => _historyFrom; set => Set(ref _historyFrom, value); }
    public string HistoryUntil { get => _historyUntil; set => Set(ref _historyUntil, value); }
    public string HistoryPath { get => _historyPath; set => Set(ref _historyPath, value); }
    public bool HasHistoryFilters => !_historyQuery.IsEmpty;
    public string HistoryFilterSummary => string.Join(" · ", new[] { _historyQuery.Text.Length > 0 ? "文本/SHA " + _historyQuery.Text : "", _historyQuery.Author.Length > 0 ? "作者 " + _historyQuery.Author : "", _historyQuery.Path.Length > 0 ? _historyQuery.Path : "", _historyQuery.From is { } from ? from.ToString("yyyy-MM-dd") + " 起" : "", _historyQuery.Until is { } until ? until.AddDays(-1).ToString("yyyy-MM-dd") + " 止" : "" }.Where(x => x.Length > 0));
    public string HistoryFilesTitle => _historyCompareOld is null ? "提交中的文件" : _historyCompareWorktree ? _historyCompareOld.ShortSha + " → 本地" : _historyCompareOld.ShortSha + " → " + _historyCompareNew!.ShortSha;
    private IReadOnlyList<HistoryBranchNode> _historyBranches = [];
    public IReadOnlyList<HistoryBranchNode> HistoryBranches => _historyBranches;
    public IReadOnlyList<HistoryReference> HistoryReferenceCandidates => _snapshot?.References ?? [];
    public bool HasHistory => Commits.Count > 0;
    private bool _historyLoadFailed;
    private bool _historyRetryReplace;
    private string? _historyPageError;
    public bool HistoryLoadFailed => _historyLoadFailed;
    public bool HistoryLoading => _historyLoading;
    public bool CanAutoLoadHistory => HasMoreHistory && !_historyLoading && !_historyLoadFailed;
    public string HistoryEmptyText => _historyLoading ? "正在读取提交…" : _historyLoadFailed ? "提交读取失败，可重试" : "此范围没有提交";
    public string HistoryPagingText => _historyLoading ? "正在读取提交…" : _historyLoadFailed ? "加载失败 · 已保留当前列表" :
        HasMoreHistory ? $"已加载 {Commits.Count:N0} 条提交 · 向下滚动继续加载" : HasHistory ? $"已加载全部 {Commits.Count:N0} 条提交" : "";
    private void HistoryPagingChanged()
    {
        Changed(nameof(HistoryLoading)); Changed(nameof(HistoryLoadFailed)); Changed(nameof(CanAutoLoadHistory));
        Changed(nameof(HistoryPagingText)); Changed(nameof(HistoryEmptyText));
    }
    public string HistoryScope => _historyReference is null ? "全部分支" : _historyReference == "HEAD" ? "HEAD · 当前分支" :
        _snapshot?.References?.FirstOrDefault(r => r.CanonicalName == _historyReference)?.Name ?? "全部分支";
    public string? HistoryReferenceKey => _historyReference;
    public string HistoryCommitDetail => _historyCompareOld is { } old ? $"{HistoryFilesTitle}\n\n起点：{old.FullMessage}\n{old.Sha}\n\n" +
        (_historyCompareWorktree ? "终点：当前工作区（包含未跟踪文件）" : $"终点：{_historyCompareNew!.FullMessage}\n{_historyCompareNew.Sha}") :
        _selectedCommit is not { } c ? "选择提交查看文件和详情" : $"{(c.FullMessage.StartsWith(c.Subject, StringComparison.Ordinal) ? c.FullMessage[c.Subject.Length..].Trim() : c.FullMessage)}\n\n{c.References}\n\n父提交：\n{string.Join("\n", c.Parents ?? [])}".Trim();

    private string? HistoryHeads(RepositorySnapshot snapshot) => _historyReference is null ? snapshot.HistoryHeads ?? snapshot.Head :
        _historyReference == "HEAD" ? snapshot.Head : snapshot.References?.FirstOrDefault(r => r.CanonicalName == _historyReference)?.Sha;

    private void UpdateHistoryReferences(bool newRepository)
    {
        if (newRepository)
        {
            _historyReference = null; _historyQuery = new(); Replace(ref _historyRead);
            HistoryText = HistoryAuthor = HistoryFrom = HistoryUntil = HistoryPath = "";
            HistoryMatchCase = HistoryUseRegex = false; _historyBranchSearch = ""; Changed(nameof(HistoryBranchSearch));
            Changed(nameof(HasHistoryFilters)); Changed(nameof(HistoryFilterSummary)); ClearHistoryComparison();
        }
        var references = _snapshot?.References ?? [];
        if (_historyReference is not (null or "HEAD") && !references.Any(r => r.CanonicalName == _historyReference)) _historyReference = null;
        BuildHistoryBranches(); Changed(nameof(HistoryScope)); Changed(nameof(HistoryReferenceKey)); HistoryFilterLabelsChanged();
    }

    private void BuildHistoryBranches()
    {
        var references = _snapshot?.References ?? [];
        var filter = _historyBranchSearch.Trim();
        var nodes = new List<HistoryBranchNode> { new("all", "全部提交"), new("HEAD", "HEAD（当前分支）") };
        static IReadOnlyList<HistoryBranchNode> Group(IReadOnlyList<HistoryReference> refs, int depth, string prefix)
        {
            return refs.GroupBy(r => r.Name.Split('/')[depth], StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g =>
                {
                    var leaf = g.Where(r => r.Name.Split('/').Length == depth + 1).Select(r => new HistoryBranchNode(r.CanonicalName, g.Key, r));
                    var nested = g.Where(r => r.Name.Split('/').Length > depth + 1).ToArray();
                    return nested.Length == 0 ? leaf : leaf.Append(new HistoryBranchNode(prefix + "/" + g.Key, g.Key, Nodes: Group(nested, depth + 1, prefix + "/" + g.Key)));
                }).ToArray();
        }
        foreach (var (kind, label) in new[] { ("local", "本地分支"), ("remote", "远程分支"), ("tag", "标签") })
        {
            var children = Group(references.Where(r => r.Kind == kind && (filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray(), 0, kind);
            if (children.Count > 0) nodes.Add(new(kind, label, Nodes: children));
        }
        _historyBranches = nodes; Changed(nameof(HistoryBranches));
    }

    public async Task SelectHistoryReferenceAsync(string? canonicalName)
    {
        if (_closed || _snapshot is null || canonicalName == _historyReference) return;
        if (canonicalName is not (null or "HEAD") && _snapshot.References?.Any(r => r.CanonicalName == canonicalName) != true) return;
        _historyReference = canonicalName; Changed(nameof(HistoryScope)); Changed(nameof(HistoryReferenceKey)); HistoryFilterLabelsChanged();
        await ReloadHistoryAsync();
    }

    public async Task ApplyHistoryQueryAsync(bool clear = false)
    {
        if (_closed || _snapshot is null) return;
        if (clear) { HistoryText = HistoryAuthor = HistoryFrom = HistoryUntil = HistoryPath = ""; HistoryMatchCase = HistoryUseRegex = false; }
        try
        {
            var query = HistoryQueryInput.Create(HistoryText, HistoryAuthor, HistoryFrom, HistoryUntil, HistoryPath, HistoryMatchCase, HistoryUseRegex);
            Error = "";
            if (query == _historyQuery) return;
            _historyQuery = query; Changed(nameof(HasHistoryFilters)); Changed(nameof(HistoryFilterSummary)); Error = "";
            HistoryFilterLabelsChanged();
            await ReloadHistoryAsync();
        }
        catch (Exception e) when (e is IOException or ArgumentOutOfRangeException) { ReportError(e.Message); }
    }

    public async Task ShowFileHistoryAsync(string path)
    {
        if (_closed || _snapshot is null || _mode != 2) return;
        _historyReference = null; Changed(nameof(HistoryScope)); Changed(nameof(HistoryReferenceKey));
        Filter = "";
        HistoryText = HistoryAuthor = HistoryFrom = HistoryUntil = "";
        HistoryPath = path;
        _historyQuery = new HistoryQuery(Path: path).Normalize();
        HistoryMatchCase = HistoryUseRegex = false; HistoryFilterLabelsChanged();
        Changed(nameof(HasHistoryFilters)); Changed(nameof(HistoryFilterSummary));
        await ReloadHistoryAsync();
    }

    private async Task ReloadHistoryAsync()
    {
        var selectedSha = _selectedCommit?.Sha;
        ClearHistoryComparison();
        Replace(ref _historyRead); ++_historyVersion; _historyLoading = false; _historyLoadFailed = false; HistoryPagingChanged();
        var generation = _historyVersion; var repositoryVersion = _repositoryVersion;
        Replace(ref _commitRead); ++_commitVersion;
        SetHistoryEntries([]); Commits = []; HasMoreHistory = false;
        _selectedCommit = null; Changed(nameof(SelectedCommit)); Changed(nameof(HistoryCommitDetail));
        await LoadHistoryAsync();
        if (_closed || generation != _historyVersion || repositoryVersion != _repositoryVersion) return;
        SelectedCommit = Commits.FirstOrDefault(c => c.Sha == selectedSha) ?? Commits.FirstOrDefault();
    }

    private void ClearHistoryComparison()
    {
        _historyCompareOld = _historyCompareNew = null; _historyCompareWorktree = false;
        Changed(nameof(HistoryFilesTitle)); Changed(nameof(NavigationTitle)); Changed(nameof(HistoryCommitDetail));
    }

    public async Task CompareHistoryAsync(CommitEntry old, CommitEntry? next, bool preserve = false)
    {
        if (_closed || _snapshot is not { } snapshot) return;
        var version = ++_commitVersion; var repositoryVersion = _repositoryVersion;
        Replace(ref _commitRead); var token = _commitRead.Token;
        _historyCompareOld = old; _historyCompareNew = next; _historyCompareWorktree = next is null;
        Changed(nameof(HistoryFilesTitle)); Changed(nameof(NavigationTitle)); Changed(nameof(HistoryCommitDetail));
        if (!preserve) SetHistoryEntries([]);
        try
        {
            var changes = await _reader.CompareChangesAsync(snapshot.Root, old.Sha, next?.Sha, token);
            if (_closed || repositoryVersion != _repositoryVersion || version != _commitVersion) return;
            SetHistoryEntries(changes.Select(c => new NavigationEntry(c.Path, c.Status, c.Kind, c.OldPath, next?.Sha ?? old.Sha, c.Status, next is null ? null : old.Sha)).ToArray());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed && version == _commitVersion && repositoryVersion == _repositoryVersion) ReportError(e.Message); }
    }
}
