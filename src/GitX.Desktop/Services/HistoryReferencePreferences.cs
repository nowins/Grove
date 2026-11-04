using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitX.Desktop.Services;

internal sealed record HistoryReferencePreferences(IReadOnlyList<string> Recent, IReadOnlyList<string> Favorites)
{
    public static HistoryReferencePreferences Empty { get; } = new([], []);
}

// Each working-directory identity has its own local-only file. Shared Git
// metadata does not combine preferences belonging to different worktrees.
internal sealed class HistoryReferencePreferenceStore(string directory)
{
    public const int RecentLimit = 16, FavoriteLimit = 256;
    private readonly SemaphoreSlim _gate = new(1);
    private sealed record Persisted(int Version, string[]? Recent, string[]? Favorites);
    public static bool IsReference(string? value) => value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl) &&
        (value.StartsWith("refs/heads/", StringComparison.Ordinal) || value.StartsWith("refs/remotes/", StringComparison.Ordinal) || value.StartsWith("refs/tags/", StringComparison.Ordinal));
    private string FilePath(string root)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        return Path.Combine(directory, "history-ref-preferences", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant() + ".json");
    }
    public Task<HistoryReferencePreferences> ReadAsync(string root) => AccessAsync(root, null, false);
    public Task<HistoryReferencePreferences> RememberAsync(string root, string key) => AccessAsync(root, key, false);
    public Task<HistoryReferencePreferences> ToggleFavoriteAsync(string root, string key) => AccessAsync(root, key, true);
    private async Task<HistoryReferencePreferences> AccessAsync(string root, string? key, bool favorite)
    {
        if (key is not null && !IsReference(key)) throw new ArgumentException("Expected canonical branch or tag reference.");
        var path = FilePath(root); await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                Persisted? saved = null;
                try { if (File.Exists(path) && new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("历史引用偏好文件超过读取上限，原文件保持不变。"); if (File.Exists(path)) saved = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path)); }
                catch (JsonException) { }
                if (saved is not null && saved.Version != 1) throw new IOException("历史引用偏好格式不受支持，原文件保持不变。");
                static List<string> Valid(string[]? values, int limit) => (values ?? []).Where(IsReference).Distinct(StringComparer.Ordinal).Take(limit).ToList();
                var recent = Valid(saved?.Recent, RecentLimit); var favorites = Valid(saved?.Favorites, FavoriteLimit);
                if (key is not null)
                {
                    if (favorite)
                    {
                        if (!favorites.Remove(key))
                        {
                            if (favorites.Count >= FavoriteLimit) throw new IOException("最多收藏 256 个引用，请先取消部分收藏。");
                            favorites.Insert(0, key);
                        }
                    }
                    else { recent.Remove(key); recent.Insert(0, key); recent = recent.Take(RecentLimit).ToList(); }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N");
                    try { File.WriteAllText(temp, JsonSerializer.Serialize(new Persisted(1, recent.ToArray(), favorites.ToArray()))); File.Move(temp, path, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                return new HistoryReferencePreferences(recent.ToArray(), favorites.ToArray());
            });
        }
        finally { _gate.Release(); }
    }
}
