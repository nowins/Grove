namespace GitX.Core;

// A quick action names its exact destination in the UI. Ambiguous multi-remote
// setups keep the explicit selector rather than silently choosing a remote.
public sealed record RemoteQuickTarget(RemoteEntry Remote, string Branch)
{
    public static RemoteQuickTarget? Resolve(ActionContext context, IReadOnlyList<RemoteEntry> remotes)
    {
        var current = context.Branches.FirstOrDefault(branch => branch.Current && !branch.Remote);
        if (current is null) return null;
        if (current.Upstream is { } upstream)
        {
            var matches = remotes.Where(item => upstream.StartsWith(item.Name + "/", StringComparison.Ordinal)
                && upstream.Length > item.Name.Length + 1).ToArray();
            // A friendly tracking name cannot disambiguate remote "team" +
            // branch "fork/main" from remote "team/fork" + branch "main".
            if (matches.Length == 1)
                return new(matches[0], upstream[(matches[0].Name.Length + 1)..]);
            return null;
        }
        return remotes.Count == 1 ? new(remotes[0], current.Name) : null;
    }
}
