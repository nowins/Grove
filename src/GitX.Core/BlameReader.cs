using System.Globalization;
using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

public sealed record BlameLine(int Line, string Sha, string Author, string When, string Summary, int OriginalLine, string OriginalPath = "",
    string PreviousSha = "", string PreviousPath = "", int? CommitNumber = null)
{
    public bool IsCommitted => Sha.Length == 40 && Sha.All(Uri.IsHexDigit) && Sha.Any(c => c != '0');
    public bool HasPrevious => PreviousSha.Length == 40 && PreviousSha.All(Uri.IsHexDigit) && PreviousSha.Any(c => c != '0') && PreviousPath.Length > 0;
}
public sealed record BlameRevision(string Sha, string Path, int Line = 1);
public sealed class BlameReader
{
    private readonly BlameCache _cache = new();
    private BlameCache.Sources? _sources;
    private int _preparingSources;
    // Discover Git's default config/attribute locations while source is being
    // read. This does not run blame/log or retain file contents.
    public async Task PrepareAsync(string root, CancellationToken cancellation = default)
    {
        if (_sources is { } current && BlameCache.SourcesCurrent(current) || Interlocked.Exchange(ref _preparingSources, 1) != 0) return;
        try { Volatile.Write(ref _sources, await BlameCache.ReadSourcesAsync(root, cancellation).ConfigureAwait(false)); }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException) { }
        finally { Volatile.Write(ref _preparingSources, 0); }
    }
    public void ClearCache() => _cache.Clear();
    public async Task<RepositoryDocument> ReadAsync(string root, string path, CancellationToken cancellation = default)
    {
        var before = await ReadWork.Cpu.Run(() => RepositoryState.Token(root, [path], cancellation), cancellation);
        using var reader = new RepositoryReader();
        var document = await reader.DocumentAsync(new(root, path, ComparisonKind.WorkingFile), cancellation);
        if (document.IsNotice) return document;
        var lineCount = document.Text.Length == 0 ? 0 : document.Text.Count(c => c == '\n') + (document.Text.EndsWith('\n') ? 0 : 1);
        if (lineCount > 5000) throw new IOException("注解暂限 5,000 行，请缩小文件后重试。");
        var head = await ReadWork.Cpu.Run(() => { using var repo = new Repository(root); return repo.Head.Tip?.Tree[path] is not null ? repo.Head.Tip.Sha : null; }, cancellation);
        IReadOnlyList<BlameLine> lines;
        if (head is null)
            lines = Enumerable.Range(1, document.Text.Length == 0 ? 0 : document.Text.Count(c => c == '\n') + (document.Text.EndsWith('\n') ? 0 : 1))
                .Select(i => new BlameLine(i, new string('0', 40), "未提交", "", "尚未进入 HEAD", i)).ToArray();
        else
        {
            lines = await AnnotationsAsync(root, path, head, before, lineCount, working: true, cancellation);
        }
        var after = await ReadWork.Cpu.Run(() => RepositoryState.Token(root, [path], cancellation), cancellation);
        if (before != after) throw new IOException("注解读取期间文件或 HEAD 已变化，请重试。");
        return document with { Blame = lines, Detail = $"工作区注解 · {lines.Count} 行 · 悬停查看提交 / 作者 / 日期" };
    }
    public static IReadOnlyList<BlameLine> Parse(string output)
    {
        var result = new List<BlameLine>(); string sha = "", author = "", summary = "", date = "", path = "", previousSha = "", previousPath = ""; int line = 0, original = 0;
        var metadata = new Dictionary<string, (string Author, string Summary, string Date, string Path, string PreviousSha, string PreviousPath)>();
        foreach (var item in output.Split('\n'))
        {
            if (item.Length >= 40 && item[..40].All(Uri.IsHexDigit) && item.Length > 40 && item[40] == ' ')
            {
                var fields = item.Split(' '); sha = fields[0]; original = int.Parse(fields[1], CultureInfo.InvariantCulture); line = int.Parse(fields[2], CultureInfo.InvariantCulture);
                (author, summary, date, path, previousSha, previousPath) = metadata.GetValueOrDefault(sha, ("", "", "", "", "", ""));
            }
            else if (item.StartsWith("author ")) author = item[7..];
            else if (item.StartsWith("author-time ") && long.TryParse(item[12..], out var timestamp)) date = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else if (item.StartsWith("summary ")) summary = item[8..];
            else if (item.StartsWith("filename ")) path = DecodePath(item[9..]);
            else if (item.StartsWith("previous ") && item.Length > 50 && item[49] == ' ')
            { previousSha = item[9..49]; previousPath = DecodePath(item[50..]); }
            else if (item.StartsWith('\t'))
            {
                metadata[sha] = (author, summary, date, path, previousSha, previousPath);
                result.Add(new(line, sha, sha.All(c => c == '0') ? "未提交" : author, date, summary, original, path, previousSha, previousPath));
            }
        }
        return result;
    }

    public async Task<RepositoryDocument> ReadRevisionAsync(string root, BlameRevision revision, bool annotate = true, CancellationToken cancellation = default)
    {
        using var reader = new RepositoryReader();
        var document = await reader.DocumentAsync(new(root, revision.Path, ComparisonKind.RevisionFile, revision.Sha), cancellation);
        if (document.IsNotice || !annotate) return document;
        var count = document.Text.Count(c => c == '\n') + (document.Text.Length > 0 && !document.Text.EndsWith('\n') ? 1 : 0);
        if (count > 5000) throw new IOException("注解暂限 5,000 行。");
        var before = await ReadWork.Cpu.Run(() => RepositoryState.Token(root, [], cancellation), cancellation);
        var lines = await AnnotationsAsync(root, revision.Path, document.SourceRevision!, before, count, working: false, cancellation);
        return document with { Blame = lines, Detail = $"修订注解 · {document.SourceRevision![..8]} · {count} 行" };
    }

    private async Task<IReadOnlyList<BlameLine>> AnnotationsAsync(string root, string path, string revision, string state, int lineCount, bool working, CancellationToken cancellation)
    {
        var generation = _cache.Generation;
        var identity = string.Join('\0', Path.GetFullPath(root), path, revision, state, working ? "working" : "revision");
        var proven = _cache.ProvenInput(identity);
        var reusable = proven is not null && await BlameCache.ProvenUnchangedAsync(proven, root, cancellation).ConfigureAwait(false);
        var isNew = !_cache.MayContain(identity);
        var keyTask = reusable ? Task.FromResult(proven!) : BlameCache.ReadInputAsync(root, path, revision, state, working, cancellation, captureProof: true, sources: Volatile.Read(ref _sources));
        Task<IReadOnlyList<BlameLine>>? fresh = isNew ? ReadAnnotationsAsync(root, path, revision, lineCount, working, cancellation) : null;
        // A new snapshot cannot hit: overlap its guard with the first read.
        if (fresh is not null) await Task.WhenAll(keyTask, fresh).ConfigureAwait(false);
        var input = await keyTask.ConfigureAwait(false); var key = input.Key;
        var lines = key is not null ? _cache.Get(key) : null;
        lines ??= await (fresh ?? ReadAnnotationsAsync(root, path, revision, lineCount, working, cancellation)).ConfigureAwait(false);
        var after = await ReadWork.Cpu.Run(() => RepositoryState.Token(root, working ? [path] : [], cancellation), cancellation).ConfigureAwait(false);
        if (state != after) throw new IOException("注解读取期间仓库或文件已变化，请重试。");
        if (key is not null)
        {
            var verified = await BlameCache.VerifyInputAsync(input, root, path, revision, after, working, cancellation).ConfigureAwait(false);
            if (key != verified) throw new IOException("注解读取期间 Git 配置或属性已变化，请重试。");
            cancellation.ThrowIfCancellationRequested();
            _cache.Put(identity, key, lines, generation, input);
        }
        return lines;
    }

    private static async Task<IReadOnlyList<BlameLine>> ReadAnnotationsAsync(string root, string path, string revision, int lineCount, bool working, CancellationToken cancellation)
    {
        using var historyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var blame = GitReadProcess.RunAsync(root, working ? ["blame", "--incremental", "--", path] : ["blame", "--incremental", revision, "--", path], cancellation);
        var log = GitReadProcess.RunAsync(root, ["log", "--follow", "--format=%H", revision, "--", path], historyCancellation.Token);
        try
        {
            var lines = ParseIncremental(await blame.ConfigureAwait(false), lineCount);
            if (!lines.Any(line => line.IsCommitted))
            {
                // There are no commit numbers to attach. Stop and await only
                // this read's speculative history, never a write or another read.
                historyCancellation.Cancel();
                try { await log.ConfigureAwait(false); }
                catch (Exception e) when (e is OperationCanceledException or IOException && !cancellation.IsCancellationRequested) { }
                cancellation.ThrowIfCancellationRequested();
                return lines;
            }
            var history = (await log.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var numbers = history.Select((sha, index) => (sha, number: history.Length - index)).ToDictionary(pair => pair.sha, pair => pair.number);
            return lines.Select(line => line with { CommitNumber = numbers.TryGetValue(line.Sha, out var number) ? number : null }).ToArray();
        }
        finally
        {
            historyCancellation.Cancel();
            try { await Task.WhenAll(blame, log).ConfigureAwait(false); } catch { } // Observe both tasks; preserve the primary failure.
        }
    }

    internal static IReadOnlyList<BlameLine> ParseIncremental(string output, int lineCount)
    {
        if (lineCount is < 0 or > 5000) throw new InvalidDataException("Git 注解行数超出读取范围。");
        var lines = new BlameLine[lineCount];
        var metadata = new Dictionary<string, (string Author, string Date, string Summary, string PreviousSha, string PreviousPath)>();
        string sha = "", author = "", date = "", summary = "", previousSha = "", previousPath = "";
        int original = 0, result = 0, count = 0, assigned = 0;
        foreach (var item in output.Split('\n'))
        {
            if (item.Length > 40 && item[40] == ' ' && item[..40].All(Uri.IsHexDigit))
            {
                var fields = item.Split(' ');
                if (count != 0 || fields.Length != 4 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out original)
                    || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out result)
                    || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out count)
                    || original < 1 || result < 1 || count < 1 || count > lineCount || result > lineCount - count + 1 || original > int.MaxValue - count + 1)
                    throw new InvalidDataException("Git 注解块的行范围无效。");
                sha = fields[0];
                (author, date, summary, previousSha, previousPath) = metadata.GetValueOrDefault(sha, ("", "", "", "", ""));
            }
            else if (item.StartsWith("author ")) author = item[7..];
            else if (item.StartsWith("author-time ") && long.TryParse(item[12..], out var timestamp))
                date = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else if (item.StartsWith("summary ")) summary = item[8..];
            else if (item.StartsWith("previous ") && item.Length > 50 && item[49] == ' ')
            { previousSha = item[9..49]; previousPath = DecodePath(item[50..]); }
            else if (item.StartsWith("filename "))
            {
                if (count == 0) throw new InvalidDataException("Git 注解块缺少行范围。");
                var path = DecodePath(item[9..]);
                metadata[sha] = (author, date, summary, previousSha, previousPath);
                for (var i = 0; i < count; i++)
                {
                    var index = result - 1 + i;
                    if (lines[index] is not null) throw new InvalidDataException("Git 注解块的行范围重叠。");
                    lines[index] = new(index + 1, sha, sha.All(c => c == '0') ? "未提交" : author, date, summary, original + i, path, previousSha, previousPath);
                }
                assigned += count; count = 0;
            }
        }
        if (count != 0 || assigned != lineCount) throw new InvalidDataException("Git 注解块未完整覆盖当前文件，请重新读取。");
        return lines;
    }

    public async Task<BlameRevision> PreviousRevisionAsync(string root, BlameLine line, string worktreeText, CancellationToken cancellation = default)
    {
        if (!line.HasPrevious) throw new IOException("此行没有更早的文件修订。");
        using var reader = new RepositoryReader();
        var previous = await reader.DocumentAsync(new(root, line.PreviousPath, ComparisonKind.RevisionFile, line.PreviousSha), cancellation);
        var current = line.IsCommitted ? await reader.DocumentAsync(new(root, line.OriginalPath, ComparisonKind.RevisionFile, line.Sha), cancellation) : null;
        if (previous.IsNotice || current?.IsNotice == true) throw new IOException(previous.IsNotice ? previous.Text : current!.Text);
        var nextText = current?.Text ?? worktreeText;
        var compared = await NativeDiff.CompareStructureAsync(new(line.PreviousPath, "前一修订", previous.Text, true),
            new(line.OriginalPath, "当前修订", nextText, true), cancellation, DiffWhitespace.Exact);
        var mapped = line.OriginalLine;
        foreach (var block in compared.Blocks)
        {
            if (line.OriginalLine <= block.NewStart) break;
            if (line.OriginalLine <= block.NewStart + block.NewCount)
            {
                mapped = block.Rows.FirstOrDefault(row => row.NewLine == line.OriginalLine)?.OldLine ?? block.OldStart + 1;
                break;
            }
            mapped -= block.NewCount - block.OldCount;
        }
        var total = previous.Text.Count(c => c == '\n') + (previous.Text.EndsWith('\n') ? 0 : 1);
        return new(line.PreviousSha, line.PreviousPath, Math.Clamp(mapped, 1, Math.Max(1, total)));
    }

    public async Task<RepositoryDocument> ReadChangeAsync(string root, BlameLine line, CancellationToken cancellation = default)
    {
        if (!line.IsCommitted) throw new IOException("此行尚未提交，没有所属提交。");
        if (string.IsNullOrEmpty(line.OriginalPath)) throw new IOException("注解缺少此修订中的原始路径，请重新打开注解。");
        var request = await ReadWork.Cpu.Run(() =>
        {
            using var repo = new Repository(root);
            var commit = repo.Lookup<Commit>(line.Sha) ?? throw new IOException("注解所属提交已不存在，请刷新注解。");
            cancellation.ThrowIfCancellationRequested();
            using var changes = repo.Diff.Compare<TreeChanges>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree,
                new LibGit2Sharp.CompareOptions { Similarity = SimilarityOptions.Renames });
            var change = changes.FirstOrDefault(c => c.Path == line.OriginalPath)
                ?? throw new IOException("此提交中未找到注解所指文件的改动。");
            return new DocumentRequest(root, change.Path, ComparisonKind.Commit, commit.Sha, change.OldPath, DiffWhitespace.Trim);
        }, cancellation);
        using var reader = new RepositoryReader();
        return await reader.DocumentAsync(request, cancellation);
    }

    private static string DecodePath(string value)
    {
        if (!value.StartsWith('"')) return value;
        var result = new StringBuilder();
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] != '\\') { result.Append(value[i]); continue; }
            var bytes = new List<byte>();
            while (i + 3 < value.Length && value[i] == '\\' && value.AsSpan(i + 1, 3).ToString().All(c => c is >= '0' and <= '7'))
            { bytes.Add(Convert.ToByte(value.Substring(i + 1, 3), 8)); i += 4; }
            if (bytes.Count > 0) { result.Append(Encoding.UTF8.GetString(bytes.ToArray())); i--; continue; }
            if (++i >= value.Length - 1) throw new IOException("Git 注解中的路径引号格式无效。");
            result.Append(value[i] switch { 'a' => '\a', 'b' => '\b', 't' => '\t', 'n' => '\n', 'v' => '\v', 'f' => '\f', 'r' => '\r', var c => c });
        }
        return result.ToString();
    }
}
