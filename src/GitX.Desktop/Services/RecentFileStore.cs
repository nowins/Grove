using System.Text.Json;

namespace GitX.Desktop.Services;

internal sealed record RecentFile(string Root, string Path, string? Revision = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Context => Revision is null ? "工作树" : Revision[..Math.Min(8, Revision.Length)];
}

// Local-only, bounded MRU. Worktree roots are separate identities, including
// worktrees sharing the same .git/common directory. No file contents are saved.
internal sealed class RecentFileStore(string directory)
{
    internal const int PerRepositoryLimit = 64, RepositoryLimit = 32;
    private readonly SemaphoreSlim _gate = new(1);
    private string FilePath => System.IO.Path.Combine(directory, "recent-files.json");
    private static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public Task<IReadOnlyList<RecentFile>> ReadAsync(string root) => AccessAsync(root, null, null);
    public Task<IReadOnlyList<RecentFile>> RememberAsync(RecentFile file) => AccessAsync(file.Root, file, null);
    public Task<IReadOnlyList<RecentFile>> ForgetAsync(RecentFile file) => AccessAsync(file.Root, null, file);
    public static bool SafePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 &&
        !System.IO.Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') &&
        !path.Any(char.IsControl) && path.Split('/').All(p => p.Length > 0 && p is not ("." or "..") && !p.Equals(".git", StringComparison.OrdinalIgnoreCase));
    public static bool SafeRevision(string? revision) => revision is null || revision.Length is 40 or 64 && revision.All(Uri.IsHexDigit);
    private static string Normalize(string root) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
    private static RecentFile? Valid(RecentFile? file)
    {
        if (file is null || !SafePath(file.Path) || !SafeRevision(file.Revision) || !System.IO.Path.IsPathFullyQualified(file.Root ?? "")) return null;
        try { return file with { Root = Normalize(file.Root!) }; }
        catch (ArgumentException) { return null; }
    }
    private static bool Same(RecentFile a, RecentFile b) => Paths.Equals(a.Root, b.Root) && Paths.Equals(a.Path, b.Path) && a.Revision == b.Revision;

    public static IReadOnlyList<RecentFile> Filter(IEnumerable<RecentFile> files, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return files.Where(f => words.All(w => f.Path.Contains(w, StringComparison.OrdinalIgnoreCase) ||
            f.Context.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private async Task<IReadOnlyList<RecentFile>> AccessAsync(string root, RecentFile? remember, RecentFile? forget)
    {
        root = Normalize(root);
        if (remember is not null && (!SafePath(remember.Path) || !SafeRevision(remember.Revision))) throw new ArgumentException("Invalid recent file.");
        await _gate.WaitAsync();
        try
        {
            return await Task.Run<IReadOnlyList<RecentFile>>(() =>
            {
                RecentFile?[] previous;
                try { previous = File.Exists(FilePath) && new FileInfo(FilePath).Length <= 2 * 1024 * 1024 ? JsonSerializer.Deserialize<RecentFile?[]>(File.ReadAllText(FilePath)) ?? [] : []; }
                catch (JsonException) { previous = []; }
                var valid = previous.Select(Valid).OfType<RecentFile>().ToList();
                if (remember is not null) valid.Insert(0, remember with { Root = root });
                if (forget is not null) valid.RemoveAll(f => Same(f, forget with { Root = root }));
                var rows = new List<RecentFile>(); var counts = new Dictionary<string, int>(Paths);
                foreach (var file in valid)
                {
                    if (!counts.ContainsKey(file.Root) && counts.Count >= RepositoryLimit ||
                        counts.GetValueOrDefault(file.Root) >= PerRepositoryLimit || rows.Any(f => Same(f, file))) continue;
                    counts[file.Root] = counts.GetValueOrDefault(file.Root) + 1; rows.Add(file);
                }
                if (remember is not null || forget is not null)
                {
                    Directory.CreateDirectory(directory); var temp = FilePath + "." + Guid.NewGuid().ToString("N");
                    try { File.WriteAllText(temp, JsonSerializer.Serialize(rows)); File.Move(temp, FilePath, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                return rows.Where(f => Paths.Equals(f.Root, root)).ToArray();
            });
        }
        finally { _gate.Release(); }
    }
}
