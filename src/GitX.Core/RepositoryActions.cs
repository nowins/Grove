using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

public sealed record BranchEntry(string Name, string CanonicalName, string Sha, bool Remote, bool Current, string? Upstream, string? RemoteName = null, string? UpstreamCanonicalName = null)
{
    public override string ToString() => $"{(Current ? "● " : "  ")}{Name}{(Current ? " · 当前" : "")}";
}
public sealed record StashEntry(int Index, string Sha, string Message)
{
    public override string ToString() => $"stash@{{{Index}}} · {Message}";
}
public sealed record ActionContext(string Root, string Token, string? Head, string Branch, string Message,
    bool Dirty, bool InOperation, IReadOnlyList<BranchEntry> Branches, IReadOnlyList<StashEntry> Stashes, bool Detached = false);

public static class RepositoryActions
{
    public static Task<ActionContext> ReadAsync(string root, CancellationToken cancellation = default) => Task.Run(() => Read(root, cancellation), cancellation);
    private static ActionContext Read(string root, CancellationToken cancellation = default)
    {
        using var repo = new Repository(root);
        var changes = repo.RetrieveStatus().Where(s => s.State != FileStatus.Unaltered && s.State != FileStatus.Ignored).ToArray();
        var token = Token(repo, changes.Select(s => s.FilePath), cancellation);
        var remoteNames = repo.Network.Remotes.Select(r => r.Name).OrderByDescending(name => name.Length).ToArray();
        var branches = repo.Branches.Where(b => b.Tip is not null).Select(b => new BranchEntry(b.FriendlyName, b.CanonicalName, b.Tip.Sha, b.IsRemote, b.IsCurrentRepositoryHead, b.TrackedBranch?.FriendlyName, b.IsRemote ? remoteNames.FirstOrDefault(name => b.CanonicalName.StartsWith("refs/remotes/" + name + "/", StringComparison.Ordinal)) : null, b.TrackedBranch?.CanonicalName)).OrderBy(b => b.Remote).ThenBy(b => b.Name).ToArray();
        var stashes = repo.Stashes.Select((s, i) => new StashEntry(i, s.WorkTree.Sha, s.Message)).ToArray();
        return new(root, token, repo.Head.Tip?.Sha, repo.Head.FriendlyName, repo.Head.Tip?.Message ?? "", changes.Length > 0,
            RepositoryOperation.Read(repo) is not null || repo.Index.Conflicts.Any(), branches, stashes, repo.Info.IsHeadDetached);
    }
    private static string Token(Repository repo, IEnumerable<string> paths, CancellationToken cancellation)
    {
        var references = string.Join('\n', repo.Refs.Where(r => !r.CanonicalName.StartsWith("refs/gitx/recovery/", StringComparison.Ordinal)).Select(r => r.CanonicalName + "=" + r.ResolveToDirectReference()?.TargetIdentifier).Order());
        var config = string.Join('\n', repo.Config.Select(c => c.Key + "=" + c.Value).Order());
        var stashes = string.Join('\n', repo.Stashes.Select((s, i) => $"{i}={s.WorkTree.Sha}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RepositoryState.Token(repo.Info.WorkingDirectory, paths, cancellation) + references + config + stashes)));
    }
    internal static void Check(ActionContext context, bool clean = false)
    {
        var current = Read(context.Root);
        if (current.Token != context.Token) throw new IOException("仓库、分支或工作区已变化，未执行操作。请重新打开操作菜单。");
        if (current.InOperation) throw new IOException("请先完成或中止当前 Git 操作、处理未合并索引。");
        if (clean && current.Dirty) throw new IOException("存在未提交改动（含未跟踪文件）。请先提交或使用 Stash 保存，再执行此操作。");
    }
    internal static async Task<string> RunWithConflicts(string root, string kind, IEnumerable<string> arguments)
    {
        try { return await GitWriteProcess.RepositoryAsync(root, arguments, noEditor: true); }
        catch (IOException e)
        {
            using var repo = new Repository(root);
            if (repo.Index.Conflicts.Any() && RepositoryOperation.Read(repo)?.Kind == kind)
                return $"已进入{kind}冲突处理，请打开冲突文件。\n{e.Message}";
            throw;
        }
    }
    internal static string CommitSha(string root, string sha)
    {
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new IOException("需要完整提交 SHA。");
        using var repo = new Repository(root);
        return (repo.Lookup<Commit>(sha) ?? throw new IOException("提交不存在，请刷新。")).Sha;
    }
}

public enum BranchAction { Create, Switch, Delete, Merge, Rebase, CreateAndSwitch }
public sealed class BranchWriter
{
    public Task<string> ExecuteAsync(ActionContext context, BranchAction action, BranchEntry? target = null, string name = "") => RepositoryWriter.Locked(context.Root, async () =>
    {
        RepositoryActions.Check(context, action is BranchAction.Switch or BranchAction.Merge or BranchAction.Rebase or BranchAction.CreateAndSwitch);
        if (target is not null && !context.Branches.Contains(target)) throw new IOException("分支不属于当前快照。");
        if (action is not (BranchAction.Create or BranchAction.CreateAndSwitch) && target is null) throw new IOException("请选择分支。");
        switch (action)
        {
            case BranchAction.Create:
            case BranchAction.CreateAndSwitch:
                await BranchCheckout.ValidateNameAsync(context.Root, name);
                var from = target?.Sha ?? context.Head ?? throw new IOException("请先创建初始提交。");
                if (context.Branches.Any(b => !b.Remote && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("本地分支已存在，未覆盖或改绑。");
                var track = target is { Remote: true, RemoteName: not null };
                if (action == BranchAction.CreateAndSwitch)
                    return await GitWriteProcess.RepositoryAsync(context.Root, track ? ["switch", "--track", "-c", name, target!.CanonicalName] : ["switch", "--no-track", "-c", name, from]);
                return await GitWriteProcess.RepositoryAsync(context.Root, track ? ["branch", "--track", "--", name, target!.CanonicalName] : ["branch", "--no-track", "--", name, from]);
            case BranchAction.Switch:
                var checkout = BranchCheckout.Resolve(context, target!);
                if (checkout.Error is { } reason) throw new IOException(reason);
                if (checkout.CreateTracking)
                {
                    await BranchCheckout.ValidateNameAsync(context.Root, checkout.LocalName);
                    return await GitWriteProcess.RepositoryAsync(context.Root, ["switch", "--track", "-c", checkout.LocalName, checkout.Target.CanonicalName]);
                }
                return await GitWriteProcess.RepositoryAsync(context.Root, ["switch", "--", checkout.Target.Name]);
            case BranchAction.Delete:
                if (target!.Remote || target.Current) throw new IOException("只能删除非当前的本地分支；未合并分支由 Git 拒绝删除。");
                return await GitWriteProcess.RepositoryAsync(context.Root, ["branch", "-d", "--", target.Name]);
            case BranchAction.Merge:
                return await RepositoryActions.RunWithConflicts(context.Root, "merge", ["merge", "--no-edit", "--no-autostash", "--", target!.Sha]);
            case BranchAction.Rebase:
                return await RepositoryActions.RunWithConflicts(context.Root, "rebase", ["-c", "rebase.updateRefs=false", "rebase", "--no-autostash", target!.Sha]);
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    });
}
