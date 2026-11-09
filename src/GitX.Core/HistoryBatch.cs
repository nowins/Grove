using LibGit2Sharp;

namespace GitX.Core;

public sealed record HistoryBatchPlan(ActionContext Context, HistoryAction Action, IReadOnlyList<CommitEntry> Commits, int Mainline)
{
    public bool NeedsMainline => Commits.Any(c => c.Parents?.Count > 1);
}

public static class HistoryBatch
{
    public static Task<HistoryBatchPlan> PrepareAsync(ActionContext context, HistoryAction action, IEnumerable<string> selection,
        int mainline = 0, CancellationToken token = default)
    {
        var shas = selection.ToArray();
        return ReadWork.Cpu.Run(() =>
        {
            if (action is not (HistoryAction.CherryPick or HistoryAction.Revert)) throw new IOException("此操作不支持批量提交。");
            if (shas.Length is < 1 or > 100 || shas.Distinct(StringComparer.Ordinal).Count() != shas.Length)
                throw new IOException("请选择 1–100 个不同的提交。");
            RepositoryActions.Check(context, clean: true);
            if (context.Head is null) throw new IOException("当前分支尚无提交。");
            using var repo = new Repository(context.Root);
            var selected = shas.Select(sha => RepositoryActions.CommitSha(context.Root, sha)).ToHashSet(StringComparer.Ordinal);
            var ordered = new List<CommitEntry>();
            foreach (var commit in repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = selected.Order(StringComparer.Ordinal).ToArray(),
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }))
            {
                token.ThrowIfCancellationRequested();
                if (!selected.Contains(commit.Sha)) continue;
                ordered.Add(new(commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.When, commit.Parents.Select(p => p.Sha).ToArray()));
                if (ordered.Count == selected.Count) break;
            }
            if (ordered.Count != selected.Count) throw new IOException("所选提交已变化，请重新选择。");
            // Use graph order, independent of Log filters, column sorting and author timestamps.
            if (action == HistoryAction.CherryPick) ordered.Reverse();
            if (mainline < 0 || mainline > 0 && ordered.Any(c => Math.Max(1, c.Parents!.Count) < mainline))
                throw new IOException("主线父提交序号不适用于所有所选提交；包含普通提交时请选择主线 1，或分开操作。");
            return new HistoryBatchPlan(context, action, ordered.AsReadOnly(), mainline);
        }, token);
    }

    public static Task<string> ExecuteAsync(HistoryBatchPlan plan) => RepositoryWriter.Locked(plan.Context.Root, async () =>
    {
        var verified = await PrepareAsync(plan.Context, plan.Action, plan.Commits.Select(c => c.Sha), plan.Mainline);
        if (!verified.Commits.Select(c => c.Sha).SequenceEqual(plan.Commits.Select(c => c.Sha))) throw new IOException("批量执行顺序已变化，请重新审阅。");
        if (verified.NeedsMainline && plan.Mainline == 0) throw new IOException("合并提交必须明确选择主线父提交。");
        RepositoryActions.Check(plan.Context, clean: true);
        var kind = plan.Action == HistoryAction.CherryPick ? "cherry-pick" : "revert";
        var args = new List<string> { kind, "--no-edit" };
        if (verified.NeedsMainline) args.AddRange(["-m", plan.Mainline.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(verified.Commits.Select(c => c.Sha));
        // One native sequencer owns the entire batch: continue/skip/abort retains the remaining commits.
        return await RepositoryActions.RunWithConflicts(plan.Context.Root, kind, args);
    });
}
