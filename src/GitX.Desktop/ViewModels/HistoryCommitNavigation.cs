using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private long _historyJumpVersion;
    internal long HistoryGeneration => _historyVersion;

    public async Task NavigateHistoryCommitAsync(string sha)
    {
        if (_closed || _snapshot is not { } snapshot) return;
        var intent = ++_historyJumpVersion;
        if (Commits.FirstOrDefault(c => c.Sha == sha) is { } visible) { SelectedCommit = visible; return; }
        var repository = _repositoryVersion;
        var generation = _historyVersion;
        var selected = _selectedCommit?.Sha;
        var token = _historyRead.Token;
        bool Current() => !_closed && repository == _repositoryVersion && generation == _historyVersion &&
            intent == _historyJumpVersion && selected == _selectedCommit?.Sha;
        try
        {
            // Resolve hidden/unloaded targets without publishing intermediate pages or resetting the user's selection.
            // The existing history cursor supplies graph rows and bounded, cancellable pages.
            var rows = new List<CommitEntry>();
            HistoryPage page;
            do
            {
                page = await _reader.QueryHistorySummariesAsync(snapshot.Root, snapshot.HistoryHeads ?? snapshot.Head, new(), rows.Count, token);
                if (!Current()) return;
                if (page.HasMore && page.Commits.Count == 0) throw new IOException("历史分页未返回提交，请刷新后重试。");
                rows.AddRange(page.Commits);
            } while (!rows.Any(c => c.Sha == sha) && page.HasMore);
            if (rows.FirstOrDefault(c => c.Sha == sha) is not { } target) throw new IOException("目标提交不在当前仓库的可达历史中，请刷新后重试。");
            var cleared = _historyReference is not null || !_historyQuery.IsEmpty;
            Replace(ref _historyRead); ++_historyVersion;
            _historyReference = null; _historyQuery = new();
            HistoryText = HistoryAuthor = HistoryFrom = HistoryUntil = HistoryPath = "";
            HistoryMatchCase = HistoryUseRegex = false;
            Changed(nameof(HistoryScope)); Changed(nameof(HistoryReferenceKey)); Changed(nameof(HasHistoryFilters)); Changed(nameof(HistoryFilterSummary));
            HistoryFilterLabelsChanged();
            _historyLoading = _historyLoadFailed = false; _historyPageError = null;
            Commits = rows; HasMoreHistory = page.HasMore; HistoryPagingChanged();
            SelectedCommit = target;
            Status = (cleared ? "已清除历史筛选 · " : "") + "已定位提交 " + target.ShortSha;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current()) ReportError(error.Message); }
    }
}
