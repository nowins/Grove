using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Services;

internal sealed record BranchCreateChoice(string Name, bool Checkout);
internal static class BranchCreateDialog
{
    public static Task<BranchCreateChoice?> ShowAsync(Window owner, ActionContext context, BranchEntry? target, CommitEntry? commit = null)
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = "New Branch", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var source = commit is not null ? "提交" : target?.Name ?? context.Branch;
        var sha = commit?.Sha ?? target?.Sha ?? context.Head ?? "";
        var description = new TextBlock { Text = $"从 {source}（{sha[..Math.Min(8, sha.Length)]}）创建分支。不会覆盖已有分支。", TextWrapping = TextWrapping.Wrap };
        var root = new TextBlock { Text = "当前工作树：" + context.Root, Classes = { "dialog-hint" }, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(root, context.Root);
        var input = new TextBox { Text = BranchCheckout.SuggestName(context, target) }; AutomationProperties.SetName(input, "新分支名称");
        var checkout = new CheckBox { Content = "创建后检出（Checkout）", IsChecked = !context.Dirty, IsEnabled = !context.Dirty };
        var tracking = commit is null && target is { Remote: true, RemoteName: not null } ? "新分支将跟踪 " + target.Name + "。" : "从所选提交创建本地分支，不设置远端跟踪。";
        var hint = new TextBlock { Text = tracking + (context.Dirty ? " 当前有未提交改动，仅创建分支；工作区和索引保持原样。" : " 未勾选检出时仍停留在当前分支。"), Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap, Foreground = (IBrush)Application.Current!.FindResource("ErrorTextBrush")! };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var create = new Button { Content = "创建", Classes = { "primary" } };
        var validating = false;
        input.TextChanged += (_, _) => { error.IsVisible = false; create.IsEnabled = !validating && !string.IsNullOrWhiteSpace(input.Text); };
        cancel.Click += (_, _) => dialog.Close(null);
        create.Click += async (_, _) =>
        {
            if (validating) return; var name = input.Text ?? ""; validating = true; create.IsEnabled = false;
            try
            {
                await BranchCheckout.ValidateNameAsync(context.Root, name);
                if (!dialog.IsVisible) return;
                if (input.Text != name) throw new IOException("名称已改变，请再次确认创建。");
                if (context.Branches.Any(b => !b.Remote && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("本地分支已存在，请修改名称；不会覆盖已有分支。");
                dialog.Close(new BranchCreateChoice(name, checkout.IsChecked == true));
            }
            catch (Exception ex) { if (dialog.IsVisible) { error.Text = ex.Message; error.IsVisible = true; input.Focus(); } }
            finally { validating = false; if (dialog.IsVisible) create.IsEnabled = !string.IsNullOrWhiteSpace(input.Text); }
        };
        var field = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "分支名称", Classes = { "dialog-label" } }, input } };
        dialog.Content = DialogChrome.Layout(new StackPanel { Spacing = 12, Children = { description, root, field, checkout, hint, error } }, DialogChrome.Buttons(cancel, create));
        DialogChrome.Apply(dialog);
        // Enter belongs to the input/IME; creating a branch requires the button.
        dialog.Opened += (_, _) => { input.Focus(NavigationMethod.Tab); input.SelectAll(); };
        return dialog.ShowDialog<BranchCreateChoice?>(owner);
    }
}
