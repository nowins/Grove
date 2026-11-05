using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace GitX.Core;

public sealed record RemoteEntry(string Name, string Url, string PushUrl)
{
    public override string ToString() => $"{Name} · {RemoteWriter.RedactUrl(Url)}";
}
public enum RemoteAction { Fetch, Pull, Push, ForceWithLease }
public sealed record GitAuthentication(IReadOnlyDictionary<string, string> Environment, Func<string, string> Sanitize);

public sealed class RemoteWriter
{
    public Task<IReadOnlyList<RemoteEntry>> ReadAsync(string root) => Task.Run<IReadOnlyList<RemoteEntry>>(() =>
    {
        using var repo = new Repository(root);
        return repo.Network.Remotes.Select(r => new RemoteEntry(r.Name, r.Url, r.PushUrl ?? r.Url)).ToArray();
    });
    public static string RedactUrl(string text)
    {
        // Diagnostics and target labels must not expose URL credentials, query
        // parameters or fragments. Preserve ordinary scp-style user@host paths.
        text = Regex.Replace(text, @"([a-z][a-z0-9+.-]*://)[^/\s@]+@", "$1***@", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"([a-z][a-z0-9+.-]*://[^\s?#]+)[?#][^\s]*", "$1?***", RegexOptions.IgnoreCase);
    }

    public async Task<string> ResolvePushUrlAsync(ActionContext context, RemoteEntry remote, CancellationToken token = default)
    {
        try
        {
            await Task.Run(() => CheckRemote(context, remote), token);
            var endpoint = (await GitReadProcess.RunAsync(context.Root, ["remote", "get-url", "--push", "--", remote.Name], token)).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint.StartsWith('-') || endpoint.Contains('\n') || endpoint.Contains('\r'))
                throw new IOException("远程推送端点无效或有多行，请使用单一明确目标。");
            // get-url already expands Git URL rules. Reusing its result must not
            // expand a second time, otherwise the reviewed endpoint is ambiguous.
            var repeated = (await GitReadProcess.RunAsync(context.Root, ["ls-remote", "--get-url", "--", endpoint], token)).TrimEnd('\r', '\n');
            if (repeated != endpoint) throw new IOException("远程 URL 存在级联 insteadOf 重写，无法绑定单一审阅目标。请配置不会再次重写的明确推送地址。");
            return endpoint;
        }
        catch (IOException ex) { throw new IOException(RedactUrl(ex.Message)); }
    }

    public async Task<string> RemoteHeadAsync(ActionContext context, RemoteEntry remote, string branch, GitAuthentication? authentication = null, CancellationToken token = default, string? expectedEndpoint = null)
    {
        await ValidateBranchAsync(context.Root, branch, token);
        var endpoint = await ResolvePushUrlAsync(context, remote, token);
        if (expectedEndpoint is not null && endpoint != expectedEndpoint) throw new IOException("实际推送地址已变化，请刷新审阅。");
        var output = await GitReadProcess.RunAsync(context.Root, ["ls-remote", "--heads", "--", endpoint, "refs/heads/" + branch], token,
            environment: authentication?.Environment, sanitize: text => RedactUrl(authentication?.Sanitize(text) ?? text));
        var line = output.Split('\n').FirstOrDefault(l => l.TrimEnd().EndsWith("\trefs/heads/" + branch, StringComparison.Ordinal));
        var sha = line?.Split('\t')[0] ?? "";
        if (sha.Length != 0 && (sha.Length != 40 || !sha.All(Uri.IsHexDigit))) throw new IOException("远程返回了不支持的引用格式。");
        return sha;
    }

    public Task<string> ExecuteAsync(ActionContext context, RemoteEntry remote, RemoteAction action, string branch, string? expectedRemote = null, GitAuthentication? authentication = null, string? reviewedRemote = null, string? reviewedPushUrl = null)
        => RepositoryWriter.Locked(context.Root, async () =>
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        RepositoryActions.Check(context, action == RemoteAction.Pull); CheckRemote(context, remote);
        if (action != RemoteAction.Fetch) await ValidateBranchAsync(context.Root, branch);
        var pushing = action is RemoteAction.Push or RemoteAction.ForceWithLease;
        var endpoint = pushing ? await ResolvePushUrlAsync(context, remote) : null;
        if (reviewedPushUrl is not null && endpoint != reviewedPushUrl) throw new IOException("实际推送地址已变化，请刷新审阅。");
        if (pushing && reviewedRemote is not null)
        {
            var currentRemote = await RemoteHeadAsync(context, remote, branch, authentication, expectedEndpoint: endpoint);
            if (currentRemote != reviewedRemote) throw new IOException("远程目标已变化，请刷新推送审阅。");
            // Credential prompts/network reads can take time; recheck the approved local snapshot.
            RepositoryActions.Check(context); CheckRemote(context, remote);
            if (action == RemoteAction.Push && reviewedRemote.Length != 0)
            {
                using var repo = new Repository(context.Root);
                var previous = repo.Lookup<Commit>(reviewedRemote);
                var approved = repo.Lookup<Commit>(context.Head ?? "");
                if (previous is null || approved is null || repo.ObjectDatabase.FindMergeBase(previous, approved)?.Sha != previous.Sha)
                    throw new IOException("普通推送必须快进已审阅的远程提交，未执行操作。");
            }
        }
        var fetchEndpoint = pushing ? await ResolveFetchUrlAsync(context.Root, remote.Name) : null;
        var tracking = pushing && fetchEndpoint == endpoint ? TrackingRefs(context.Root, remote, branch) : [];
        var args = new List<string>();
        switch (action)
        {
            case RemoteAction.Fetch:
                args.AddRange(["fetch", "--", remote.Name]); break;
            case RemoteAction.Pull:
                args.AddRange(["pull", "--ff-only", "--no-rebase", "--no-autostash", "--", remote.Name, "refs/heads/" + branch]); break;
            case RemoteAction.Push:
            case RemoteAction.ForceWithLease:
                if (context.Head is null) throw new IOException("没有可推送的提交。");
                // A URL can be matched to an existing remote by newer Git and
                // update its tracking refs before our compare-and-swap below.
                // A command-only remote with no fetch mapping leaves tracking
                // updates exclusively to that guarded step. Nothing is saved.
                var pushRemote = "gitx-push-" + Guid.NewGuid().ToString("N");
                args.AddRange(["-c", $"remote.{pushRemote}.url={endpoint}", "-c", $"remote.{pushRemote}.pushurl={endpoint}",
                    "push", "--porcelain", "--no-follow-tags", "--recurse-submodules=no"]);
                // A reviewed normal push has already been proved fast-forward above.
                // The exact lease closes the race between the read and the server update;
                // it does not grant permission to replace non-ancestor history.
                if (action == RemoteAction.Push && reviewedRemote is not null)
                    args.Add($"--force-with-lease=refs/heads/{branch}:{reviewedRemote}");
                if (action == RemoteAction.ForceWithLease)
                {
                    if (expectedRemote is null || expectedRemote.Length != 0 && (expectedRemote.Length != 40 || !expectedRemote.All(Uri.IsHexDigit))) throw new IOException("必须先读取并确认远程分支 SHA。");
                    args.Add($"--force-with-lease=refs/heads/{branch}:{expectedRemote}");
                }
                // Use the exact approved commit, not a movable local ref. One destination only.
                args.AddRange(["--", pushRemote, $"{context.Head}:refs/heads/{branch}"]); break;
        }
        var output = await Run(context.Root, args, authentication, pushing ? "push" : null);
        if (!pushing || tracking.Count == 0) return output;
        try
        {
            CheckRemote(context, remote);
            if (await ResolvePushUrlAsync(context, remote) != endpoint || await ResolveFetchUrlAsync(context.Root, remote.Name) != fetchEndpoint)
                throw new IOException("远程实际地址已变化");
            if (!tracking.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(TrackingRefs(context.Root, remote, branch).Keys))
                throw new IOException("远程跟踪映射已变化");
            foreach (var (reference, previous) in tracking)
                await GitWriteProcess.RepositoryAsync(context.Root, ["update-ref", "--no-deref", "-m", "gitx: push tracking update",
                    reference, context.Head!, previous]);
        }
        catch (IOException e)
        {
            // The remote write succeeded. Never present a local CAS conflict as
            // a failed push, or overwrite a ref advanced by another Git process.
            output += "\n推送已成功；本地跟踪引用未全部同步，请获取后查看状态：" + e.Message;
        }
        return output;
    });

    private static async Task<string> ResolveFetchUrlAsync(string root, string name)
    {
        try { return (await GitReadProcess.RunAsync(root, ["remote", "get-url", "--", name], default)).TrimEnd('\r', '\n'); }
        catch (IOException ex) { throw new IOException(RedactUrl(ex.Message)); }
    }

    private static Dictionary<string, string> TrackingRefs(string root, RemoteEntry remote, string branch)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // A distinct fetch endpoint can represent a different repository.
        if (remote.Url != remote.PushUrl) return result;
        using var repo = new Repository(root);
        foreach (var spec in repo.Network.Remotes[remote.Name].FetchRefSpecs)
        {
            if (!spec.SourceMatches("refs/heads/" + branch)) continue;
            var destination = spec.Transform("refs/heads/" + branch);
            if (!destination.StartsWith("refs/remotes/", StringComparison.Ordinal)) continue;
            var reference = repo.Refs[destination];
            if (reference is SymbolicReference) continue;
            result[destination] = reference?.TargetIdentifier ?? new string('0', 40);
        }
        return result;
    }

    private static async Task ValidateBranchAsync(string root, string branch, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-')) throw new IOException("请输入目标分支名称。");
        await GitReadProcess.RunAsync(root, ["check-ref-format", "refs/heads/" + branch], token);
    }
    private static void CheckRemote(ActionContext context, RemoteEntry remote)
    {
        using var repo = new Repository(context.Root); var current = repo.Network.Remotes[remote.Name];
        if (current is null || current.Url != remote.Url || (current.PushUrl ?? current.Url) != remote.PushUrl) throw new IOException("远程配置已变化，请重新打开远程操作。");
        // Git supports multiple push URLs; an explicit single endpoint avoids accidental extra destinations.
        if (string.IsNullOrWhiteSpace(remote.Url) || string.IsNullOrWhiteSpace(remote.PushUrl) || remote.PushUrl.StartsWith('-')) throw new IOException("远程 URL 无效。");
    }
    private static Task<string> Run(string root, IEnumerable<string> arguments, GitAuthentication? authentication, string? operationCommand = null)
        => GitWriteProcess.RunAsync(root, arguments, noEditor: true, literalPaths: false, includeErrorOutput: true,
            environment: authentication?.Environment, sanitize: text => RedactUrl(authentication?.Sanitize(text) ?? text), operationCommand: operationCommand);
}
