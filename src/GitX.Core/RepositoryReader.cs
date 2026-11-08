using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

public sealed partial class RepositoryReader : IRepositoryReader, IDisposable
{
    private readonly object _historyGate = new();
    private HistoryCursor? _history;
    private volatile bool _disposed;
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxFiles = 20000;
    public static Task<string> ResolveWorkingDirectoryAsync(string path) => ReadWork.Cpu.Run(() =>
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new IOException("仓库目录不存在，请检查路径或重新选择文件夹。");
        var discovered = Repository.Discover(fullPath);
        if (discovered is null) throw new IOException("此目录不在 Git 仓库中，请选择含 .git 的工作目录。");
        using var repo = new Repository(discovered);
        if (repo.Info.IsBare) throw new IOException("暂不支持裸仓库，请选择有工作区的仓库。");
        return Path.TrimEndingDirectorySeparator(repo.Info.WorkingDirectory);
    }, CancellationToken.None);
    private const FileStatus IndexFlags = FileStatus.NewInIndex | FileStatus.ModifiedInIndex |
        FileStatus.DeletedFromIndex | FileStatus.RenamedInIndex | FileStatus.TypeChangeInIndex;
    private const FileStatus WorkFlags = FileStatus.NewInWorkdir | FileStatus.ModifiedInWorkdir |
        FileStatus.DeletedFromWorkdir | FileStatus.RenamedInWorkdir | FileStatus.TypeChangeInWorkdir;

    public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => OpenAsync(path, token, ReadPriority.Foreground);

    private Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token, ReadPriority priority) => ReadWork.Cpu.Run(() =>
    {
        var discovered = Repository.Discover(Path.GetFullPath(path));
        if (discovered is null) throw new IOException("此目录不在 Git 仓库中，请选择含 .git 的工作目录。");
        using var repo = new Repository(discovered);
        if (repo.Info.IsBare) throw new IOException("首版只支持有工作区的仓库，暂不支持裸仓库。");
        var root = repo.Info.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var indexToken = RepositoryState.Token(root, [], token);
        token.ThrowIfCancellationRequested();
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true, RecurseUntrackedDirs = true, IncludeIgnored = false,
            DetectRenamesInIndex = true, DetectRenamesInWorkDir = false
        });
        var changes = new List<ChangeEntry>();
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in repo.Index)
        {
            token.ThrowIfCancellationRequested();
            paths.Add(entry.Path);
        }
        foreach (var entry in status)
        {
            token.ThrowIfCancellationRequested();
            if (entry.State.HasFlag(FileStatus.Conflicted))
                changes.Add(new(entry.FilePath, ComparisonKind.Conflict, "冲突"));
            else
            {
                if ((entry.State & IndexFlags) != 0)
                    changes.Add(new(entry.FilePath, ComparisonKind.Index, StatusLabel(entry.State, true), entry.HeadToIndexRenameDetails?.OldFilePath));
                if ((entry.State & WorkFlags) != 0)
                    changes.Add(new(entry.FilePath, ComparisonKind.Worktree, StatusLabel(entry.State, false), entry.IndexToWorkDirRenameDetails?.OldFilePath));
            }
            if (!entry.State.HasFlag(FileStatus.DeletedFromWorkdir)) paths.Add(entry.FilePath);
            else paths.Remove(entry.FilePath);
        }
        token.ThrowIfCancellationRequested();
        if (indexToken != RepositoryState.Token(root, [], token)) throw new IOException("读取期间索引或 HEAD 发生变化，请刷新后重试。");
        var references = HistoryReferences(repo);
        return new RepositorySnapshot(root,
            repo.Info.IsHeadUnborn ? $"{repo.Head.FriendlyName}（尚无提交）" : repo.Info.IsHeadDetached ? $"分离 HEAD · {repo.Head.Tip!.Sha[..8]}" : repo.Head.FriendlyName,
            repo.Head.Tip?.Sha, paths.Take(MaxFiles).Select(p => new FileEntry(p)).ToArray(),
            changes.OrderBy(c => c.Kind).ThenBy(c => c.Path, StringComparer.Ordinal).ToArray(), paths.Count > MaxFiles, indexToken, RepositoryOperation.Read(repo),
            string.Join(' ', references.Select(r => r.Sha).Distinct().Order()), string.Join('\n', references.Select(r => $"{r.CanonicalName}={r.Sha}").Order()),
            references.Where(reference => reference.Kind != "head").ToArray());
    }, token, priority);

    public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default)
        => QueryHistoryAsync(root, head, new(), skip, token);

    public Task<HistoryPage> QueryHistoryAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        => ReadHistoryAsync(root, head, query, skip, true, token);

    public Task<HistoryPage> QueryHistorySummariesAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        => ReadHistoryAsync(root, head, query, skip, false, token);

    private Task<HistoryPage> ReadHistoryAsync(string root, string? head, HistoryQuery query, int skip, bool includeMessages, CancellationToken token) => ReadWork.Cpu.Run(() =>
    {
        query = query.Normalize();
        query.Validate();
        if (query.From is { } from && query.Until is { } until && from >= until) throw new IOException("结束日期应晚于开始日期。");
        if (skip < 0 || skip > int.MaxValue - 101) throw new ArgumentOutOfRangeException(nameof(skip));
        lock (_historyGate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrEmpty(head)) { _history?.Dispose(); _history = null; return new HistoryPage([], false); }
            // Open fresh metadata for ref names: adding a label without moving
            // a reachable tip must invalidate the cursor as well.
            using var metadata = new Repository(root);
            var references = HistoryReferences(metadata);
            var key = metadata.Info.Path + "\n" + head + "\n" + includeMessages + "\n" + System.Text.Json.JsonSerializer.Serialize(query) + "\n" + string.Join('\n', references.Select(r => $"{r.CanonicalName}={r.Sha}").Order());
            if (_history?.Key != key)
            {
                _history?.Dispose(); _history = null;
                _history = new(root, head, key, references.GroupBy(r => r.Sha).ToDictionary(g => g.Key, g => g.ToArray()), query, includeMessages);
            }
            try { return _history.Read(skip, token); }
            catch (IOException) { _history.Dispose(); _history = null; throw; }
        }
    }, token, ReadPriority.Background);

    private sealed class HistoryCursor : IDisposable
    {
        public string Key { get; }
        private readonly Repository _repository;
        private readonly IEnumerator<Commit> _commits;
        private readonly Dictionary<string, HistoryReference[]> _labels;
        private readonly HistoryGraph _graph = new();
        private readonly List<CommitEntry> _rows = [];
        private readonly HistoryQuery _query;
        private readonly bool _includeMessages;
        private readonly bool _validPath;
        private readonly System.Text.RegularExpressions.Regex? _textPattern;
        private bool _ended;
        public HistoryCursor(string root, string heads, string key, Dictionary<string, HistoryReference[]> labels, HistoryQuery query, bool includeMessages)
        {
            _includeMessages = includeMessages;
            Key = key; _labels = labels; _query = query; _textPattern = query.CreateTextPattern(); _repository = new Repository(root);
            _validPath = query.Path.Length == 0 || !query.Path.Contains('\0') && !query.Path.Split('/').Any(part => part is "" or "." or "..");
            try { _commits = _repository.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = heads.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }).GetEnumerator(); }
            catch { _repository.Dispose(); throw; }
        }
        public HistoryPage Read(int skip, CancellationToken token)
        {
            while (!_ended && _rows.Count < skip + 101)
            {
                token.ThrowIfCancellationRequested();
                if (!_commits.MoveNext()) { _ended = true; break; }
                // Once the cursor advances, finish exactly one row even if the
                // request is cancelled, so a later page cannot lose that commit.
                var commit = _commits.Current;
                if (!Matches(commit)) continue;
                var parents = commit.Parents.Select(p => p.Sha).ToArray();
                var labels = _labels.GetValueOrDefault(commit.Sha, []);
                _rows.Add(new(commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.When, parents,
                    string.Join(", ", labels.Select(r => r.Name)), _query.IsEmpty ? _graph.Append(commit.Sha, parents) : new(0, 0, 2, parents.Length > 1, []),
                    _includeMessages ? commit.Message : "", labels) { HasFullMessage = _includeMessages });
            }
            token.ThrowIfCancellationRequested();
            return new(_rows.Skip(skip).Take(100).ToArray(), _rows.Count > skip + 100);
        }
        private bool Matches(Commit commit)
        {
            if (_query.Text.Length > 0 && !commit.Sha.StartsWith(_query.Text, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (_textPattern is not null ? !_textPattern.IsMatch(commit.Message) :
                        !commit.Message.Contains(_query.Text, _query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { throw new IOException("历史搜索正则执行超时，请简化表达式后重试。"); }
            }
            if (_query.Author.Length > 0 && !commit.Author.Name.Contains(_query.Author, StringComparison.OrdinalIgnoreCase) && !commit.Author.Email.Contains(_query.Author, StringComparison.OrdinalIgnoreCase)) return false;
            if (_query.From is { } from && commit.Author.When < from || _query.Until is { } until && commit.Author.When >= until) return false;
            if (_query.Path.Length == 0) return true;
            return PathChanged(commit);
        }
        private bool PathChanged(Commit commit)
        {
            // A literal path/subtree has changed iff its object or mode changed.
            // This avoids a full-tree diff for every unrelated commit, without
            // Git log's merge simplification or rename-following semantics.
            if (!_validPath) return false;
            var before = commit.Parents.FirstOrDefault()?.Tree;
            var previous = before?[_query.Path]; var current = commit.Tree[_query.Path];
            if (previous?.Target.Id == current?.Target.Id && previous?.Mode == current?.Mode) return false;
            if ((previous is null || previous.TargetType == TreeEntryTargetType.Tree) &&
                (current is null || current.TargetType == TreeEntryTargetType.Tree))
            {
                // Plumbing can create empty trees: their addition/removal is
                // not a changed file. Diff only the changed subtree in this case.
                using var changes = _repository.Diff.Compare<TreeChanges>(previous?.Target as Tree, current?.Target as Tree,
                    new CompareOptions { Similarity = SimilarityOptions.None });
                return changes.Any();
            }
            return true;
        }
        public void Dispose() { _commits.Dispose(); _repository.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commitMessages.Dispose(); _commitChanges.Dispose();
        // UI disposal must not wait for an in-flight native MoveNext. Reads run
        // off-thread; dispose the cursor under the same gate once it returns.
        _ = Task.Run(() => { lock (_historyGate) { _history?.Dispose(); _history = null; } });
    }

    private static IReadOnlyList<HistoryReference> HistoryReferences(Repository repo)
    {
        var result = new List<HistoryReference>();
        if (repo.Head.Tip is { } head) result.Add(new("HEAD", "HEAD", head.Sha, "head", true));
        foreach (var branch in repo.Branches) if (branch.Tip is { } tip)
            result.Add(new(branch.FriendlyName, branch.CanonicalName, tip.Sha, branch.IsRemote ? "remote" : "local", branch.IsCurrentRepositoryHead));
        foreach (var tag in repo.Tags) if (tag.PeeledTarget is Commit commit)
            result.Add(new(tag.FriendlyName, tag.CanonicalName, commit.Sha, "tag"));
        return result;
    }

    public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => ReadWork.Cpu.Run<IReadOnlyList<ChangeEntry>>(() =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var repo = new Repository(root);
        var commit = repo.Lookup<Commit>(sha) ?? throw new IOException("提交已不存在，请刷新仓库。");
        return ReadTreeChanges(repo, commit.Parents.FirstOrDefault()?.Tree, commit.Tree, ComparisonKind.Commit, token);
    }, token);

    public async Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default)
    {
        if (request.Kind is not (ComparisonKind.Worktree or ComparisonKind.Index or ComparisonKind.LocalChanges or ComparisonKind.CommitWorktree or ComparisonKind.CommitParentWorktree)) return await ReadDocumentAsync(request, token);
        var paths = request.OldPath is { } old && old != request.Path ? new[] { request.Path, old } : [request.Path];
        var state = await ReadWork.Cpu.Run(() => RepositoryState.Token(request.Root, paths, token), token);
        var document = await ReadDocumentAsync(request, token);
        var after = await ReadWork.Cpu.Run(() => RepositoryState.Token(request.Root, paths, token), token);
        if (state != after) throw new IOException("读取期间仓库发生变化，请刷新后重试。");
        return document with { WriteToken = state, CanStage = request.Kind == ComparisonKind.Worktree && document.Diff is not null,
            CanInclude = request.Kind == ComparisonKind.LocalChanges };
    }

    private async Task<RepositoryDocument> ReadDocumentAsync(DocumentRequest request, CancellationToken token)
    {
        (RepositoryDocument? Preview, DiffSide? Old, DiffSide? New, CommentContext? Context) snapshot;
        try { snapshot = await ReadWork.Cpu.Run(() => Capture(request, token), token).ConfigureAwait(false); }
        catch (IOException e) when (e is PreviewLimitException or WorkingTextLimitException) { return Notice(request, e.Message); }
        if (snapshot.Preview is not null) return snapshot.Preview;
        var oldSide = snapshot.Old!;
        var newSide = snapshot.New!;
        var compared = await NativeDiff.CompareCapturedAsync(oldSide, newSide, snapshot.Context!.Snapshot, token, request.Whitespace).ConfigureAwait(false);
        var diff = compared.Diff;
        token.ThrowIfCancellationRequested();
        var comparison = $"{oldSide.Label} → {newSide.Label} · 并排 diff";
        var detail = $"{diff.Blocks.Count} 个变更块 · F7 下一处 / Shift+F7 上一处 · 文件内循环";
        if (diff.CoarseInlineHighlights) detail += " · 部分长行使用范围高亮";
        return new(request.Path, compared.HasPatch ? "" : "没有文本差异。路径或文件属性变化见两侧标题。", Language(request.Path), comparison, detail, IsDiff: true, Diff: diff, CommentContext: snapshot.Context)
            { DeferredText = compared.HasPatch ? compared.Patch : null };
    }

    private static (RepositoryDocument? Preview, DiffSide? Old, DiffSide? New, CommentContext? Context) Capture(DocumentRequest request, CancellationToken token)
    {
        using var repo = new Repository(request.Root);
        token.ThrowIfCancellationRequested();
        var fullPath = SafeWorktreePath(repo.Info.WorkingDirectory, request.Path);
        var revision = request.Kind == ComparisonKind.Commit ? repo.Lookup<Commit>(request.Commit)?.Sha ?? "" :
            request.Kind is ComparisonKind.RevisionRange or ComparisonKind.CommitWorktree or ComparisonKind.CommitParentWorktree ? (request.BaseCommit ?? "") + ":" + (repo.Lookup<Commit>(request.Commit)?.Sha ?? "") : repo.Head.Tip?.Sha ?? "unborn";
        if (request.Kind is ComparisonKind.WorkingFile or ComparisonKind.Conflict)
        {
            try
            {
                var editable = request.Kind == ComparisonKind.WorkingFile ? WorkingTextFiles.Capture(repo, request.Path, token) : null;
                var preview = editable is null ? TextDocument(request, ReadWorktree(fullPath), "冲突 · 工作区（只读）")
                    : editable.Problem is not null || editable.Text is null ? Notice(request, editable.Problem ?? "文件不是有效 UTF-8，未创建编辑基线。")
                    : new RepositoryDocument(request.Path, editable.Text!, Language(request.Path), "工作区 · 文本",
                        $"UTF-8 · {editable.Info.Newline} · {editable.Bytes.Length:N0} 字节");
                if (!preview.IsNotice && request.Kind == ComparisonKind.WorkingFile)
                    preview = preview with { CommentContext = CommentContext.Create(repo, request, revision, null, new(request.Path, "工作区", preview.Text, true)), Editing = editable!.Info };
                return (preview, null, null, preview.CommentContext);
            }
            catch (IOException e) when (request.Kind == ComparisonKind.WorkingFile && e is FileNotFoundException or DirectoryNotFoundException)
            { return (Notice(request, "文件已不存在。已打开的未保存草稿仍保留，不会重新创建或覆盖其他文件。"), null, null, null); }
            catch (IOException e) when (request.Kind == ComparisonKind.Conflict && e is FileNotFoundException or DirectoryNotFoundException)
            { return (Notice(request, "此冲突路径在工作区不存在。请打开合并窗口，核对删除或重命名状态。"), null, null, null); }
        }

        DiffSide ReadBlob(Blob? blob, string path, string label, bool checkout = false)
        {
            token.ThrowIfCancellationRequested();
            if (blob is null) return new(path, label, "", false);
            if (blob.Size > MaxTextBytes) throw new PreviewLimitException("文件超过 1 MiB，暂停 diff 加载。");
            if (blob.IsBinary) throw new PreviewLimitException("二进制文件，不显示文本 diff。");
            using var stream = checkout ? blob.GetContentStream(new FilteringOptions(path)) : blob.GetContentStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = stream.Read(chunk)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (buffer.Length + count > MaxTextBytes) throw new PreviewLimitException("检出格式的文件超过 1 MiB，暂停 diff 加载。");
                buffer.Write(chunk, 0, count);
            }
            var (text, problem) = DecodeText(buffer.ToArray());
            if (problem is not null) throw new PreviewLimitException(problem);
            return new(path, label, text!, true);
        }

        if (request.Kind == ComparisonKind.RevisionFile)
        {
            var commit = repo.Lookup<Commit>(request.Commit) ?? throw new IOException("注解修订已不存在。");
            var side = ReadBlob(commit.Tree[request.Path]?.Target as Blob, request.Path, commit.Sha[..8]);
            if (!side.Exists) throw new IOException("此修订中不存在该文件。");
            return (new(request.Path, side.Text, Language(request.Path), $"修订 {commit.Sha[..8]} · 只读",
                $"历史文件 · {commit.Sha}", SourceRevision: commit.Sha), null, null, null);
        }

        var oldPath = request.OldPath ?? request.Path;
        DiffSide oldSide;
        DiffSide newSide;
        if (request.Kind is ComparisonKind.Commit or ComparisonKind.RevisionRange)
        {
            var commit = repo.Lookup<Commit>(request.Commit) ?? throw new IOException("提交不存在。");
            var parent = request.Kind == ComparisonKind.RevisionRange ? repo.Lookup<Commit>(request.BaseCommit) ?? throw new IOException("比较起点已不存在。") : commit.Parents.FirstOrDefault();
            oldSide = ReadBlob(parent?.Tree[oldPath]?.Target as Blob, oldPath, parent?.Sha[..8] ?? "空树");
            newSide = ReadBlob(commit.Tree[request.Path]?.Target as Blob, request.Path, commit.Sha[..8]);
        }
        else if (request.Kind == ComparisonKind.Index)
        {
            var head = repo.Head.Tip;
            var index = repo.Index[request.Path];
            oldSide = ReadBlob(head?.Tree[oldPath]?.Target as Blob, oldPath, head is null ? "HEAD（空树）" : $"HEAD {head.Sha[..8]}");
            newSide = ReadBlob(index is null ? null : repo.Lookup<Blob>(index.Id), request.Path, "索引");
        }
        else
        {
            var index = repo.Index[oldPath];
            // Checkout filtering handles Git's CRLF/LF policy; it does not create a working file.
            var baseline = request.Kind is ComparisonKind.CommitWorktree or ComparisonKind.CommitParentWorktree ? repo.Lookup<Commit>(request.Commit) ?? throw new IOException("比较提交已不存在。") : repo.Head.Tip;
            if (request.Kind == ComparisonKind.CommitParentWorktree) baseline = baseline!.Parents.FirstOrDefault();
            oldSide = request.Kind is ComparisonKind.LocalChanges or ComparisonKind.CommitWorktree or ComparisonKind.CommitParentWorktree
                ? ReadBlob(baseline?.Tree[oldPath]?.Target as Blob, oldPath, baseline?.Sha[..8] ?? "空树")
                : ReadBlob(index is null ? null : repo.Lookup<Blob>(index.Id), oldPath, "索引（检出格式）", checkout: true);
            if (File.Exists(fullPath))
            {
                var (text, problem) = DecodeText(ReadWorktree(fullPath));
                if (problem is not null) throw new PreviewLimitException(problem);
                newSide = new(request.Path, index is null ? "工作区（未跟踪）" : "工作区", text!, true);
            }
            else newSide = new(request.Path, "工作区", "", false);
        }
        return (null, oldSide, newSide, CommentContext.Create(repo, request, revision, oldSide, newSide));
    }

    private sealed class PreviewLimitException(string message) : IOException(message);
    private static byte[] ReadWorktree(string path)
    {
        // Do not follow symlinks/junctions out of the selected repository.
        for (var cursor = new FileInfo(path) as FileSystemInfo; cursor is not null; cursor = Directory.GetParent(cursor.FullName))
            if (cursor.Exists && cursor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("暂不读取符号链接或目录联接，请在外部编辑器中查看。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxTextBytes) throw new PreviewLimitException("文件超过 1 MiB，暂停加载全文，可执行整文件操作。");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxTextBytes) throw new PreviewLimitException("文件超过 1 MiB，暂停加载全文，可执行整文件操作。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static RepositoryDocument TextDocument(DocumentRequest request, byte[] bytes, string comparison)
    {
        var (text, problem) = DecodeText(bytes);
        if (problem is not null) return Notice(request, problem);
        return new(request.Path, text!, Language(request.Path), comparison,
            $"UTF-8 · {(text!.Contains("\r\n") ? "CRLF" : "LF")} · {bytes.Length:N0} 字节");
    }

    private static (string? Text, string? Problem) DecodeText(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return (null, "二进制或 UTF-16 文件，首版仅支持 UTF-8 文本。");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            if (text.Split('\n').Any(line => line.Length > 20000))
                return (null, "单行超过 20,000 字符，首版暂停编辑器加载。");
            return (text, null);
        }
        catch (DecoderFallbackException) { return (null, "文件不是有效 UTF-8，首版未提供编码转换。"); }
    }

    private static RepositoryDocument Notice(DocumentRequest request, string text) =>
        new(request.Path, text, "plaintext", "内容预览受限", text, IsNotice: true);

    public static string SafeWorktreePath(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!full.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("文件路径超出仓库范围。");
        return full;
    }

    private static string StatusLabel(FileStatus status, bool index)
    {
        if ((status & (index ? FileStatus.NewInIndex : FileStatus.NewInWorkdir)) != 0) return index ? "新增" : "未跟踪";
        if ((status & (index ? FileStatus.DeletedFromIndex : FileStatus.DeletedFromWorkdir)) != 0) return "删除";
        if ((status & (index ? FileStatus.RenamedInIndex : FileStatus.RenamedInWorkdir)) != 0) return "重命名";
        return "修改";
    }

    private static string Language(string path) => SourceLanguage.Identify(path);
}
