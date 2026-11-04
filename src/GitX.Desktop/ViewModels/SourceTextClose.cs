namespace GitX.Desktop.ViewModels;

public sealed record SourceTextCloseRequest(string Reason, IReadOnlyList<string> Ids);
public sealed class SourceTextCloseLease(bool allowed, Func<Task>? release = null) : IAsyncDisposable
{
    private Func<Task>? _release = release;
    public bool Allowed { get; } = allowed;
    public async ValueTask DisposeAsync() { var action = Interlocked.Exchange(ref _release, null); if (action is not null) await action(); }
}
