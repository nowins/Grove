using GitX.Core;

namespace GitX.Desktop.Services;

internal static class BranchActionAvailability
{
    public static BranchEntry CheckoutTarget(ActionContext context, BranchEntry target) => target.Remote
        ? context.Branches.FirstOrDefault(b => !b.Remote && b.Upstream == target.Name) ?? target : target;

    public static string? DisabledReason(ActionContext context, BranchAction action, BranchEntry? target)
    {
        if (context.InOperation) return "请先完成或中止当前 Git 操作。";
        if (context.Head is null) return "请先创建初始提交。";
        if (action == BranchAction.Create) return null;
        if (target is null) return "请选择分支。";
        if (action == BranchAction.Switch) target = CheckoutTarget(context, target);
        if (target.Current) return "这已经是当前分支。";
        if (action == BranchAction.Delete) return target.Remote ? "这里只删除已合并的本地分支。" : null;
        return context.Dirty ? "存在本地改动，请先提交或 Stash。" : null;
    }
}
