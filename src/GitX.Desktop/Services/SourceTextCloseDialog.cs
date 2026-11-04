using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitX.Desktop.Services;
internal enum SourceTextCloseChoice { Cancel, Discard, Save }
internal static class SourceTextCloseDialog
{
    public static Task<SourceTextCloseChoice> ShowAsync(Window owner, IReadOnlyList<string> paths)
    {
        var dialog = new Window { Title = "未保存的文件", Width = 540, MaxHeight = 620, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel=new Button{Content="取消",IsCancel=true,IsDefault=true};var discard=new Button{Content="放弃这些草稿"};var save=new Button{Content="全部保存并继续",Classes={"primary"}};
        cancel.Click+=(_,_)=>dialog.Close(SourceTextCloseChoice.Cancel);discard.Click+=(_,_)=>dialog.Close(SourceTextCloseChoice.Discard);save.Click+=(_,_)=>dialog.Close(SourceTextCloseChoice.Save);
        var content=new StackPanel{Spacing=12};content.Children.Add(new TextBlock{Text="以下文件有未保存的文本改动：",TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new ScrollViewer{MaxHeight=280,Content=new TextBlock{Text=string.Join('\n',paths),TextWrapping=TextWrapping.Wrap}});
        content.Children.Add(new TextBlock{Text="保存只写入工作区，不会暂存或提交。若磁盘文件已变化，会保留草稿并停止关闭。放弃草稿不会改动磁盘文件。",TextWrapping=TextWrapping.Wrap,Classes={"dialog-hint"}});
        dialog.Content=DialogChrome.Layout(content,DialogChrome.Buttons(cancel,discard,save));DialogChrome.Apply(dialog);dialog.Opened+=(_,_)=>cancel.Focus();return dialog.ShowDialog<SourceTextCloseChoice>(owner);
    }
}
