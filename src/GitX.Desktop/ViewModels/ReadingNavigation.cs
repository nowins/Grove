using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed record ReadingPosition(long Version, int Side, int Line, int Column);
internal sealed record ReadingTarget(string Path, ComparisonKind Kind, string? OldPath, string? Commit,
    string? BaseCommit, string? Revision)
{
    public NavigationEntry Entry => new(Path, Path, Kind, OldPath, Commit, BaseCommit: BaseCommit);
}
internal sealed record ReadingPlace(ReadingTarget Target, int Side, int Line, int Column);

// Per repository session, metadata only: no document, model, draft or text cache.
internal sealed class ReadingHistory
{
    private readonly List<ReadingPlace> _places = [];
    public int Index { get; private set; } = -1;
    public long Generation { get; private set; }
    public ReadingPlace? Current => Index >= 0 ? _places[Index] : null;
    public bool CanBack => Index > 0;
    public bool CanForward => Index >= 0 && Index + 1 < _places.Count;
    internal IReadOnlyList<ReadingPlace> Places => _places.AsReadOnly();

    public bool Record(ReadingPlace place, bool navigation)
    {
        var current = Current;
        if (current == place) return false;
        Generation++;
        if (current is not null && current.Target == place.Target &&
            (!navigation || current.Side == place.Side && current.Line == place.Line))
        { _places[Index] = place; return false; }
        if (Index + 1 < _places.Count) _places.RemoveRange(Index + 1, _places.Count - Index - 1);
        if (Index > 0 && current is not null && _places[Index - 1] is { } previous &&
            previous.Target == current.Target && previous.Side == current.Side && Math.Abs((long)previous.Line - current.Line) < 4)
        { _places.RemoveAt(Index - 1); Index--; }
        _places.Add(place);
        if (_places.Count > 100) _places.RemoveAt(0);
        Index = _places.Count - 1;
        return true;
    }

    public (int Index, ReadingPlace Place)? Peek(int direction, bool restoreCurrent)
    {
        if (direction is not (-1 or 1)) return null;
        var index = restoreCurrent && direction < 0 ? Index : Index + direction;
        return Index >= 0 && index >= 0 && index < _places.Count ? (index, _places[index]) : null;
    }
    public bool Move(int index, long generation)
    {
        if (generation != Generation || index < 0 || index >= _places.Count) return false;
        Index = index; Generation++; return true;
    }
    public void Clear() { _places.Clear(); Index = -1; Generation++; }
}

public sealed partial class MainViewModel
{
    private readonly ReadingHistory _readingHistory = new();
    private ReadingTarget? _readingTarget;
    private CancellationTokenSource? _readingRead;
    private long _readingIntent;
    public Func<long, Task<ReadingPosition?>>? CaptureReadingPosition { get; set; }
    public bool CanReadBack => !_closed && Root is not null && _readingRead is null &&
        (_readingHistory.CanBack || _document is null && _readingHistory.Current is not null);
    public bool CanReadForward => !_closed && Root is not null && _readingRead is null && _readingHistory.CanForward;

    private void ReadingAvailabilityChanged() { Changed(nameof(CanReadBack)); Changed(nameof(CanReadForward)); }
    private void InterruptReading() { ++_readingIntent; _readingRead?.Cancel(); }
    private void ResetReading()
    {
        InterruptReading(); _readingTarget = null; _readingHistory.Clear(); ReadingAvailabilityChanged();
    }
    private static ReadingTarget? ReadingTargetFor(NavigationEntry entry, RepositoryDocument document)
    {
        static bool Revision(string? value) => value is null || value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
        var revision = entry.Kind == ComparisonKind.WorkingFile ? document.SourceRevision : null;
        if (document.IsNotice || entry.Path.Length is 0 or > 4096 || document.Path.Length is 0 or > 4096 || entry.OldPath?.Length > 4096 ||
            !Revision(entry.Commit) || !Revision(entry.BaseCommit) || !Revision(revision)) return null;
        return revision is not null ? new(document.Path, ComparisonKind.WorkingFile, null, null, null, revision)
            : new(entry.Path, entry.Kind, entry.OldPath, entry.Commit, entry.BaseCommit, null);
    }
    private void TrackReading(NavigationEntry entry, RepositoryDocument document, bool preserve, bool explicitJump = false)
    {
        if (ReadingTargetFor(entry, document) is not { } target) return;
        _readingTarget = target;
        if (!preserve || _readingHistory.Current?.Target != target)
            _readingHistory.Record(new(target, document.Diff is null ? 1 : document.RevealSide ?? 1,
                Math.Max(1, document.RevealLine ?? 1), Math.Max(1, document.RevealColumn ?? 1)), explicitJump);
        ReadingAvailabilityChanged();
    }
    public void RecordReadingPosition(ReadingPosition position, bool navigation = false)
    {
        if (_closed || position.Version != _documentVersion || _document is not { IsNotice: false } document ||
            _readingTarget is null || _readingHistory.Current?.Target != _readingTarget ||
            position.Side is < 0 or > 1 || position.Line < 1 || position.Column < 1 || document.Diff is null && position.Side != 1) return;
        if (_readingHistory.Record(new(_readingTarget, position.Side, position.Line, position.Column), navigation)) ReadingAvailabilityChanged();
    }
    private async Task<bool> CaptureReadingAsync()
    {
        if (_document is null || _document.IsNotice || CaptureReadingPosition is not { } capture) return true;
        var version = _documentVersion; var repository = _repositoryVersion;
        try
        {
            var position = await capture(version);
            if (_closed || version != _documentVersion || repository != _repositoryVersion || position?.Version != version) return false;
            RecordReadingPosition(position); return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { return false; } // Optional capture must never block normal file opening.
    }

    public async Task<bool> NavigateReadingAsync(int direction, long version)
    {
        if (_closed || direction is not (-1 or 1) || version != _documentVersion || _snapshot is not { } snapshot ||
            _readingRead is not null || IsWriting) return false;
        InterruptReading(); var intent = _readingIntent; var repository = _repositoryVersion; var annotation = _annotationNavigation;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(_repositoryRead.Token, _documentRead.Token);
        _readingRead = read; ReadingAvailabilityChanged();
        bool Current() => !_closed && !read.IsCancellationRequested && intent == _readingIntent &&
            repository == _repositoryVersion && version == _documentVersion && annotation == _annotationNavigation;
        try
        {
            if (!await CaptureReadingAsync() || !Current() ||
                _readingHistory.Peek(direction, restoreCurrent: _document is null) is not { } next) return false;
            var generation = _readingHistory.Generation; var place = next.Place; var target = place.Target; var entry = target.Entry;
            var tab = FindReadingTab(target); var annotate = tab?.Annotate == true;
            var document = entry.Kind == ComparisonKind.WorkingFile
                ? await ReadSourceSnapshotAsync(snapshot.Root, entry, target.Revision is { } revision ? new(revision, target.Path) : null, annotate, null, read.Token)
                : await _reader.DocumentAsync(new(snapshot.Root, entry.Path, entry.Kind, entry.Commit, entry.OldPath, _diffWhitespace, entry.BaseCommit), read.Token);
            if (!Current() || generation != _readingHistory.Generation) return false;
            if (document.IsNotice) { ReportError("无法返回阅读位置：" + document.Text); return false; }
            if (!_readingHistory.Move(next.Index, generation)) return false;
            ResetContinuousDiff();
            SetEditorContext(EditorContext(entry));
            if (tab is null)
            {
                tab = new(entry, target.Revision is { } sha ? new(sha, target.Path) : null, false);
                _sourceTabs.Add(tab);
            }
            SetSourceTab(tab);
            _selectedEntry = entry; Changed(nameof(SelectedEntry));
            Replace(ref _documentRead);
            _readingTarget = target;
            _document = document = document with { RevealLine = place.Line, RevealColumn = place.Column, RevealLength = 0, RevealSide = place.Side, PreserveView = false };
            Error = ""; FileTitle = document.Path; Comparison = document.Comparison; Detail = document.Detail;
            WriteStateChanged(); DocumentChanged?.Invoke(++_documentVersion, document);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { if (Current()) ReportError("无法返回阅读位置：" + error.Message); return false; }
        finally
        {
            if (ReferenceEquals(_readingRead, read)) { _readingRead = null; ReadingAvailabilityChanged(); }
        }
    }
    private SourceTabState? FindReadingTab(ReadingTarget target) => _sourceTabs.FirstOrDefault(tab => target.Revision is not null
        ? tab.Target?.Sha == target.Revision && tab.Target.Path == target.Path
        : tab.Target is null && tab.Entry.Path == target.Path && tab.Entry.Kind == target.Kind && tab.Entry.Commit == target.Commit &&
            tab.Entry.BaseCommit == target.BaseCommit && tab.Entry.OldPath == target.OldPath);
}
