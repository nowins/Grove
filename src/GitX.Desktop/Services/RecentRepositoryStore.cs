using System.Text.Json;

namespace GitX.Desktop.Services;

// Only successfully opened repository roots are remembered. All disk work is off the UI thread.
internal sealed class RecentRepositoryStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1);
    private string FilePath => Path.Combine(directory, "recent-repositories.json");
    public Task<IReadOnlyList<string>> ReadAsync() => AccessAsync(null);
    public Task<IReadOnlyList<string>> RememberAsync(string root) => AccessAsync(root);

    private async Task<IReadOnlyList<string>> AccessAsync(string? root)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run<IReadOnlyList<string>>(() =>
            {
                string[] previous;
                try { previous = File.Exists(FilePath) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? [] : []; }
                catch (JsonException) { previous = []; }
                var paths = (root is null ? previous : previous.Prepend(root))
                    .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
                    .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Take(16).ToArray();
                if (root is not null)
                {
                    Directory.CreateDirectory(directory);
                    var temp = FilePath + "." + Guid.NewGuid().ToString("N");
                    try { File.WriteAllText(temp, JsonSerializer.Serialize(paths)); File.Move(temp, FilePath, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                return paths;
            });
        }
        finally { _gate.Release(); }
    }
}
