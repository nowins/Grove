using System.ComponentModel;
using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed class RevisionBrowser : INotifyPropertyChanged, IDisposable
{
    private readonly Func<string, string, string, int, CancellationToken, Task<RevisionTreePage>> _read;
    private CancellationTokenSource _pending = new();
    private long _generation;
    private bool _closed;
    public string Root { get; private set; } = "";
    public string Sha { get; private set; } = "";
    public string Path { get; private set; } = "";
    public IReadOnlyList<RevisionTreeEntry> Entries { get; private set; } = [];
    public bool HasMore { get; private set; }
    public bool Loading { get; private set; }
    public string Error { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public RevisionBrowser(Func<string, string, string, int, CancellationToken, Task<RevisionTreePage>>? read = null) => _read = read ?? RevisionTree.ReadAsync;
    public Task OpenAsync(string root, string sha)
    {
        if (_closed) return Task.CompletedTask;
        Root = root; Sha = sha; return NavigateAsync("");
    }
    public Task NavigateAsync(string path)
    {
        if (_closed || Sha.Length == 0) return Task.CompletedTask;
        _pending.Cancel(); _pending.Dispose(); _pending = new(); ++_generation;
        Path = path; Entries = []; HasMore = false;
        return ReadAsync(append: false);
    }
    public Task LoadMoreAsync() => !_closed && !Loading && HasMore ? ReadAsync(append: true) : Task.CompletedTask;
    public Task RetryAsync() => !_closed && !Loading ? ReadAsync(Entries.Count > 0) : Task.CompletedTask;
    private async Task ReadAsync(bool append)
    {
        var generation = _generation; var root = Root; var sha = Sha; var path = Path; var token = _pending.Token;
        Loading = true; Error = ""; Changed();
        try
        {
            var page = await _read(root, sha, path, append ? Entries.Count : 0, token);
            if (_closed || generation != _generation || root != Root || sha != Sha || path != Path) return;
            if (page.Sha != sha || page.Path != path || page.HasMore && page.Entries.Count == 0) throw new IOException("修订目录读取结果不匹配，请重试。");
            Entries = append ? Array.AsReadOnly(Entries.Concat(page.Entries).ToArray()) : page.Entries;
            HasMore = page.HasMore;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed && generation == _generation) Error = error.Message; }
        finally { if (!_closed && generation == _generation) { Loading = false; Changed(); } }
    }
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
    public void Dispose() { if (_closed) return; _closed = true; ++_generation; _pending.Cancel(); _pending.Dispose(); }
}
