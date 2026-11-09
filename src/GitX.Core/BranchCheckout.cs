namespace GitX.Core;

public sealed record BranchCheckoutPlan(BranchEntry Target, string LocalName, bool CreateTracking, string? Error = null);

public static class BranchCheckout
{
    public static string? RemoteBranchName(BranchEntry target)
    {
        if (!target.Remote || target.RemoteName is not { Length: > 0 } remote) return null;
        var prefix = "refs/remotes/" + remote + "/";
        return target.CanonicalName.StartsWith(prefix, StringComparison.Ordinal) && target.CanonicalName.Length > prefix.Length
            ? target.CanonicalName[prefix.Length..] : null;
    }

    public static BranchCheckoutPlan Resolve(ActionContext context, BranchEntry target)
        => Resolve(context.Branches, target);

    public static BranchCheckoutPlan Resolve(IReadOnlyList<BranchEntry> branches, BranchEntry target)
    {
        if (!branches.Contains(target)) return new(target, "", false, "分支已不在当前快照中，请刷新。");
        if (!target.Remote) return new(target, target.Name, false);
        var shortName = RemoteBranchName(target);
        var tracking = branches.Where(b => !b.Remote &&
            (b.UpstreamCanonicalName is { } canonical ? canonical == target.CanonicalName : b.Upstream == target.Name)).ToArray();
        var existing = tracking.FirstOrDefault(b => b.Current) ?? tracking.FirstOrDefault(b => b.Name == shortName)
            ?? (tracking.Length == 1 ? tracking[0] : null);
        if (existing is not null) return new(existing, existing.Name, false);
        if (tracking.Length > 1) return new(target, "", false, "多个本地分支跟踪此远端分支，请在本地分支列表选择，或使用 New Branch 指定新名称。");
        if (string.IsNullOrEmpty(shortName) || shortName is "HEAD" or "@" || shortName.StartsWith('-'))
            return new(target, "", false, "无法确定有效的本地分支名称，请检查远端配置，或使用 New Branch 指定名称。");
        if (branches.Any(b => !b.Remote && b.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase)))
            return new(target, shortName, false, $"本地分支 {shortName} 已存在且未跟踪此远端分支；不会覆盖或改绑。请选择本地分支，或使用 New Branch。 ");
        return new(target, shortName, true);
    }

    public static string SuggestName(ActionContext context, BranchEntry? target)
    {
        var name = target is { Remote: true } ? RemoteBranchName(target) : target?.Name;
        if (string.IsNullOrWhiteSpace(name) || name is "HEAD" or "@" || name.StartsWith('-')) name = "new-branch";
        var names = context.Branches.Where(b => !b.Remote).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name)) return name;
        for (var suffix = 2; ; suffix++) { var candidate = name + "-" + suffix; if (!names.Contains(candidate)) return candidate; }
    }

    public static async Task ValidateNameAsync(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') || name is "HEAD" or "@") throw new IOException("请输入有效的新分支名称。");
        // Validate the literal ref, without expanding Git's @{-1} shorthand.
        try { await GitReadProcess.RunAsync(root, ["check-ref-format", "refs/heads/" + name], default); }
        catch (IOException error)
        {
            throw new IOException("无法使用此分支名称。请检查空格、连续点、特殊字符或结尾斜杠等 Git 引用规则。\n" + error.Message, error);
        }
    }
}
