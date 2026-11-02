using System.Text;
using System.Text.Json;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;
using LibGit2Sharp;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private sealed record CommentDraft(string Id, CommentAnchor Anchor, LocalComment? Original);
    private readonly LocalCommentStore _comments = new(Path.Combine(Environment.GetEnvironmentVariable("GITX_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitX"), "Comments"));
    private CommentDraft? _commentDraft;
    private IReadOnlyList<LocalComment> _commentItems = [];
    private string? _commentRepository;
    private string? _commentRoot;
    private bool _commentBusy;
    private long _commentSequence;
    private long _commentPublication;
    private string _commentMessage = "";
    private bool _commentClosePending;
    private sealed record CommentReveal(string Root, long Version, string Id, string Revision);
    private CommentReveal? _commentReveal;
    private long _commentNavigation;

    private Task OpenLocalNotesAsync()
    {
        if (_closed || !_ready || _model.Root is null) return Task.CompletedTask;
        _webView?.Focus();
        return InvokeEditorAsync("window.gitx.openNotes()");
    }

    private async Task ConfirmCommentCloseAsync()
    {
        if (_commentClosePending || _commentDraft is not { } draft) return;
        _commentClosePending = true;
        try
        {
            var choice = await CommentCloseDialog.ShowAsync(this, draft.Anchor);
            if (_closed || _commentDraft?.Id != draft.Id) return;
            if (_commentBusy) { _model.ReportError("评论操作尚未完成，请等待完成后关闭。"); return; }
            if (choice == CommentCloseChoice.Edit)
            {
                // ShowDialog can complete before the owner's activation/focus
                // restoration finishes. Re-enter the native child afterwards,
                // even if Avalonia retained logical focus on the WebView.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_closed || !IsActive || _commentDraft?.Id != draft.Id) return;
                    FocusManager?.Focus(null);
                    _webView?.Focus();
                }, DispatcherPriority.Background);
                if (_closed || !IsActive || _commentDraft?.Id != draft.Id) return;
                await InvokeEditorAsync("window.gitx.openCommentDraft(" + JsonSerializer.Serialize(draft.Id) + ")");
            }
            else if (choice == CommentCloseChoice.Discard)
            {
                _commentDraft = null;
                PublishComments();
                // Re-enter Closing so Git/merge write guards still run.
                Close();
            }
        }
        catch (Exception e) { if (!_closed) _model.ReportError("评论关闭确认失败：" + e.Message); }
        finally { _commentClosePending = false; }
    }

    private async Task RefreshCommentsAsync()
    {
        var owner = ++_commentSequence;
        var root = _model.Root; var version = _publishedVersion;
        if (_closed || _server is null) return;
        try
        {
            var id = root is null ? null : await Task.Run(() => { using var repository = new Repository(root); return CommentContext.RepositoryIdFor(repository); });
            var items = id is null ? [] : await _comments.ReadAsync(id);
            if (_closed || owner != _commentSequence || root != _model.Root || version != _publishedVersion) return;
            _commentRoot = root; _commentRepository = id; _commentItems = items;
            PublishComments();
        }
        catch (Exception e)
        {
            if (_closed || owner != _commentSequence || root != _model.Root || version != _publishedVersion) return;
            _commentRoot = root; _commentItems = []; _commentRepository = null; _commentMessage = e.Message;
            PublishComments();
        }
    }

    private async void PublishComments()
    {
        if (_server is null || _closed) return;
        var owner = ++_commentPublication;
        var root = _model.Root;
        var current = root == _commentRoot;
        var reveal = _commentReveal is { } target && target.Root == root && target.Version == _publishedVersion ? new { target.Id, target.Revision } : null;
        var payload = new { version = _publishedVersion, repositoryId = current ? _commentRepository : null,
            comments = current ? _commentItems : [], draft = _commentDraft, busy = _commentBusy, message = _commentMessage, reveal };
        try
        {
            var bytes = await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(payload,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            if (_closed || owner != _commentPublication || root != _model.Root || payload.version != _publishedVersion) return;
            _server.PublishComments(bytes);
            if (_ready) await InvokeEditorAsync("window.gitx.refreshComments()");
        }
        catch (Exception e) { if (!_closed && owner == _commentPublication) _model.ReportError(e.Message); }
    }

    private async Task HandleCommentAsync(JsonElement message)
    {
        if (_commentBusy) return;
        var ownsOperation = false;
        try
        {
            var action = message.GetProperty("action").GetString();
            var navigation = action == "list" ? _commentNavigation : ++_commentNavigation;
            if (action is not ("list" or "open-source")) _model.CancelNoteSource();
            _commentMessage = "";
            if (action == "list") { await RefreshCommentsAsync(); return; }
            if (action == "cancel") { _commentDraft = null; PublishComments(); return; }
            if (action == "begin")
            {
                if (_commentDraft is not null) { PublishComments(); return; }
                var document = _model.CurrentDocument;
                if (document?.CommentContext is null || message.GetProperty("version").GetInt64() != _model.DocumentVersion || document.CommentContext.Snapshot != message.GetProperty("snapshot").GetString())
                    throw new IOException("文件版本已变化，请重新选择代码行。");
                var anchor = CommentAnchor.Capture(document, message.GetProperty("side").GetInt32(), message.GetProperty("start").GetInt32(), message.GetProperty("end").GetInt32());
                _commentDraft = new(Guid.NewGuid().ToString("N"), anchor, null);
                PublishComments(); return;
            }
            if (action == "save")
            {
                var draft = _commentDraft;
                if (draft is null || draft.Id != message.GetProperty("id").GetString()) throw new IOException("草稿已变化，请重新打开评论。");
                var body = message.GetProperty("body").GetString() ?? "";
                ownsOperation = _commentBusy = true; PublishComments();
                await _comments.SaveAsync(draft.Anchor, body, draft.Original);
                _commentDraft = null; _commentMessage = "已保存本地评论";
            }
            else
            {
                if (_commentRoot != _model.Root || _commentRepository is null || message.GetProperty("repositoryId").GetString() != _commentRepository)
                    throw new IOException("仓库已变化，请刷新评论列表。");
                if (action is "edit" or "delete" or "open-source")
                {
                    var item = _commentItems.FirstOrDefault(c => c.Id == message.GetProperty("id").GetString() && c.Revision == message.GetProperty("revision").GetString())
                        ?? throw new IOException("评论已变化，请刷新列表。");
                    if (action == "open-source")
                    {
                        if (_commentDraft is not null) throw new IOException("请先保存或取消当前草稿，再打开其他笔记的源码。");
                        var root = _model.Root!; var version = _model.DocumentVersion;
                        var opened = await _model.OpenNoteSourceAsync(root, item.Anchor);
                        if (_closed || root != _model.Root || navigation != _commentNavigation) return;
                        if (opened)
                        {
                            _commentReveal = new(root, _model.DocumentVersion, item.Id, item.Revision);
                            _commentMessage = "已定位笔记代码"; await RefreshCommentsAsync();
                        }
                        else if (version == _model.DocumentVersion) _commentMessage = "当前源码与笔记原版本不一致或文件已不存在，请查看原快照。";
                        return;
                    }
                    if (action == "edit")
                    { _commentDraft ??= new(Guid.NewGuid().ToString("N"), item.Anchor, item); PublishComments(); return; }
                    if (_commentDraft?.Original?.Id == item.Id) throw new IOException("请先保存或取消此评论的编辑。");
                    ownsOperation = _commentBusy = true; PublishComments();
                    await _comments.DeleteAsync(item); _commentMessage = "已删除本地评论";
                }
                else if (action is "copy" or "export")
                {
                    var id = _commentRepository;
                    ownsOperation = _commentBusy = true; PublishComments();
                    var items = await _comments.ReadAsync(id);
                    if (items.Count == 0) throw new IOException("当前仓库没有已保存的评论。");
                    var markdown = await Task.Run(() => CommentMarkdown.Export(items));
                    if (action == "copy")
                    {
                        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。");
                        await clipboard.SetTextAsync(markdown); _commentMessage = $"已复制 {items.Count} 条评论为 Markdown";
                    }
                    else
                    {
                        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                        { Title = "导出本地行评论", SuggestedFileName = "grove-comments.md", DefaultExtension = "md",
                          FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }] });
                        if (file is not null)
                        {
                            await using var output = await file.OpenWriteAsync();
                            output.SetLength(0);
                            await output.WriteAsync(Encoding.UTF8.GetBytes(markdown));
                            _commentMessage = $"已导出 {items.Count} 条评论";
                        }
                    }
                }
            }
        }
        catch (Exception e) { if (!_closed) _commentMessage = e.Message; }
        finally
        {
            // A concurrent list request must not finish another operation's lease.
            if (ownsOperation) { _commentBusy = false; await RefreshCommentsAsync(); }
            else PublishComments();
        }
    }
}
