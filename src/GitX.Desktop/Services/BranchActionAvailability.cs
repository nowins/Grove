using GitX.Core;

namespace GitX.Desktop.Services;

internal static class BranchActionAvailability
{
    public static BranchEntry CheckoutTarget(ActionContext context, BranchEntry target) => BranchCheckout.Resolve(context, target).Target;

    public static string? DisabledReason(ActionContext context, BranchAction action, BranchEntry? target, IReadOnlyList<WorktreeEntry>? worktrees = null)
        => DisabledReason(context.Head, context.Dirty, context.InOperation, context.Branches, action, target, worktrees);

    public static string? DisabledReason(string? head, bool dirty, bool inOperation, IReadOnlyList<BranchEntry> branches,
        BranchAction action, BranchEntry? target, IReadOnlyList<WorktreeEntry>? worktrees = null)
    {
        if (inOperation) return "请先完成或中止当前 Git 操作。";
        if (head is null) return "请先创建初始提交。";
        if (action == BranchAction.Create) return null;
        if (action == BranchAction.CreateAndSwitch) return dirty ? "存在本地改动，请先提交或 Stash，或取消勾选创建后检出。" : null;
        if (target is null) return "请选择分支。";
        if (action == BranchAction.Switch)
        {
            var plan = BranchCheckout.Resolve(branches, target);
            if (plan.Error is not null) return plan.Error;
            target = plan.Target;
        }
        if (action is BranchAction.Switch or BranchAction.Delete or BranchAction.Rename && !target.Remote && worktrees?.FirstOrDefault(w => !w.Current && w.Branch == target.CanonicalName) is { } occupied)
            return "此分支已在另一个工作树使用：" + occupied.Root + "。可打开该工作树，不会强制检出。";
        if (action == BranchAction.Rename) return target.Remote ? "这里只重命名本地分支。" : null;
        if (target.Current) return "这已经是当前分支。";
        if (action == BranchAction.Delete) return target.Remote ? "这里只删除已合并的本地分支。" : null;
        return dirty ? "存在本地改动，请先提交或 Stash。" : null;
    }
}
