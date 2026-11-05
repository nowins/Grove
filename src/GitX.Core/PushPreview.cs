using LibGit2Sharp;

namespace GitX.Core;

public enum PushRelation { NewBranch, Ahead, Synchronized, Behind, Diverged, Unknown }
public sealed record PushCommit(string Sha, string Subject)
{
    public override string ToString() => $"{Sha[..8]}  {Subject}";
}
public sealed record PushFile(string Status, string Path)
{
    public override string ToString() => $"{Status}  {Path}";
}
public sealed record PushPreview(ActionContext Context, RemoteEntry Remote, string Branch, string RemoteHead, string PushUrl,
    DateTimeOffset ReadAt, PushRelation Relation, int? CommitCount)
{
    public bool CanPush => Relation is PushRelation.NewBranch or PushRelation.Ahead;
}

// Probes the actual push endpoint, never mistakes a stale fetch ref for remote truth.
// Reads do not fetch objects or alter refs, the index, config or the working tree.
public sealed class PushPreviewReader
{
    public async Task<PushPreview> ReadAsync(ActionContext context, RemoteEntry remote, string branch,
        GitAuthentication? authentication = null, CancellationToken token = default)
    {
        if (context.Head is null) throw new IOException("没有可推送的提交。");
        var writer = new RemoteWriter();
        var endpoint = await writer.ResolvePushUrlAsync(context, remote, token);
        var sha = await writer.RemoteHeadAsync(context, remote, branch, authentication, token, expectedEndpoint: endpoint);
        return await Task.Run(() =>
        {
            RepositoryActions.Check(context);
            using var repo = new Repository(context.Root);
            var head = repo.Lookup<Commit>(context.Head ?? "") ?? throw new IOException("没有可推送的提交。");
            var basis = sha.Length == 0 ? null : repo.Lookup<Commit>(sha);
            var relation = sha.Length == 0 ? PushRelation.NewBranch : sha == head.Sha ? PushRelation.Synchronized
                : basis is null ? PushRelation.Unknown
                : repo.ObjectDatabase.FindMergeBase(head, basis)?.Sha == basis.Sha ? PushRelation.Ahead
                : repo.ObjectDatabase.FindMergeBase(head, basis)?.Sha == head.Sha ? PushRelation.Behind : PushRelation.Diverged;
            var count = 0;
            if (relation != PushRelation.Unknown)
                foreach (var commit in repo.Commits.QueryBy(Filter(head.Sha, sha))) { token.ThrowIfCancellationRequested(); count++; }
            return new PushPreview(context, remote, branch, sha, endpoint, DateTimeOffset.UtcNow, relation, relation == PushRelation.Unknown ? null : count);
        }, token);
    }

    public Task<IReadOnlyList<PushCommit>> CommitsAsync(PushPreview preview, int offset = 0, CancellationToken token = default)
        => Task.Run<IReadOnlyList<PushCommit>>(() =>
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (preview.Relation == PushRelation.Unknown) throw new IOException("远程提交尚未获取，无法确定待推提交。请先 Fetch 后重新审阅。");
            using var repo = new Repository(preview.Context.Root);
            var result = new List<PushCommit>(); var skipped = 0;
            foreach (var commit in repo.Commits.QueryBy(Filter(preview.Context.Head!, preview.RemoteHead)))
            {
                token.ThrowIfCancellationRequested();
                if (skipped++ < offset) continue;
                result.Add(new(commit.Sha, commit.MessageShort));
                if (result.Count == 100) break;
            }
            return result;
        }, token);

    public async Task<IReadOnlyList<PushFile>> FilesAsync(PushPreview preview, PushCommit selected, CancellationToken token = default)
    {
        var parent = await Task.Run(() =>
        {
            using var repo = new Repository(preview.Context.Root);
            var commit = repo.Lookup<Commit>(selected.Sha) ?? throw new IOException("提交已不可用，请刷新审阅。");
            return commit.Parents.FirstOrDefault()?.Sha;
        }, token);
        var args = new List<string> { "diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-z", "--no-renames", "--no-ext-diff", "--no-textconv" };
        if (parent is not null) args.Add(parent);
        args.Add(selected.Sha); args.Add("--");
        // Read-only child process: bounded output, timeout and cancellation.
        // No native full-tree diff is retained before applying the display cap.
        var output = await GitReadProcess.RunAsync(preview.Context.Root, args, token);
        var parts = output.Split('\0'); var files = new List<PushFile>();
        for (var i = 0; i + 1 < parts.Length && files.Count < 1000; i += 2)
        {
            token.ThrowIfCancellationRequested();
            files.Add(new(parts[i] is "A" or "D" ? parts[i] : "M", parts[i + 1]));
        }
        return files;
    }

    public Task<string> ExecuteAsync(PushPreview preview, bool force, GitAuthentication? authentication = null)
    {
        if (!force && !preview.CanPush) throw new IOException("此预览不能普通推送，请刷新并检查远程状态。");
        if (preview.Relation == PushRelation.Unknown) throw new IOException("远程历史未知，请先 Fetch 并重新审阅。");
        return new RemoteWriter().ExecuteAsync(preview.Context, preview.Remote, force ? RemoteAction.ForceWithLease : RemoteAction.Push,
            preview.Branch, force ? preview.RemoteHead : null, authentication, reviewedRemote: preview.RemoteHead, reviewedPushUrl: preview.PushUrl);
    }

    private static CommitFilter Filter(string head, string remote) => new()
    { IncludeReachableFrom = head, ExcludeReachableFrom = remote.Length == 0 ? null : remote, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
}
