using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

// Immutable commit reads. None of these commands checks out a revision or writes the index.
public static class CommitLogActions
{
    public static Task<CommitEntry> ReadAsync(string root, string sha, CancellationToken token = default) => ReadWork.Cpu.Run(() =>
    {
        using var repo = new Repository(root);
        return Entry(Lookup(repo, sha));
    }, token);

    public static Task<IReadOnlyList<CommitEntry>> ChildrenAsync(string root, string sha, CancellationToken token = default) => ReadWork.Cpu.Run<IReadOnlyList<CommitEntry>>(() =>
    {
        using var repo = new Repository(root);
        var commit = Lookup(repo, sha);
        // Follow actual graph edges, including merge edges, across reachable branches/tags.
        // Recovery refs and stash commits are not part of the Log's history scope.
        var tips = repo.Branches.Select(b => b.Tip).Concat(repo.Tags.Select(t => t.PeeledTarget as Commit))
            .Append(repo.Head.Tip).Where(c => c is not null).DistinctBy(c => c!.Sha).ToArray();
        var result = new List<CommitEntry>();
        foreach (var child in repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = tips, ExcludeReachableFrom = commit,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }))
        {
            token.ThrowIfCancellationRequested();
            if (!child.Parents.Any(p => p.Sha == commit.Sha)) continue;
            if (result.Count == 1000) throw new IOException("此提交的子提交超过 1,000 个，请使用历史筛选缩小范围。");
            result.Add(Entry(child));
        }
        return result.AsReadOnly();
    }, token);

    public static async Task<byte[]> PatchAsync(string root, string sha, string? parent = null, CancellationToken token = default)
    {
        var commit = await ReadAsync(root, sha, token).ConfigureAwait(false);
        var parents = commit.Parents!;
        if (parent is not null && !parents.Contains(parent, StringComparer.Ordinal)) throw new IOException("补丁起点必须是此提交的父提交。");
        if (parents.Count > 1 && parent is null) throw new IOException("合并提交导出补丁时必须选择父提交。");
        parent ??= parents.FirstOrDefault();
        var args = parent is not null ? new List<string> { "diff", parent, commit.Sha } : ["show", "--format=", "--root", commit.Sha];
        args.AddRange(["--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--no-relative",
            "--src-prefix=a/", "--dst-prefix=b/", "--"]);
        // Preserve non-UTF-8 file bytes. The bounded reader rejects oversized output rather than exporting a partial patch.
        var patch = await GitReadProcess.RunAsync(root, args, token, outputEncoding: Encoding.Latin1).ConfigureAwait(false);
        return Encoding.Latin1.GetBytes(patch);
    }

    private static Commit Lookup(Repository repo, string sha)
    {
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new IOException("需要完整提交 SHA。");
        return repo.Lookup<Commit>(sha) ?? throw new IOException("提交已不存在，请刷新。");
    }

    private static CommitEntry Entry(Commit commit) => new(commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.When,
        commit.Parents.Select(p => p.Sha).ToArray(), Message: commit.Message);
}

public sealed class CommitReferenceWriter
{
    public Task<string> CreateBranchAsync(ActionContext context, string sha, string name, bool checkout = false) => RepositoryWriter.Locked(context.Root, async () =>
    {
        RepositoryActions.Check(context, clean: checkout);
        sha = RepositoryActions.CommitSha(context.Root, sha);
        await BranchCheckout.ValidateNameAsync(context.Root, name);
        if (context.Branches.Any(b => !b.Remote && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("本地分支已存在，未覆盖或改绑。");
        RepositoryActions.Check(context, clean: checkout);
        return await GitWriteProcess.RepositoryAsync(context.Root, checkout
            ? ["checkout", "--no-track", "--no-overwrite-ignore", "--no-recurse-submodules", "-b", name, sha, "--"]
            : ["branch", "--no-track", "--", name, sha]);
    });

    public Task<string> CreateTagAsync(ActionContext context, string sha, string name) => RepositoryWriter.Locked(context.Root, async () =>
    {
        RepositoryActions.Check(context);
        sha = RepositoryActions.CommitSha(context.Root, sha);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-')) throw new IOException("请输入有效的新标签名称。");
        try { await GitReadProcess.RunAsync(context.Root, ["check-ref-format", "refs/tags/" + name], default); }
        catch (IOException error) { throw new IOException("无法使用此标签名称，请检查空格、连续点和特殊字符。", error); }
        using (var repo = new Repository(context.Root))
            if (repo.Tags.Any(t => t.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("标签已存在，未覆盖。");
        RepositoryActions.Check(context);
        // This entry creates a local lightweight tag, irrespective of tag.gpgSign.
        return await GitWriteProcess.RepositoryAsync(context.Root, ["tag", "--no-sign", "--", name, sha]);
    });

    public Task<string> CheckoutAsync(ActionContext context, string sha) => RepositoryWriter.Locked(context.Root, async () =>
    {
        RepositoryActions.Check(context, clean: true);
        sha = RepositoryActions.CommitSha(context.Root, sha);
        return await GitWriteProcess.RepositoryAsync(context.Root, ["checkout", "--detach", "--no-overwrite-ignore", "--no-recurse-submodules", sha, "--"]);
    });
}
