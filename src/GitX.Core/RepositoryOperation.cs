using LibGit2Sharp;

namespace GitX.Core;

public sealed record RepositoryOperation(string Kind, string Label, string LeftLabel, string RightLabel, string Detail)
{
    public static RepositoryOperation? Read(Repository repo)
    {
        var directory = repo.Info.Path;
        string Read(string path) => File.Exists(Path.Combine(directory, path)) ? File.ReadAllText(Path.Combine(directory, path)).Trim() : "";
        string Short(string value) => value.Length > 12 ? value[..12] : value;
        if (Directory.Exists(Path.Combine(directory, "rebase-merge")) || Directory.Exists(Path.Combine(directory, "rebase-apply")))
        {
            var merge = Directory.Exists(Path.Combine(directory, "rebase-merge"));
            var prefix = merge ? "rebase-merge/" : "rebase-apply/";
            var step = Read(prefix + (merge ? "msgnum" : "next")); var total = Read(prefix + (merge ? "end" : "last"));
            return new("rebase", "变基中", "变基目标 / 已重放结果（ours）", "正在重放的提交（theirs）",
                $"第 {step}/{total} 个提交 · onto {Short(Read(prefix + "onto"))} · {Short(Read("REBASE_HEAD"))}");
        }
        if (Read("CHERRY_PICK_HEAD") is { Length: > 0 } cherry)
            return new("cherry-pick", "Cherry-pick 中", "当前 HEAD（ours）", "被拣取的提交（theirs）", Short(cherry));
        if (Read("REVERT_HEAD") is { Length: > 0 } revert)
            return new("revert", "Revert 中", "当前 HEAD（ours）", "撤销后的版本（theirs）", Short(revert));
        if (Read("MERGE_HEAD") is { Length: > 0 } incoming)
            return new("merge", "合并中", "当前 HEAD（ours）", "传入版本（theirs）", $"HEAD ← {Short(incoming)}");
        return null;
    }

    internal static IEnumerable<string> StateFiles => ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "ORIG_HEAD", "sequencer/head", "sequencer/todo",
        "rebase-merge/onto", "rebase-merge/msgnum", "rebase-merge/end", "rebase-apply/onto", "rebase-apply/next", "rebase-apply/last"];
}
