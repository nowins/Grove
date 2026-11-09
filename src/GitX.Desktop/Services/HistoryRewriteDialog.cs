using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Services;

internal static class HistoryRewriteDialog
{
    private sealed record ActionChoice(RewriteAction Action, string Label);
    private sealed class Row(RewriteCommit commit, RewriteStep step)
    {
        public RewriteCommit Commit { get; } = commit;
        public RewriteAction Action { get; set; } = step.Action;
        public string Message { get; set; } = step.Action == RewriteAction.Reword ? step.Message : commit.Message;
    }
    public static Task<RewriteStep[]?> ShowAsync(Window owner, string title, HistoryRewritePlan plan, IReadOnlyList<RewriteStep> initial)
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = title, Width = 780, Height = 620, MinWidth = 650, MinHeight = 510, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var rows = new ObservableCollection<Row>(initial.Select(s => new Row(plan.Commits.Single(c => c.Sha == s.Sha), s)));
        var choices = new[] { new ActionChoice(RewriteAction.Pick, "保留 · Pick"), new(RewriteAction.Reword, "修改说明 · Reword"), new(RewriteAction.Squash, "合并说明 · Squash"),
            new(RewriteAction.Fixup, "保留前条说明 · Fixup"), new(RewriteAction.Drop, "丢弃 · Drop") };
        var list = new ListBox { ItemsSource = rows, MinHeight = 100 };
        AutomationProperties.SetName(list, "历史整理顺序，从旧到新");
        var message = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100 };
        AutomationProperties.SetName(message, "所选提交的新说明");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "dialog-hint" } };
        var accept = new Button { Content = "执行历史整理", Classes = { "primary" } };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var up = new Button { Content = "上移" }; var down = new Button { Content = "下移" };
        RewriteStep[] Steps() => rows.Select(r => new RewriteStep(r.Commit.Sha, r.Action, r.Message)).ToArray();
        void Validate()
        {
            try { HistoryRewrite.Validate(plan, Steps()); error.Text = "执行前保存原历史恢复记录；冲突或空改动将暂停，可继续、跳过或中止。"; accept.IsEnabled = true; }
            catch (IOException ex) { error.Text = ex.Message; accept.IsEnabled = false; }
            up.IsEnabled = list.SelectedIndex > 0; down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < rows.Count - 1;
        }
        void Select()
        {
            if (list.SelectedItem is Row row) { message.Text = row.Message; message.IsEnabled = row.Action == RewriteAction.Reword; }
            else { message.Text = ""; message.IsEnabled = false; }
            Validate();
        }
        list.ItemTemplate = new FuncDataTemplate<Row>((row, _) =>
        {
            if (row is null) return new Border();
            var grid = new Grid { ColumnDefinitions = new("190,*"), ColumnSpacing = 8, Margin = new(0,2) };
            var action = new ComboBox { ItemsSource = choices, SelectedItem = choices.Single(c => c.Action == row.Action), HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemTemplate = new FuncDataTemplate<ActionChoice>((choice, _) => new TextBlock { Text = choice?.Label }) };
            AutomationProperties.SetName(action, "整理动作 " + row.Commit.Sha[..8]);
            action.SelectionChanged += (_, _) =>
            {
                if (action.SelectedItem is not ActionChoice choice) return;
                row.Action = choice.Action; list.SelectedItem = row; Select();
            };
            grid.Children.Add(action);
            var label = new TextBlock { Text = row.Commit.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(label, row.Commit.Message); Grid.SetColumn(label, 1); grid.Children.Add(label); return grid;
        });
        list.SelectionChanged += (_, _) => Select();
        message.TextChanged += (_, _) => { if (list.SelectedItem is Row { Action: RewriteAction.Reword } row) row.Message = message.Text ?? ""; Validate(); };
        void Move(int offset) { var index = list.SelectedIndex; if (index < 0 || index + offset < 0 || index + offset >= rows.Count) return; var row = rows[index]; rows.Move(index, index + offset); list.SelectedItem = row; Validate(); }
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        cancel.Click += (_, _) => dialog.Close(null); accept.Click += (_, _) => { if (accept.IsEnabled) dialog.Close(Steps()); };
        var body = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,Auto,Auto"), RowSpacing = 10 };
        var description = new TextBlock { Text = $"{plan.Context.Branch} · {plan.Commits.Count} 个提交（从旧到新）\n上移／下移调整顺序；Squash 合并说明，Fixup 保留前条说明，Drop 删除提交。\n仅整理当前分支的线性、未发布历史；发布状态按本地远端跟踪引用判断。", TextWrapping = TextWrapping.Wrap };
        body.Children.Add(description); Grid.SetRow(list, 1); body.Children.Add(list);
        var moves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { up, down } }; Grid.SetRow(moves, 2); body.Children.Add(moves);
        var caption = new TextBlock { Text = "所选提交说明（选择 Reword 后可修改）", Classes = { "dialog-hint" } }; Grid.SetRow(caption, 3); body.Children.Add(caption);
        Grid.SetRow(message, 4); body.Children.Add(message); Grid.SetRow(error, 5); body.Children.Add(error);
        dialog.Content = DialogChrome.Layout(body, DialogChrome.Buttons(cancel, accept)); DialogChrome.Apply(dialog);
        list.SelectedIndex = Math.Max(0, initial.ToList().FindIndex(s => s.Action == RewriteAction.Reword)); Validate();
        dialog.Opened += (_, _) => cancel.Focus(); return dialog.ShowDialog<RewriteStep[]?>(owner);
    }
}
