using GitX.Core;

namespace GitX.Desktop.Services;

// One current payload, no cache or editor models. UI callers serialize requests
// and invalidation; canceled workers may finish but cannot publish old content.
internal sealed class SearchPreviewSession : IDisposable
{
    private readonly Func<string, CodeSearchMatch, CancellationToken, Task<CodeSearchPreview>> _read;
    private CancellationTokenSource? _pending;
    private long _sequence;
    private bool _disposed;
    public CodeSearchPreview? Current { get; private set; }
    public string? Error { get; private set; }
    public bool IsLoading { get; private set; }

    public SearchPreviewSession(Func<string, CodeSearchMatch, CancellationToken, Task<CodeSearchPreview>>? read = null)
        => _read = read ?? RepositoryReader.SearchPreviewAsync;

    public void Invalidate()
    {
        ++_sequence; _pending?.Cancel(); _pending = null;
        Current = null; Error = null; IsLoading = false;
    }

    // False means superseded/closed, so even error/status UI must remain untouched.
    public async Task<bool> LoadAsync(string root, CodeSearchMatch match, Func<bool> contextCurrent)
    {
        if (_disposed) return false;
        Invalidate(); var owner = _sequence;
        using var cancellation = new CancellationTokenSource(); _pending = cancellation;
        if (!contextCurrent()) { Error = "仓库或文档已变化，请关闭后重新搜索。"; _pending = null; return true; }
        IsLoading = true;
        try
        {
            var preview = await _read(root, match, cancellation.Token);
            if (_disposed || owner != _sequence || cancellation.IsCancellationRequested) return false;
            if (!contextCurrent()) Error = "仓库或文档已变化，请关闭后重新搜索。";
            else if (preview.RootKey != WorkingTextFiles.RootKey(root) || preview.Path != match.Path)
                Error = "预览结果与当前选择不符，请重新搜索。";
            else Current = preview;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            if (_disposed || owner != _sequence) return false;
            Error = error.Message;
        }
        finally
        {
            if (owner == _sequence) { IsLoading = false; _pending = null; }
        }
        return !_disposed && owner == _sequence;
    }

    public void Dispose() { if (_disposed) return; _disposed = true; Invalidate(); }
}
