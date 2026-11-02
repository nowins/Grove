using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitX.Core;

public sealed record CodeSearchQuery(string Text, bool FilesOnly = false, bool MatchCase = false, bool WholeWord = false,
    bool RegularExpression = false, string FileMask = "");
public sealed record CodeSearchMatch(string Path, int Line, int Column, int Length, string Preview, string? Fingerprint = null, int PreviewOffset = 0);
public sealed record CodeSearchPage(IReadOnlyList<CodeSearchMatch> Matches, int SearchedFiles, int SkippedFiles, bool Truncated);

public sealed partial class RepositoryReader
{
    public const int MaxSearchResults = 1000;
    public static string SearchFingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Use the same file inventory and text limits as source browsing, including
    // untracked files and Git's ignore rules. Never scan directories on the UI thread.
    public static Task<CodeSearchPage> SearchAsync(string root, IReadOnlyList<string> paths, CodeSearchQuery query, CancellationToken token = default)
        => Task.Run(() => Search(root, paths, query, token), token);

    public static async Task<CodeSearchPage> SearchEverywhereAsync(string root, IReadOnlyList<string> paths, CodeSearchQuery query, CancellationToken token = default)
    {
        var files = SearchAsync(root, paths, query with { FilesOnly = true }, token);
        var text = SearchAsync(root, paths, query with { FilesOnly = false }, token);
        await Task.WhenAll(files, text);
        token.ThrowIfCancellationRequested();
        return new(files.Result.Matches.Concat(text.Result.Matches).Take(MaxSearchResults).ToArray(), text.Result.SearchedFiles, text.Result.SkippedFiles,
            files.Result.Truncated || text.Result.Truncated || files.Result.Matches.Count + text.Result.Matches.Count > MaxSearchResults);
    }

    private static CodeSearchPage Search(string root, IReadOnlyList<string> paths, CodeSearchQuery query, CancellationToken token)
    {
        var text = query.FilesOnly ? query.Text.Trim().Replace('\\', '/') : query.Text;
        if (text.Length == 0) return new([], 0, 0, false);
        if (text.Length > 1024 || query.FileMask.Length > 1024) throw new IOException("搜索文本和文件范围最多 1,024 字符。");
        var masks = query.FileMask.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Included(string path) => masks.Length == 0 || masks.Any(mask => FileSystemName.MatchesSimpleExpression(mask.Replace('\\', '/'), mask.Contains('/') || mask.Contains('\\') ? path : System.IO.Path.GetFileName(path), true));
        if (query.FilesOnly)
        {
            var line = 1;
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out var requested) && requested > 0) { line = requested; text = text[..colon]; }
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = paths.Where(path => { token.ThrowIfCancellationRequested(); return Included(path) && parts.All(part => path.Contains(part, StringComparison.OrdinalIgnoreCase)); })
                .OrderBy(path => System.IO.Path.GetFileName(path).Equals(text, StringComparison.OrdinalIgnoreCase) ? 0 : System.IO.Path.GetFileName(path).StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(MaxSearchResults + 1).ToArray();
            return new(matches.Take(MaxSearchResults).Select(path => new CodeSearchMatch(path, line, 1, 0, "")).ToArray(), paths.Count, 0, matches.Length > MaxSearchResults);
        }

        Regex? expression = null;
        if (query.RegularExpression || query.WholeWord)
        {
            var pattern = query.RegularExpression ? text : Regex.Escape(text);
            if (query.WholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
            try { expression = new Regex(pattern, RegexOptions.CultureInvariant | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { throw new IOException("正则表达式无效，请检查后重试。"); }
        }
        var results = new List<CodeSearchMatch>(); var searched = 0; var skipped = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!Included(path)) continue;
            string source;
            try
            {
                var (decoded, problem) = DecodeText(ReadWorktree(SafeWorktreePath(root, path)));
                if (problem is not null) { skipped++; continue; }
                source = decoded!;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
            searched++;
            var fingerprint = SearchFingerprint(source);
            var lines = source.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var content = lines[index].TrimEnd('\r');
                if (expression is not null)
                {
                    try
                    {
                        for (var match = expression.Match(content); match.Success; match = match.NextMatch())
                        {
                            token.ThrowIfCancellationRequested();
                            if (Add(match.Index, match.Length)) return new(results, searched, skipped, true);
                        }
                    }
                    catch (RegexMatchTimeoutException) { throw new IOException("正则搜索耗时过长，请简化表达式后重试。"); }
                }
                else
                {
                    var start = 0;
                    while (start <= content.Length - text.Length)
                    {
                        var at = content.IndexOf(text, start, query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                        if (at < 0) break;
                        if (Add(at, text.Length)) return new(results, searched, skipped, true);
                        start = at + text.Length;
                    }
                }
                bool Add(int column, int length)
                {
                    if (results.Count == MaxSearchResults) return true;
                    var from = Math.Max(0, column - 65); var count = Math.Min(200, content.Length - from);
                    var preview = (from > 0 ? "…" : "") + content.Substring(from, count) + (from + count < content.Length ? "…" : "");
                    results.Add(new(path, index + 1, column + 1, length, preview, fingerprint, column - from + (from > 0 ? 1 : 0))); return false;
                }
            }
        }
        return new(results, searched, skipped, false);
    }
}
