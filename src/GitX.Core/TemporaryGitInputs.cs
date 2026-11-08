using System.Diagnostics;

namespace GitX.Core;

// Only the fixed files in this invocation's private directory are deleted.
// GitReadProcess owns cleanup after handoff, including cancelled queue entries
// and the uncommon case where termination needs a background exit waiter.
internal sealed class TemporaryGitInputs
{
    public string DirectoryPath { get; }
    private readonly string[] _names;

    public TemporaryGitInputs(string area, params string[] names)
    {
        if (area is not ("diff" or "merge") || names.Any(name => string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0))
            throw new ArgumentException("临时输入名称无效。");
        _names = names.ToArray();
        DirectoryPath = Path.Combine(Path.GetTempPath(), "GitX", area, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public async Task CleanupAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var name in _names) File.Delete(Path.Combine(DirectoryPath, name));
                if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A root's exit does not prove all inherited child handles have
                // closed. Retry sharing failures without masking the read error.
                if (attempt >= 8)
                { Trace.TraceWarning("Git 临时输入清理未完成：{0} ({1})", DirectoryPath, e.GetType().Name); return; }
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }
}
