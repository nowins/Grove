using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly HistoryReferencePreferenceStore _historyRefPreferences = new(LocalDataDirectory);
    private async Task OpenHistoryReferencePickerAsync(Control anchor)
    {
        if (_model.Root is not { } root || ToggleShellPopup(anchor)) return;
        var generation = _shellGeneration;
        try
        {
            var preferences = HistoryReferencePreferences.Empty;
            string? preferenceError = null;
            try { preferences = await _historyRefPreferences.ReadAsync(root); }
            catch (Exception ex) { preferenceError = "偏好未能读取，仍可筛选：" + ex.Message; }
            if (_closed || root != _model.Root || generation != _shellGeneration) return;
            var catalog = new HistoryReferenceChoices(_model.HistoryReferenceCandidates);
            var panel = new StackPanel { Width = 390, Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "筛选历史提交", Classes = { "dialog-label" }, Margin = new(4,4) });
            var search = new TextBox { PlaceholderText = "搜索分支、远端或标签", Classes = { "search" }, Margin = new(4,4,4,6) };
            AutomationProperties.SetName(search, "搜索历史引用"); panel.Children.Add(search);
            var list = new ListBox { MaxHeight = 320, MinHeight = 70, Background = Brushes.Transparent, BorderThickness = new(0), AutoScrollToSelectedItem = true };
            list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(IsEnabledProperty, new Binding(nameof(HistoryReferenceChoice.Selectable))) } });
            AutomationProperties.SetName(list, "历史分支与标签筛选"); panel.Children.Add(list);
            var status = new TextBlock { Classes = { "filter-summary" }, Margin = new(4), TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
            panel.Children.Add(new TextBlock { Text = "Enter 筛选 · 列表 Space 收藏 · Esc 取消", Classes = { "muted" }, FontSize = UiTypography.SecondarySize, Margin = new(4) });
            var saving = false; long renderVersion = 0;
            bool Current() => !_closed && root == _model.Root && generation == _shellGeneration;
            void Render()
            {
                var render = ++renderVersion;
                var restoreList = list.IsKeyboardFocusWithin;
                // Keep native focus inside the popup while virtualized rows are
                // replaced. Removing its focused row otherwise activates the
                // underlying native WebView and light-dismisses the chooser.
                if (restoreList) search.Focus();
                var epoch = _inputEpoch; var query = search.Text;
                var selected = list.SelectedItem as HistoryReferenceChoice;
                var rows = catalog.Build(preferences, _model.HistoryReferenceKey, search.Text ?? "");
                list.ItemsSource = rows;
                list.SelectedItem = rows.FirstOrDefault(r => r.Selectable && r.Key == selected?.Key && r.Group == selected?.Group) ?? rows.FirstOrDefault(r => r.Selected) ?? rows.FirstOrDefault(r => r.Selectable);
                var count = rows.Where(r => r.Reference is not null).Select(r => r.Key).Distinct(StringComparer.Ordinal).Count();
                status.Text = preferenceError ?? (count == 0 && rows.All(r => !r.Selectable) ? "没有匹配的分支或标签" : $"{count:N0} 个引用 · 仅改变历史范围");
                if (restoreList) Dispatcher.UIThread.Post(() =>
                {
                    if (Current() && renderVersion == render && _inputEpoch == epoch && search.Text == query && search.IsKeyboardFocusWithin)
                        FocusShellList(list);
                }, DispatcherPriority.Loaded);
            }
            async Task FavoriteAsync(HistoryReferenceChoice row)
            {
                if (!Current() || !row.CanFavorite || saving) return;
                saving = true; status.Text = "正在保存收藏…";
                try
                {
                    var next = await _historyRefPreferences.ToggleFavoriteAsync(root, row.Key);
                    if (!Current()) return;
                    preferences = next; preferenceError = null;
                }
                catch (Exception ex) { if (Current()) _model.ReportError("收藏未能保存：" + ex.Message); }
                finally { saving = false; if (Current()) Render(); }
            }
            async Task ApplyAsync(HistoryReferenceChoice row)
            {
                if (!Current() || !row.Selectable) return;
                try
                {
                    var key = row.Key == "all" ? null : row.Key;
                    if (row.Reference is not null && !_model.HistoryReferenceCandidates.Any(r => r.CanonicalName == key))
                    { catalog = new(_model.HistoryReferenceCandidates); Render(); status.Text = "该引用已失效，请重新选择。"; return; }
                    CloseShellPopup(restoreFocus: true);
                    await _model.SelectHistoryReferenceAsync(key);
                    if (_closed || root != _model.Root || _model.HistoryReferenceKey != key || row.Reference is null) return;
                    try { await _historyRefPreferences.RememberAsync(root, row.Key); }
                    catch (Exception ex) { if (!_closed && root == _model.Root) _model.ReportError("历史筛选已应用，最近引用未能保存：" + ex.Message); }
                }
                catch (Exception ex) { if (!_closed && root == _model.Root) _model.ReportError("历史筛选未能应用：" + ex.Message); }
            }
            list.ItemTemplate = new FuncDataTemplate<HistoryReferenceChoice>((row, _) =>
            {
                if (row is null) return new Border();
                if (row.IsHeading) return new TextBlock { Text = row.Name, Classes = { "muted" }, FontSize = UiTypography.SecondarySize, Margin = new(4,6,4,2) };
                var content = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("18,*,Auto"), ColumnSpacing = 6, Margin = new(2,2) };
                if (row.Selected) content.Children.Add(IconContent.Icon("CheckIcon"));
                var label = new StackPanel { Spacing = 1 };
                label.Children.Add(new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                if (row.Detail.Length > 0) label.Children.Add(new TextBlock { Text = row.Detail, Classes = { "muted" }, FontSize = UiTypography.SecondarySize, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(label,1); content.Children.Add(label); ToolTip.SetTip(label,row.Key);
                if (row.CanFavorite)
                {
                    var star = new Button { Content = FavoriteIcon(row.Favorite), Classes = { "toolbar", "icon-button" }, IsEnabled = !saving, VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
                    AutomationProperties.SetName(star, (row.Favorite ? "取消收藏 " : "收藏 ") + row.Name + " " + row.KindLabel);
                    ToolTip.SetTip(star,row.Favorite ? "取消收藏" : "收藏");
                    star.Click += async (_,e) => { e.Handled = true; await FavoriteAsync(row); };
                    Grid.SetColumn(star,2); content.Children.Add(star);
                }
                return content;
            });
            search.TextChanged += (_,_) => Render(); Render();
            panel.AddHandler(KeyDownEvent, async (_,key) =>
            {
                if (search.GetVisualDescendants().OfType<TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText))) return;
                var button = (key.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true;
                if (key.Key == Key.Enter && !button && list.SelectedItem is HistoryReferenceChoice row)
                { key.Handled = true; await ApplyAsync(row); }
                else if (key.Key == Key.Space && list.IsKeyboardFocusWithin && !button && list.SelectedItem is HistoryReferenceChoice favorite)
                { key.Handled = true; await FavoriteAsync(favorite); }
                else if (key.Key is Key.Down or Key.Up && key.KeyModifiers == KeyModifiers.None && !button)
                {
                    key.Handled = true;
                    // Enter the selected result before advancing. With a single
                    // match, advancing first leaves focus in the search box and
                    // Space would type a space instead of toggling its favorite.
                    if (search.IsKeyboardFocusWithin && list.SelectedItem is HistoryReferenceChoice { Selectable: true })
                    { list.ScrollIntoView(list.SelectedIndex); FocusShellList(list); return; }
                    var delta = key.Key == Key.Down ? 1 : -1; var index = list.SelectedIndex + delta;
                    while (index >= 0 && index < list.ItemCount && list.Items[index] is HistoryReferenceChoice { Selectable:false }) index += delta;
                    if (index >= 0 && index < list.ItemCount) { list.SelectedIndex = index; list.ScrollIntoView(index); FocusShellList(list); }
                }
            },RoutingStrategies.Tunnel);
            list.AddHandler(PointerReleasedEvent, async (_,pointer) =>
            {
                if (pointer.InitialPressMouseButton != MouseButton.Left || (pointer.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true) return;
                if ((pointer.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is HistoryReferenceChoice { Selectable:true } row) await ApplyAsync(row);
            },RoutingStrategies.Bubble);
            ShowHistoryPopup(panel,anchor,search);
            Control FavoriteIcon(bool selected)
            {
                var icon = IconContent.Icon("StarIcon");
                if (selected) icon.Fill = (IBrush)this.FindResource("AccentBrush")!;
                return icon;
            }
        }
        catch (Exception ex) { if (!_closed && root == _model.Root && generation == _shellGeneration) _model.ReportError("历史引用筛选不可用：" + ex.Message); }
    }
}
