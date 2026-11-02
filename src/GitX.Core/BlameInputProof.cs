using System.Collections;
using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

// A proof for the ordinary, include-free Git configuration/attribute sources.
// Unknown sources keep the full Git recheck. Missing files are inputs too.
internal sealed record BlameInputProof(string[] Files, string Fingerprint)
{
    internal sealed record ConfigEntry(string Origin, string Name, string Value);
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static ConfigEntry[]? ParseConfiguration(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length % 2 != 0) return null;
        var result = new List<ConfigEntry>();
        for (var i = 0; i < fields.Length; i += 2)
        {
            var separator = fields[i + 1].IndexOf('\n');
            var name = separator < 0 ? fields[i + 1] : fields[i + 1][..separator];
            if (name.IndexOf('.') < 1) return null;
            result.Add(new(fields[i], name.ToLowerInvariant(), separator < 0 ? "" : fields[i + 1][(separator + 1)..]));
        }
        return result.ToArray();
    }

    public bool Covers(IReadOnlyList<ConfigEntry> entries, string root)
    {
        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith("include.") || entry.Name.StartsWith("includeif.") || entry.Name == "core.attributesfile") return false;
            if (entry.Origin == "command line:" && entry.Name == "core.quotepath" && entry.Value == "false") continue;
            if (!entry.Origin.StartsWith("file:")
                || !Files.Contains(Path.GetFullPath(entry.Origin[5..], root), Paths)) return false;
        }
        return true;
    }

    public static async Task<BlameInputProof?> CaptureAsync(string root, string path, CancellationToken token, string[]? defaultFiles = null)
    {
        try
        {
            if (Environment.GetEnvironmentVariables().Values.Cast<string>().Any(value => value.IndexOfAny(['\r', '\n']) >= 0)) return null;
            // Git lists these source paths after configuration/other variables.
            // One process, without assuming an installation path or home directory.
            var defaults = defaultFiles ?? ParseDefaultFiles(await GitReadProcess.RunAsync(root, ["var", "-l"], token).ConfigureAwait(false));
            if (defaults is null) return null;
            return await Task.Run(() =>
            {
                var files = new HashSet<string>(defaults, Paths);
                using var repo = new Repository(root);
                var metadata = Path.GetFullPath(repo.Info.Path);
                var commonFile = Path.Combine(metadata, "commondir"); files.Add(commonFile);
                var common = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(metadata, File.ReadAllText(commonFile).Trim())) : metadata;
                foreach (var directory in new[] { metadata, common })
                    foreach (var name in new[] { "config", "config.worktree", "info/attributes" }) files.Add(Path.GetFullPath(Path.Combine(directory, name)));
                var working = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Info.WorkingDirectory));
                for (var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(working, path))); directory is not null; directory = Path.GetDirectoryName(directory))
                {
                    if (!Paths.Equals(directory, working) && !directory.StartsWith(working + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
                    files.Add(Path.Combine(directory, ".gitattributes"));
                    if (Paths.Equals(directory, working)) break;
                }
                var inputs = files.Order(Paths).ToArray(); var fingerprint = HashFiles(inputs, token);
                return fingerprint is null ? null : new BlameInputProof(inputs, fingerprint);
            }, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibGit2SharpException or ArgumentException)
        { return null; }
    }

    internal static string[]? ParseDefaultFiles(string output)
    {
        // var -l is not NUL-delimited. Accept only the complete, known trailing
        // source block; configurations can contain newlines and fake prefixes.
        // Unusual/missing defaults or a different Git protocol keep the recheck.
        var names = new[] { "GIT_ATTR_SYSTEM=", "GIT_ATTR_GLOBAL=", "GIT_CONFIG_SYSTEM=", "GIT_CONFIG_GLOBAL=" };
        var lines = output.TrimEnd('\n').Split('\n');
        var start = Array.FindLastIndex(lines, line => line.StartsWith(names[0], StringComparison.Ordinal));
        if (start < 0 || lines.Length - start is < 4 or > 5) return null;
        var result = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            var prefix = names[Math.Min(i - start, 3)];
            if (!lines[i].StartsWith(prefix, StringComparison.Ordinal)) return null;
            var file = lines[i][prefix.Length..];
            if (!Path.IsPathFullyQualified(file) || file.Contains('\r') || file.Contains('\0')) return null;
            result.Add(Path.GetFullPath(file));
        }
        return result.ToArray();
    }

    public Task<bool> UnchangedAsync(CancellationToken token) => Task.Run(() => Fingerprint == HashFiles(Files, token), token);

    // Full bytes, presence and file kind, with strict read budgets. Never mtime-only.
    internal static string? HashFiles(IEnumerable<string> paths, CancellationToken token)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().OrderBy(e => (string)e.Key, StringComparer.Ordinal))
                hash.AppendData(Encoding.UTF8.GetBytes($"{entry.Key}\0{entry.Value}\0"));
            hash.AppendData(Encoding.UTF8.GetBytes(TimeZoneInfo.Local.Id));
            var buffer = new byte[65536]; long total = 0;
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested(); hash.AppendData(Encoding.UTF8.GetBytes("\0" + path + "\0"));
                if (!File.Exists(path))
                {
                    if (Directory.Exists(path)) return null;
                    hash.AppendData("missing\0"u8); continue;
                }
                var attributes = File.GetAttributes(path);
                hash.AppendData(BitConverter.GetBytes((int)(attributes & FileAttributes.ReparsePoint)));
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                if (length > 2 * 1024 * 1024) return null;
                hash.AppendData(BitConverter.GetBytes(length));
                long read = 0; int count;
                while ((count = stream.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested(); read += count; total += count;
                    if (read > 2 * 1024 * 1024 || total > 8 * 1024 * 1024) return null;
                    hash.AppendData(buffer.AsSpan(0, count));
                }
                if (read != length || stream.Length != length) return null;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
