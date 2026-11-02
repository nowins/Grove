using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Services;

internal enum CommentCloseChoice { Cancel, Edit, Discard }

internal static class CommentCloseDialog
{
    public static Task<CommentCloseChoice> ShowAsync(Window owner, CommentAnchor anchor)
    {
        var dialog = new Window { Title = "未保存的本地评论", Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var discard = new Button { Content = "放弃草稿并关闭" };
        var edit = new Button { Content = "继续编辑", IsDefault = true, Classes = { "primary" } };
        cancel.Click += (_, _) => dialog.Close(CommentCloseChoice.Cancel);
        discard.Click += (_, _) => dialog.Close(CommentCloseChoice.Discard);
        edit.Click += (_, _) => dialog.Close(CommentCloseChoice.Edit);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "以下文件有一条未保存的评论：", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new Border { Classes = { "dialog-context" }, Child = new TextBlock { Text = $"{anchor.Path}\n{(anchor.Side == 0 ? "旧侧" : "新侧")} · L{anchor.StartLine}–{anchor.EndLine} · {anchor.Label}", TextWrapping = TextWrapping.Wrap } });
        content.Children.Add(new TextBlock { Text = "继续编辑可返回评论输入。放弃草稿并关闭将丢失本次未保存的输入，已保存的评论不受影响。", TextWrapping = TextWrapping.Wrap, Classes = { "dialog-hint" } });
        dialog.Content = DialogChrome.Layout(content, DialogChrome.Buttons(cancel, discard, edit)); DialogChrome.Apply(dialog);
        dialog.Opened += (_, _) => edit.Focus(NavigationMethod.Tab);
        return dialog.ShowDialog<CommentCloseChoice>(owner);
    }
}
