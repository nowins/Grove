using System.Diagnostics;

namespace GitX.Core;

public sealed record GitWriteProgress(string Root, string Name, string Phase, TimeSpan Elapsed,
    bool Running, int? ProcessId, bool TerminationRequested, string Output);

// One explicit write lifetime. View/read cancellation never enters this scope.
public sealed class GitWriteOperation(string root, string name)
{
    private static readonly AsyncLocal<GitWriteOperation?> Active = new();
    internal static GitWriteOperation? Current => Active.Value;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Process? _process;
    private string _phase = "准备操作", _output = "";
    private bool _terminated, _running = true;
    private const int OutputLimit = 64 * 1024;

    public GitWriteProgress Snapshot()
    {
        lock (_gate) return new(root, name, _phase, _clock.Elapsed, _running,
            _process is { HasExited: false } ? _process.Id : null, _terminated, _output);
    }

    public async Task RunAsync(Func<Task> action)
    {
        var previous = Active.Value; Active.Value = this;
        try { ThrowIfTerminated(); await action(); ThrowIfTerminated(); }
        finally { Active.Value = previous; }
    }

    internal void Attach(Process process, string command)
    {
        lock (_gate) { ThrowIfTerminated(); _process = process; _phase = "git " + command; }
    }
    internal void Detach(Process process)
    { lock (_gate) { if (_process == process) _process = null; } }
    internal void Append(string text)
    {
        if (text.Length == 0) return;
        lock (_gate)
        {
            _output += text;
            if (_output.Length > OutputLimit) _output = "[仅保留最近 64 Ki 字符的任务输出]\n" + _output[^OutputLimit..];
        }
    }
    internal void ThrowIfTerminated()
    {
        lock (_gate) if (_terminated) throw new GitWriteTerminatedException();
    }

    // The confirmed PID belongs to this scope. If the command finished while
    // the confirmation was open, never terminate a later command instead.
    public bool Terminate(int confirmedProcessId)
    {
        lock (_gate)
        {
            if (!_running || _terminated || _process is null || _process.Id != confirmedProcessId || _process.HasExited) return false;
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception e) { throw new IOException("无法终止该 Git 进程：" + e.Message, e); }
            _terminated = true; _phase = "已请求终止，等待进程结束"; return true;
        }
    }
    public void Reconciling() { lock (_gate) _phase = "重新读取仓库状态"; }
    public void Complete(string? failure)
    {
        lock (_gate)
        {
            _running = false; _clock.Stop(); _phase = _terminated ? "已终止 · 请核对结果" : failure is null ? "已完成" : "未完成 · 查看输出";
            if (failure is not null) Append("\n" + failure + "\n");
        }
    }
}

public sealed class GitWriteTerminatedException : IOException
{
    public GitWriteTerminatedException() : base("已请求终止本次 Git 进程。操作可能已部分完成，请核对工作区、索引、HEAD 及远程结果。未自动重试，也未删除锁文件。") { }
}
