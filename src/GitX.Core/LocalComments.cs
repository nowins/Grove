using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace GitX.Core;

// Captured with the document, never reconstructed from a later HEAD or line number.
public sealed record CommentContext(string RepositoryId, string Root, string Path, ComparisonKind Kind,
    string Revision, string Snapshot)
{
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string RepositoryIdFor(Repository repository)
    {
        static string Canonical(string path) => OperatingSystem.IsWindows()
            ? System.IO.Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant()
            : System.IO.Path.GetFullPath(path).TrimEnd('/');
        return Hash(Canonical(repository.Info.WorkingDirectory) + "\n" + Canonical(repository.Info.Path));
    }
    internal static CommentContext Create(Repository repository, DocumentRequest request, string revision, DiffSide? oldSide, DiffSide newSide) =>
        new(RepositoryIdFor(repository), System.IO.Path.TrimEndingDirectorySeparator(repository.Info.WorkingDirectory), request.Path, request.Kind, revision,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.Kind, request.Path, oldSide, newSide, revision }))).ToLowerInvariant());
}

public sealed record CommentAnchor(CommentContext Context, int Side, string Path, string Label, int StartLine, int EndLine, string Code, string Language)
{
    public static CommentAnchor Capture(RepositoryDocument document, int side, int start, int end)
    {
        var context = document.CommentContext ?? throw new IOException("当前内容不支持行评论。");
        if (side is < 0 or > 1 || (document.Diff is null && side != 1)) throw new IOException("评论侧无效。");
        var source = document.Diff is { } diff ? side == 0 ? diff.Old : diff.New : new DiffSide(document.Path, "工作区", document.Text, true);
        var lines = SourceLines(source.Text);
        if (!source.Exists || start < 1 || end < start || end > lines.Length || end - start >= 200)
            throw new IOException("请选择实际代码行，单条评论最多关联 200 行；空文件和不存在的一侧不能添加行评论。");
        var code = string.Join('\n', lines[(start - 1)..end]);
        if (code.Length > 64 * 1024) throw new IOException("所选代码超过 64 KiB，请缩小范围。");
        return new(context, side, source.Path, source.Label, start, end, code, document.Language);
    }
    public bool Matches(CommentContext? current) => current is not null && Context == current;
    public static string[] SourceLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}

public sealed record LocalComment(string Id, string Revision, CommentAnchor Anchor, string Body, DateTimeOffset Created, DateTimeOffset Updated);

// Per-user, per-worktree data. Atomic replacement + an exclusive lock coordinate
// different windows/processes; optimistic record revisions reject stale edits.
public sealed class LocalCommentStore(string directory)
{
    private sealed record FileData(int Format, string RepositoryId, IReadOnlyList<LocalComment> Comments);
    private const int MaxBytes = 16 * 1024 * 1024;
    private string FilePath(string repositoryId)
    {
        if (repositoryId.Length != 64 || !repositoryId.All(Uri.IsHexDigit)) throw new IOException("评论仓库标识无效。");
        return Path.Combine(directory, repositoryId + ".json");
    }
    public Task<IReadOnlyList<LocalComment>> ReadAsync(string repositoryId) => Task.Run(() => Read(repositoryId));
    private IReadOnlyList<LocalComment> Read(string repositoryId)
    {
        var path = FilePath(repositoryId);
        if (!File.Exists(path)) return [];
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaxBytes) throw new IOException("评论文件超过 16 MiB，未加载或覆盖。");
            var file = JsonSerializer.Deserialize<FileData>(input);
            if (file is null || file.Format != 1 || file.RepositoryId != repositoryId || file.Comments is null || file.Comments.Count > 1000)
                throw new IOException("评论文件版本或仓库标识不匹配，未覆盖原数据。");
            if (file.Comments.Any(c => c is null || c.Anchor?.Context?.RepositoryId != repositoryId || c.Body is null || c.Body.Length > 16000 || c.Anchor.Code is null || c.Anchor.Code.Length > 65536)
                || file.Comments.Select(c => c.Id).Distinct().Count() != file.Comments.Count)
                throw new IOException("评论文件内容无效，未覆盖原数据。");
            return file.Comments;
        }
        catch (JsonException e) { throw new IOException("评论文件损坏，未覆盖原数据。请备份后检查 JSON。", e); }
    }
    public Task<LocalComment> SaveAsync(CommentAnchor anchor, string body, LocalComment? original = null) => Task.Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 16000) throw new IOException("评论不能为空，且最多 16,000 字符。");
        using var lease = await LockAsync(anchor.Context.RepositoryId).ConfigureAwait(false);
        var items = Read(anchor.Context.RepositoryId).ToList();
        var now = DateTimeOffset.UtcNow;
        var saved = new LocalComment(original?.Id ?? Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), anchor, body, original?.Created ?? now, now);
        if (original is not null)
        {
            var index = items.FindIndex(c => c.Id == original.Id && c.Revision == original.Revision && c.Anchor == anchor);
            if (index < 0) throw new IOException("此评论已被其他窗口修改或删除，请刷新评论列表后重试。");
            items[index] = saved;
        }
        else
        {
            if (items.Count >= 1000) throw new IOException("此仓库已保存 1,000 条评论，请先导出并清理。");
            items.Add(saved);
        }
        Write(anchor.Context.RepositoryId, items);
        return saved;
    });
    public Task DeleteAsync(LocalComment original) => Task.Run(async () =>
    {
        using var lease = await LockAsync(original.Anchor.Context.RepositoryId).ConfigureAwait(false);
        var items = Read(original.Anchor.Context.RepositoryId).ToList();
        if (items.RemoveAll(c => c.Id == original.Id && c.Revision == original.Revision) != 1)
            throw new IOException("此评论已被其他窗口修改或删除，请刷新评论列表后重试。");
        Write(original.Anchor.Context.RepositoryId, items);
    });
    private async Task<FileStream> LockAsync(string id)
    {
        Directory.CreateDirectory(directory);
        var path = FilePath(id) + ".lock";
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // Contended saves must yield the worker instead of starving the thread
        // pool that the lock owner needs to finish. Keep cross-process exclusion
        // and a bounded monotonic wait; never remove another writer's lock file.
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
            { await Task.Delay(50).ConfigureAwait(false); }
        }
    }
    private void Write(string id, IReadOnlyList<LocalComment> comments)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new FileData(1, id, comments));
        if (bytes.Length > MaxBytes) throw new IOException("评论总量超过 16 MiB，请先导出并清理。");
        var path = FilePath(id); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class CommentMarkdown
{
    public static string Export(IEnumerable<LocalComment> comments)
    {
        var result = new StringBuilder("# Grove 本地行评论\n\n行号与代码均为评论保存时的快照；未对当前工作区重新定位。\n\n");
        static string Fence(string text) => new('`', Math.Max(3, Regex.Matches(text, "`+").Select(m => m.Length + 1).DefaultIfEmpty(3).Max()));
        static string Inline(string text) => System.Net.WebUtility.HtmlEncode(text).Replace("\r", " ").Replace("\n", " ");
        foreach (var comment in comments.OrderBy(c => c.Created).ThenBy(c => c.Id))
        {
            var a = comment.Anchor;
            result.Append("## <code>").Append(Inline(a.Path)).Append("</code> · ").Append(a.Side == 0 ? "旧侧" : "新侧")
                .Append(" · L").Append(a.StartLine).Append('–').Append(a.EndLine).Append("\n\n")
                .Append("- 仓库：<code>").Append(Inline(a.Context.Root)).Append("</code>\n")
                .Append("- 比较：").Append(a.Context.Kind).Append(" · ").Append(Inline(a.Label)).Append('\n')
                .Append("- 版本：<code>").Append(Inline(a.Context.Revision)).Append("</code>\n")
                .Append("- 快照：<code>").Append(a.Context.Snapshot).Append("</code>\n")
                .Append("- 评论时间：").Append(comment.Updated.ToString("O")).Append("\n\n");
            var fence = Fence(a.Code);
            result.Append(fence).Append(Regex.IsMatch(a.Language, "^[a-zA-Z0-9-]+$") ? a.Language : "text").Append('\n')
                .Append(a.Code).Append('\n').Append(fence).Append("\n\n");
            // Plain text notes remain literal even if they contain Markdown/HTML.
            fence = Fence(comment.Body);
            result.Append("评论：\n\n").Append(fence).Append("text\n").Append(comment.Body).Append('\n').Append(fence).Append("\n\n");
        }
        return result.ToString();
    }
}
