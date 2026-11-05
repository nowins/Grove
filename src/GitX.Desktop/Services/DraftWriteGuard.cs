using GitX.Core;
namespace GitX.Desktop.Services;
internal sealed record DraftRestoreResult(bool? Restored);
internal sealed record DraftWriteSession(string RootKey, string Path, bool Dirty, bool Busy);
internal static class DraftWriteGuard
{
    public static IEnumerable<string> Paths(DiscardRecovery recovery) => recovery.Files is { } files ? files.Select(f => f.Path) : [recovery.Path];
    public static bool CanRestore(DiscardRecovery recovery, IEnumerable<DraftWriteSession> sessions)
        => CanWrite(recovery.Root, Paths(recovery), sessions);
    public static bool CanWrite(string repository, IEnumerable<string> paths, IEnumerable<DraftWriteSession> sessions)
    {
        var affected = new HashSet<string>(paths, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var root = WorkingTextFiles.RootKey(repository);
        return !sessions.Any(s => s.RootKey == root && affected.Contains(s.Path) && (s.Dirty || s.Busy));
    }
}
