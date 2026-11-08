using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop.ViewModels;

public sealed record ContinuousDiffEntry(int Index, string Path, string? OldPath, string Status);
public sealed record ContinuousDiffManifest(string Id, long Version, string Title, string Whitespace,
    int TotalFiles, int Offset, IReadOnlyList<ContinuousDiffEntry> Files);
public sealed record ContinuousDiffRow(int? OldLine, int? NewLine, string OldText, string NewText, string Kind);
public sealed record ContinuousDiffSide(string Path, string Label, string Format, bool Exists, bool EndsWithNewline);
public sealed record ContinuousDiffFile(string Id, int Index, ContinuousDiffSide? Old, ContinuousDiffSide? New,
    IReadOnlyList<ContinuousDiffRow> Rows, string Notice, bool Truncated);
internal sealed class ContinuousDiffBusyException() : IOException("连续 Diff 正在读取其他文件，请重试。");

// One immutable comparison scope, no document cache. HTTP can only select an
// index from this captured list; paths and revisions never come from the page.
public sealed class ContinuousDiffSession : IDisposable
{
    private readonly DocumentRequest[] _requests;
    private readonly NavigationEntry[] _entries;
    private readonly IRepositoryReader _reader;
    private readonly Func<bool> _current;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly string? _repositoryToken;
    private int _disposed;
    public ContinuousDiffManifest Manifest { get; }
    internal bool IsCurrent => Volatile.Read(ref _disposed) == 0 && _current();

    internal ContinuousDiffSession(string root, long version, string title, IReadOnlyList<NavigationEntry> entries,
        DiffWhitespace whitespace, IRepositoryReader reader, Func<bool> current, CancellationToken lifetime, int offset = 0, string? repositoryToken = null)
    {
        _reader = reader; _current = current;
        var captured = _entries = entries.Skip(offset).Take(200).ToArray();
        if (captured.Any(entry => !RecentFileStore.SafePath(entry.Path) || entry.OldPath is not null && !RecentFileStore.SafePath(entry.OldPath) ||
            !RecentFileStore.SafeRevision(entry.Commit) || !RecentFileStore.SafeRevision(entry.BaseCommit) ||
            entry.Kind is ComparisonKind.WorkingFile or ComparisonKind.RevisionFile)) throw new IOException("连续 Diff 的文件或修订范围无效。");
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime); _token = _lifetime.Token;
        _requests = captured.Select(entry => new DocumentRequest(root, entry.Path, entry.Kind, entry.Commit, entry.OldPath, whitespace, entry.BaseCommit)).ToArray();
        _repositoryToken = repositoryToken;
        Manifest = new(Guid.NewGuid().ToString("N"), version, title, whitespace switch {
            DiffWhitespace.Exact => "不忽略空白", DiffWhitespace.Ignore => "忽略空白", _ => "修剪首尾空白" }, entries.Count, offset,
            Array.AsReadOnly(captured.Select((entry, index) => new ContinuousDiffEntry(index, entry.Path, entry.OldPath, entry.Status)).ToArray()));
    }
    internal NavigationEntry Entry(int index) => _entries[index];
    public async Task<ContinuousDiffFile?> ReadAsync(int index, CancellationToken token = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_current() || index < 0 || index >= _requests.Length) return null;
        // Refuse excess work instead of accumulating an HTTP read queue.
        if (!_slots.Wait(0)) throw new ContinuousDiffBusyException();
        try
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(_token, token);
            var request = _requests[index];
            var mutable = request.Kind is ComparisonKind.LocalChanges or ComparisonKind.Index or ComparisonKind.Worktree or ComparisonKind.CommitWorktree or ComparisonKind.CommitParentWorktree or ComparisonKind.Conflict;
            async Task CheckRepository()
            {
                if (mutable && _repositoryToken is not null && await Task.Run(() => RepositoryState.Token(request.Root, [], read.Token), read.Token).ConfigureAwait(false) != _repositoryToken)
                    throw new IOException("索引、HEAD 或 Git 操作状态已变化，请刷新后重新打开连续 Diff。");
            }
            await CheckRepository().ConfigureAwait(false);
            var document = await _reader.DocumentAsync(_requests[index], read.Token).ConfigureAwait(false);
            await CheckRepository().ConfigureAwait(false);
            read.Token.ThrowIfCancellationRequested();
            if (!_current() || Volatile.Read(ref _disposed) != 0) return null;
            var projected = await Task.Run(() => Project(Manifest.Id, index, document), read.Token).ConfigureAwait(false);
            read.Token.ThrowIfCancellationRequested();
            return _current() && Volatile.Read(ref _disposed) == 0 ? projected : null;
        }
        finally { _slots.Release(); }
    }
    internal static ContinuousDiffFile Project(string id, int index, RepositoryDocument document)
    {
        if (document.IsNotice || document.Diff is not { } diff)
            return new(id, index, null, null, [], document.Text.Length > 2000 ? document.Text[..2000] + "…" : document.Text, false);
        const int limit = 6000;
        var oldLines = Lines(diff.Old.Text); var newLines = Lines(diff.New.Text);
        var rows = new List<ContinuousDiffRow>(); var truncated = false;
        bool Add(int? oldLine, int? newLine, string kind, string? notice = null)
        {
            if (rows.Count >= limit) { truncated = true; return false; }
            rows.Add(new(oldLine, newLine, notice ?? (oldLine is { } o ? oldLines[o - 1] : ""),
                notice ?? (newLine is { } n ? newLines[n - 1] : ""), kind)); return true;
        }
        void Context(int oldStart, int newStart, int count)
        {
            for (var offset = 0; offset < count && !truncated; offset++)
            {
                if (count > 6 && offset == 3)
                { Add(null, null, "gap", $"… 隐藏 {count - 6:N0} 行未变化代码"); offset = count - 4; continue; }
                Add(oldStart + offset + 1, newStart + offset + 1, "context");
            }
        }
        var oldEnd = 0; var newEnd = 0;
        foreach (var block in diff.Blocks)
        {
            Context(oldEnd, newEnd, block.OldStart - oldEnd);
            foreach (var row in block.Rows)
                if (!Add(row.OldLine, row.NewLine, row.OldLine is null ? "added" : row.NewLine is null ? "deleted" : "modified")) break;
            oldEnd = block.OldStart + block.OldCount; newEnd = block.NewStart + block.NewCount;
            if (truncated) break;
        }
        if (!truncated) Context(oldEnd, newEnd, oldLines.Length - oldEnd);
        static ContinuousDiffSide Side(DiffSide side) => new(side.Path, side.Label,
            !side.Exists ? "不存在" : side.Text.Length == 0 ? "空文件" : side.LineEnding, side.Exists, side.EndsWithNewline);
        return new(id, index, Side(diff.Old), Side(diff.New), rows.AsReadOnly(), diff.Blocks.Count == 0 ? "没有文本差异；换行与文件状态见两侧说明。" : "", truncated);
    }
    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n'); var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        return lines.Take(count).Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}

public sealed partial class MainViewModel
{
    private ContinuousDiffSession? _continuousDiff;
    public ContinuousDiffSession? ContinuousDiff => _continuousDiff;
    public bool CanOpenContinuousDiff => !_closed && !IsWriting && _snapshot is not null && _mode is 1 or 2 && NavigationEntries(_mode).Count > 0;
    private void ResetContinuousDiff() { _continuousDiff?.Dispose(); _continuousDiff = null; }
    public async Task<bool> OpenContinuousDiffAsync(int offset = 0)
    {
        if (!CanOpenContinuousDiff) return false;
        var repository = _repositoryVersion; var version = _documentVersion; var mode = _mode;
        var entries = NavigationEntries(mode).ToArray();
        if (offset < 0 || offset >= entries.Length) return false;
        await CaptureReadingAsync();
        if (_closed || IsWriting || repository != _repositoryVersion || version != _documentVersion || mode != _mode ||
            !entries.SequenceEqual(NavigationEntries(mode)) || _snapshot is not { } snapshot) return false;
        var revision = entries[0].Commit is { } sha ? sha[..Math.Min(12, sha.Length)] : "提交";
        var title = mode == 1 ? "本地改动 · HEAD → 当前版本" : _historyCompareOld is not null ? HistoryFilesTitle : $"{revision} · 第一父提交 → 所选提交";
        ContinuousDiffSession? session = null;
        try
        {
            session = new(snapshot.Root, version + 1, title, entries,
                _diffWhitespace, _reader, () => !_closed && repository == _repositoryVersion && version + 1 == _documentVersion &&
                    ReferenceEquals(_continuousDiff, session), _repositoryRead.Token, offset, snapshot.IndexToken);
        }
        catch (Exception error) when (error is IOException or ArgumentException) { ReportError(error.Message); return false; }
        SuspendSourceTab(); _selectedEntry = null; Changed(nameof(SelectedEntry));
        ClearDocument("连续 Diff", _ => _continuousDiff = session);
        return true;
    }
    public async Task<bool> OpenContinuousDiffFileAsync(string id, int index, int side, int line, long version)
    {
        if (_continuousDiff is not { } session || session.Manifest.Id != id || version != _documentVersion ||
            index < 0 || index >= session.Manifest.Files.Count || side is < 0 or > 1 || line < 1) return false;
        // Use the original request, not a newly selected commit/tool scope.
        var entry = session.Entry(index);
        await OpenEditorEntryAsync(entry, line, side);
        if (_closed || _documentVersion != version + 1 || _selectedEntry != entry || _document is not { IsNotice: false } document) return false;
        return document.RevealSide == side && document.RevealLine == line;
    }
}
