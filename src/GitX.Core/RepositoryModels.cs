namespace GitX.Core;

public enum ComparisonKind { WorkingFile, Worktree, Index, Commit, Conflict, LocalChanges, RevisionRange, CommitWorktree, RevisionFile, CommitParentWorktree }
public enum DiffWhitespace { Exact, Trim, Ignore }

public sealed record FileEntry(string Path, string Status = "");

public sealed record ChangeEntry(string Path, ComparisonKind Kind, string Status, string? OldPath = null)
{
    public string Label => $"{Status}  {Path}";
    public string Comparison => Kind switch
    {
        ComparisonKind.Index => "HEAD → 索引",
        ComparisonKind.Worktree => "索引 → 工作区",
        ComparisonKind.Conflict => "冲突 · 工作区文本（尚未提供合并操作）",
        _ => "第一父提交 → 所选提交"
    };
}

public sealed record CommitEntry(string Sha, string Subject, string Author, DateTimeOffset When,
    IReadOnlyList<string>? Parents = null, string References = "", HistoryGraphRow? Graph = null, string Message = "",
    IReadOnlyList<HistoryReference>? ReferenceLabels = null)
{
    public bool HasFullMessage { get; init; } = true;
    public string ShortSha => Sha[..8];
    public string Detail => $"{ShortSha}  {Author}  {When.LocalDateTime:yyyy-MM-dd HH:mm}";
    public string Caption => string.IsNullOrEmpty(References) ? Subject : $"[{References}]  {Subject}";
    public string FullMessage => string.IsNullOrEmpty(Message) ? Subject : Message.TrimEnd();
    public string DateLabel => When.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Tooltip => $"{Sha}\n{Subject}\n{References}\n父提交：{string.Join(" · ", Parents ?? [])}";
}

public sealed record HistoryReference(string Name, string CanonicalName, string Sha, string Kind, bool Current = false);

public sealed record RepositorySnapshot(string Root, string Branch, string? Head,
    IReadOnlyList<FileEntry> Files, IReadOnlyList<ChangeEntry> Changes, bool FilesTruncated, string? IndexToken = null, RepositoryOperation? Operation = null,
    string? HistoryHeads = null, string? HistoryToken = null, IReadOnlyList<HistoryReference>? References = null);

public sealed record HistoryPage(IReadOnlyList<CommitEntry> Commits, bool HasMore);
public sealed record CommitDetails(string Message, IReadOnlyList<ChangeEntry> Changes);
public sealed record CommitComparisonDetails(string OldMessage, string? NewMessage, IReadOnlyList<ChangeEntry> Changes);

public sealed record HistoryQuery(string Text = "", string Author = "", DateTimeOffset? From = null, DateTimeOffset? Until = null, string Path = "", bool MatchCase = false, bool UseRegex = false)
{
    public bool IsEmpty => Text.Length == 0 && Author.Length == 0 && From is null && Until is null && Path.Length == 0;
    public HistoryQuery Normalize() => this with { Text = Text.Trim(), Author = Author.Trim(), Path = Path.Trim().Replace('\\', '/').TrimEnd('/') };
    public void Validate() => CreateTextPattern();
    internal System.Text.RegularExpressions.Regex? CreateTextPattern()
    {
        if (!UseRegex || Text.Length == 0) return null;
        if (Text.Length > 2048) throw new IOException("历史搜索正则不能超过 2048 个字符。");
        try
        {
            return new(Text, System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                (MatchCase ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException) { throw new IOException("历史搜索正则无效，请检查表达式。"); }
    }
}

public sealed record DocumentRequest(string Root, string Path, ComparisonKind Kind,
    string? Commit = null, string? OldPath = null, DiffWhitespace Whitespace = DiffWhitespace.Exact, string? BaseCommit = null);

public sealed record RepositoryDocument([property: System.Text.Json.Serialization.JsonPropertyOrder(-2)] string Path, string Text, string Language, string Comparison,
    string Detail, bool IsDiff = false, SideBySideDiff? Diff = null, bool PreserveView = false, string? WriteToken = null, bool CanStage = false, IReadOnlyList<BlameLine>? Blame = null, bool IsNotice = false, CommentContext? CommentContext = null, bool CanInclude = false,
    string? SourceRevision = null, int? RevealLine = null, int? RevealColumn = null, int? RevealLength = null, WorkingTextEditInfo? Editing = null)
{
    private readonly string _text = Text;
    [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
    public string Text { get => DeferredText?.Value ?? _text; init { _text = value; DeferredText = null; } }
    // Diff text is a compatibility/fallback view. Structured consumers should
    // not decode it; an explicit Text replacement must discard the old view.
    internal Lazy<string>? DeferredText { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? RevealSide { get; init; }
}

public sealed record DiffSide(string Path, string Label, string Text, bool Exists)
{
    private (int CrLf, int Lf) LineBreakCounts()
    {
        var crlf = 0; var lf = 0;
        for (var i = 0; i < Text.Length; i++) if (Text[i] == '\n')
        { if (i > 0 && Text[i - 1] == '\r') crlf++; else lf++; }
        return (crlf, lf);
    }
    public string LineEnding => LineBreakCounts() switch { (> 0, > 0) => "混合 LF/CRLF", (> 0, _) => "CRLF", _ => "LF" };
    [System.Text.Json.Serialization.JsonIgnore]
    public string PreferredNewline { get { var (crlf, lf) = LineBreakCounts(); return crlf > lf ? "\r\n" : "\n"; } }
    public bool EndsWithNewline => Text.EndsWith('\n');
}

// Line offsets are zero-based; inline columns are one-based UTF-16, as required by Monaco.
public sealed record InlineSpan(int StartColumn, int EndColumn);
public sealed record InlineChange(int OldLine, int NewLine, IReadOnlyList<InlineSpan> OldSpans, IReadOnlyList<InlineSpan> NewSpans,
    IReadOnlyList<InlineSpan>? OldWordSpans = null, IReadOnlyList<InlineSpan>? NewWordSpans = null);
public sealed record DiffRow(int? OldLine, int? NewLine);
public sealed record DiffBlock(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<InlineChange> InlineChanges, IReadOnlyList<DiffRow> Rows);
public sealed record SideBySideDiff(DiffSide Old, DiffSide New, IReadOnlyList<DiffBlock> Blocks, bool CoarseInlineHighlights);
