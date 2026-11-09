using LibGit2Sharp;

namespace GitX.Core;

// Opening a branch picker only needs refs. Never scan status, hash worktree
// contents, enumerate stashes or start Git processes on this path.
public static class BranchCatalog
{
    public static Task<IReadOnlyList<BranchEntry>> ReadAsync(string root, CancellationToken cancellation = default) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        using var repo = new Repository(root);
        return Read(repo, cancellation);
    }, cancellation);

    internal static IReadOnlyList<BranchEntry> Read(Repository repo, CancellationToken cancellation = default)
    {
        var remoteNames = repo.Network.Remotes.Select(r => r.Name).OrderByDescending(name => name.Length).ToArray();
        var branches = new List<BranchEntry>();
        foreach (var branch in repo.Branches)
        {
            cancellation.ThrowIfCancellationRequested();
            if (branch.Tip is not { } tip) continue;
            branches.Add(new(branch.FriendlyName, branch.CanonicalName, tip.Sha, branch.IsRemote, branch.IsCurrentRepositoryHead,
                branch.TrackedBranch?.FriendlyName, branch.IsRemote ? remoteNames.FirstOrDefault(name => branch.CanonicalName.StartsWith("refs/remotes/" + name + "/", StringComparison.Ordinal)) : null,
                branch.TrackedBranch?.CanonicalName));
        }
        return branches.OrderBy(b => b.Remote).ThenBy(b => b.Name).ToArray();
    }
}
