using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool _diffFileNavigating;
    private long _diffFileNavigationIntent;
    public void CancelDiffNavigation() => ++_diffFileNavigationIntent;
    private static bool SameDiffEntry(NavigationEntry left, NavigationEntry right) =>
        left.Path == right.Path && (left.OldPath ?? left.Path) == (right.OldPath ?? right.Path) && left.Kind == right.Kind && left.Commit == right.Commit && left.BaseCommit == right.BaseCommit;

    private IReadOnlyList<NavigationEntry> DiffFileEntries(SourceTabState tab)
    {
        if (tab.Entry.Kind == ComparisonKind.LocalChanges) return NavigationEntries(1);
        if (tab.Entry.Kind is ComparisonKind.Index or ComparisonKind.Worktree or ComparisonKind.Conflict)
            return _snapshot?.Changes.Where(change => change.Kind == tab.Entry.Kind)
                .Select(change => new NavigationEntry(change.Path, change.Status, change.Kind, change.OldPath, Status: change.Status)).ToArray() ?? [];
        return tab.DiffScope ?? [tab.Entry];
    }
    private static bool CanResolveDiffRange(NavigationEntry entry) => entry.Commit is not null && RecentFileStore.SafeRevision(entry.Commit) &&
        (entry.Kind is ComparisonKind.Commit or ComparisonKind.CommitWorktree || entry.Kind == ComparisonKind.RevisionRange &&
            entry.BaseCommit is not null && RecentFileStore.SafeRevision(entry.BaseCommit));
    public bool CanNavigateDiffFiles => !_closed && _document is { IsDiff: true } && _sourceTabVisible && _activeSourceTab is { } tab &&
        (tab.Entry.Kind is ComparisonKind.LocalChanges or ComparisonKind.Index or ComparisonKind.Worktree or ComparisonKind.Conflict ||
            DiffFileEntries(tab).Count > 1 || CanResolveDiffRange(tab.Entry));

    public async Task<bool> NavigateDiffFileAsync(int direction, int side, long version)
    {
        if (_closed || _diffFileNavigating || IsWriting || direction is not (-1 or 1) || side is < 0 or > 1 ||
            version != _documentVersion || _document is not { IsDiff: true } || !_sourceTabVisible ||
            _activeSourceTab is not { } tab || _snapshot is not { } snapshot) return false;
        var repository = _repositoryVersion; var annotation = _annotationNavigation; var intent = _readingIntent; var whitespace = _diffWhitespace;
        var navigation = ++_diffFileNavigationIntent;
        bool Current() => !_closed && !IsWriting && repository == _repositoryVersion && version == _documentVersion &&
            annotation == _annotationNavigation && intent == _readingIntent && ReferenceEquals(tab, _activeSourceTab) &&
            snapshot.IndexToken == _snapshot?.IndexToken && whitespace == _diffWhitespace && navigation == _diffFileNavigationIntent;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_repositoryRead.Token, _documentRead.Token);
        _diffFileNavigating = true;
        try
        {
            var entries = DiffFileEntries(tab);
            if (CanResolveDiffRange(tab.Entry) && (tab.DiffScope is null || tab.Entry.Kind == ComparisonKind.CommitWorktree))
            {
                var entry = tab.Entry;
                var changes = entry.Kind == ComparisonKind.Commit
                    ? await _reader.CommitChangesAsync(snapshot.Root, entry.Commit!, lifetime.Token)
                    : await _reader.CompareChangesAsync(snapshot.Root, entry.BaseCommit ?? entry.Commit!, entry.Kind == ComparisonKind.CommitWorktree ? null : entry.Commit, lifetime.Token);
                entries = changes.Select(change => new NavigationEntry(change.Path, change.Status, change.Kind, change.OldPath, entry.Commit, change.Status, entry.BaseCommit)).ToArray();
            }
            if (!Current()) return false;
            var index = -1;
            for (var i = 0; i < entries.Count; i++) if (SameDiffEntry(entries[i], tab.Entry)) { index = i; break; }
            if (entries.Count == 0) return false;
            var count = entries.Count - 1;
            if (index < 0)
            {
                // A watched file can become clean while its Diff stays open.
                // Continue from its path instead of trapping navigation there.
                var next = direction > 0 ? 0 : entries.Count - 1;
                if (direction > 0)
                { for (var i = 0; i < entries.Count; i++) if (string.CompareOrdinal(entries[i].Path, tab.Entry.Path) > 0) { next = i; break; } }
                else
                { for (var i = entries.Count - 1; i >= 0; i--) if (string.CompareOrdinal(entries[i].Path, tab.Entry.Path) < 0) { next = i; break; } }
                index = (next - direction + entries.Count) % entries.Count; count = entries.Count;
            }
            for (var step = 1; step <= count; step++)
            {
                if (!Current()) return false;
                var entry = entries[(index + direction * step % entries.Count + entries.Count) % entries.Count];
                var mutable = entry.Kind is ComparisonKind.LocalChanges or ComparisonKind.Index or ComparisonKind.Worktree or ComparisonKind.Conflict or ComparisonKind.CommitWorktree or ComparisonKind.CommitParentWorktree;
                async Task CheckBasis()
                {
                    if (mutable && snapshot.IndexToken is { } expected &&
                        await Task.Run(() => RepositoryState.Token(snapshot.Root, [], lifetime.Token), lifetime.Token) != expected)
                        throw new IOException("比较基准已变化，请刷新后继续差异导航。");
                }
                await CheckBasis();
                var document = await _reader.DocumentAsync(new(snapshot.Root, entry.Path, entry.Kind, entry.Commit, entry.OldPath, whitespace, entry.BaseCommit), lifetime.Token);
                await CheckBasis();
                if (!Current()) return false;
                if (document.IsNotice || document.Diff is not { Blocks.Count: > 0 } diff) continue;
                await CaptureReadingAsync();
                if (!Current()) return false;
                var block = direction > 0 ? diff.Blocks[0] : diff.Blocks[^1];
                var line = (side == 0 ? block.OldStart : block.NewStart) + 1;
                document = document with { RevealDiffEdge = direction, RevealSide = side, RevealLine = line, RevealColumn = 1, PreserveView = false };
                var nextVersion = ++_documentVersion;
                InterruptReading(); Replace(ref _documentRead);
                SetEditorContext(EditorContext(entry)); OpenDocumentTab(entry);
                if (EditorContext(entry) == 2) _activeSourceTab!.DiffScope = entries;
                _selectedEntry = entry; Changed(nameof(SelectedEntry));
                _document = document; Error = ""; FileTitle = document.Path; Comparison = document.Comparison; Detail = document.Detail;
                TrackReading(entry, document, preserve: false, explicitJump: true); WriteStateChanged();
                DocumentChanged?.Invoke(nextVersion, document);
                return true;
            }
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { if (Current()) ReportError(error.Message); return false; }
        finally { _diffFileNavigating = false; }
    }
}
