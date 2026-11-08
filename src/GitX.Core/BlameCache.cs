using System.Collections;
using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

// Per-reader, bounded annotations only: no repository handles, documents or live tasks.
internal sealed class BlameCache
{
    internal sealed record Input(string? Key, BlameInputProof? Proof, string? Metadata = null, string? Environment = null);
    internal sealed record Sources(string Environment, string[] Files);
    private sealed record Entry(string Identity, string Key, IReadOnlyList<BlameLine> Lines, long Characters, Input? Input);
    private readonly object _gate = new();
    private readonly LinkedList<Entry> _entries = new();
    private long _characters, _generation;
    public long Generation { get { lock (_gate) return _generation; } }
    public bool MayContain(string identity) { lock (_gate) return _entries.Any(entry => entry.Identity == identity); }
    public Input? ProvenInput(string identity) { lock (_gate) return _entries.FirstOrDefault(entry => entry.Identity == identity && entry.Input?.Proof is not null)?.Input; }
    public void Clear() { lock (_gate) { _entries.Clear(); _characters = 0; ++_generation; } }
    public IReadOnlyList<BlameLine>? Get(string key)
    {
        lock (_gate)
        {
            var node = _entries.First;
            while (node is not null && node.Value.Key != key) node = node.Next;
            if (node is null) return null;
            _entries.Remove(node); _entries.AddFirst(node); return node.Value.Lines;
        }
    }
    public void Put(string identity, string key, IReadOnlyList<BlameLine> lines, long generation, Input? input = null)
    {
        // Count every field even when strings are shared, plus a fixed allowance
        // for each record. The budget is conservative, not a heap measurement.
        var characters = lines.Sum(line => 64L + line.Sha.Length + line.Author.Length + line.When.Length + line.Summary.Length
            + line.OriginalPath.Length + line.PreviousSha.Length + line.PreviousPath.Length);
        characters += input?.Proof?.Files.Sum(file => (long)file.Length + 32) ?? 0;
        if (characters > 2 * 1024 * 1024) return;
        lock (_gate)
        {
            if (generation != _generation) return;
            var previous = _entries.First;
            while (previous is not null && previous.Value.Key != key) previous = previous.Next;
            if (previous is not null) { _characters -= previous.Value.Characters; _entries.Remove(previous); }
            _entries.AddFirst(new Entry(identity, key, lines, characters, input)); _characters += characters;
            while (_entries.Count > 4 || _characters > 2 * 1024 * 1024)
            { _characters -= _entries.Last!.Value.Characters; _entries.RemoveLast(); }
        }
    }

    public static async Task<Input> ReadInputAsync(string root, string path, string revision, string state, bool working, CancellationToken token, bool captureProof = false, Sources? sources = null)
    {
        // Configuration values stay private; only their digest becomes a key.
        // Resolve includes/conditional includes with the same Git as blame.
        try
        {
            var environment = EnvironmentFingerprint();
            var proof = captureProof ? await BlameInputProof.CaptureAsync(root, path, token,
                sources?.Environment == environment ? sources.Files : null).ConfigureAwait(false) : null;
            var config = GitReadProcess.RunAsync(root, ["config", "--show-origin", "--null", "--list"], token);
            var attributes = GitReadProcess.RunAsync(root, ["check-attr", "-z", "--all", "--", path], token);
            await Task.WhenAll(config, attributes).ConfigureAwait(false);
            var entries = BlameInputProof.ParseConfiguration(config.Result);
            if (entries is null || entries.Any(entry => entry.Name.StartsWith("mailmap.") || entry.Name == "blame.ignorerevsfile")) return new(null, null);
            var fields = attributes.Result.Split('\0');
            for (var i = 1; i + 1 < fields.Length; i += 3)
                if (fields[i] is "filter" or "diff" && fields[i + 1] is not ("unset" or "unspecified")) return new(null, null);
            var metadata = await ReadWork.Cpu.Run(() => Metadata(root, token), token).ConfigureAwait(false);
            if (metadata is null) return new(null, null);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\0', metadata, path, revision, state, working ? "working" : "revision", TimeZoneInfo.Local.Id, config.Result, attributes.Result))));
            if (proof is not null && (environment != EnvironmentFingerprint() || !proof.Covers(entries, root) || !await proof.UnchangedAsync(token).ConfigureAwait(false))) proof = null;
            return new(key, proof, metadata, environment);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibGit2SharpException)
        { return new(null, null); } // A failed cache guard must not prevent a fresh Git read.
    }

    public static async Task<string?> VerifyInputAsync(Input input, string root, string path, string revision, string state, bool working, CancellationToken token)
    {
        if (await ProvenUnchangedAsync(input, root, token).ConfigureAwait(false)) return input.Key;
        return (await ReadInputAsync(root, path, revision, state, working, token).ConfigureAwait(false)).Key;
    }

    public static async Task<bool> ProvenUnchangedAsync(Input input, string root, CancellationToken token)
    {
        try
        {
            return input.Proof is not null && input.Environment == EnvironmentFingerprint() && await input.Proof.UnchangedAsync(token).ConfigureAwait(false)
                && await ReadWork.Cpu.Run(() => Metadata(root, token), token).ConfigureAwait(false) is { } metadata && metadata == input.Metadata;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibGit2SharpException or ArgumentException)
        { return false; }
    }

    public static bool SourcesCurrent(Sources sources) => sources.Environment == EnvironmentFingerprint();
    public static async Task<Sources?> ReadSourcesAsync(string root, CancellationToken token)
    {
        var environment = EnvironmentFingerprint();
        var files = BlameInputProof.ParseDefaultFiles(await GitReadProcess.RunAsync(root, ["var", "-l"], token, priority: ReadPriority.Background).ConfigureAwait(false));
        return files is not null && environment == EnvironmentFingerprint() ? new(environment, files) : null;
    }
    private static string EnvironmentFingerprint()
    {
        // Includes HOME/XDG/PATH and Git overrides: a source list resolved in
        // one environment is never proof for a later, different environment.
        var values = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().OrderBy(pair => (string)pair.Key, StringComparer.Ordinal)
            .Select(pair => new[] { (string)pair.Key, (string?)pair.Value });
        return Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { TimeZoneInfo.Local.Id, values })));
    }

    private static string? Metadata(string root, CancellationToken token)
    {
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = (string)variable.Key;
            if (name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase)
                && name is not ("GIT_PAGER" or "GIT_OPTIONAL_LOCKS" or "GIT_TERMINAL_PROMPT" or "GIT_LITERAL_PATHSPECS")) return null;
        }
        using var repo = new Repository(root);
        var metadata = Path.GetFullPath(repo.Info.Path);
        var commonFile = Path.Combine(metadata, "commondir");
        var common = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(metadata, File.ReadAllText(commonFile).Trim())) : metadata;
        if (File.Exists(Path.Combine(repo.Info.WorkingDirectory, ".mailmap")) || repo.Head.Tip?.Tree[".mailmap"] is not null) return null;
        foreach (var directory in new[] { metadata, common }.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (new[] { "shallow", "info/grafts", "objects/info/alternates" }.Any(name => File.Exists(Path.Combine(directory, name)))
                || Directory.Exists(Path.Combine(directory, "refs/replace"))) return null;
            var packed = Path.Combine(directory, "packed-refs");
            if (File.Exists(packed) && (new FileInfo(packed).Length > 2 * 1024 * 1024 || File.ReadAllText(packed).Contains("refs/replace/", StringComparison.Ordinal))) return null;
        }
        return OperatingSystem.IsWindows() ? metadata.ToUpperInvariant() : metadata;
    }
}
