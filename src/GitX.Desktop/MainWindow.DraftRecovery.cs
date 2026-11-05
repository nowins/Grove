using System.Text.Json;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;
public sealed partial class MainWindow
{
    private readonly TextDraftStore _draftStore = new(Path.Combine(LocalDataDirectory, "DraftRecovery"));
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly SemaphoreSlim _draftGate = new(1);
    private readonly Dictionary<string, TextDraftRecord> _draftOwned = [];
    private bool _draftEnabled, _draftDialogOpen;
    private (string Root, long Epoch, TextDraftRecord Record)? _pendingDraft;
    private void InitializeDraftRecovery()
    {
        try { _draftEnabled = _draftStore.Enabled; } catch { _model.ReportError("本地草稿恢复不可用；当前编辑仍只在内存中。"); }
        _draftTimer.Tick += async (_, _) => { _draftTimer.Stop(); await CheckpointDraftsAsync(); };
        _model.RepositoryOpened += async root =>
        {
            _pendingDraft = null;
            try
            {
                var listing = await Task.Run(() => _draftStore.List(root));
                if (!_closed && root == _model.Root && listing.Drafts.Count + listing.Expired + listing.Unreadable + listing.Unassigned > 0)
                    _model.ReportError($"本地草稿：{listing.Drafts.Count} 项可恢复，{listing.Expired} 项已过期，{listing.Unreadable} 项无法读取，{listing.Unassigned} 项仓库身份已变化未归属。请在“文件 → 本地草稿恢复”审阅；磁盘文件未更改。");
            }
            catch { if (!_closed && root == _model.Root) _model.ReportError("无法读取本地恢复草稿；原文件未更改。"); }
        };
    }
    private void ScheduleDraftCheckpoint()
    {
        if (!_draftEnabled || _closed || _draftDialogOpen || _textGuardPending) return;
        // Coalesce without resetting the deadline on every keystroke.
        if (!_draftTimer.IsEnabled) _draftTimer.Start();
    }
    private async Task CheckpointDraftsAsync(bool forClose = false)
    {
        if (!_draftEnabled || _closed || !_ready || _draftDialogOpen || _textGuardPending && !forClose) return;
        if (!await _draftGate.WaitAsync(0)) { ScheduleDraftCheckpoint(); return; }
        var root = _model.Root; var epoch = _model.RepositoryGeneration;
        try
        {
            if (root is null) return;
            var state = await ReadTextQueryAsync<TextSessions>("window.gitx.fileSessions()");
            if (state is null || state.Composing) { ScheduleDraftCheckpoint(); return; }
            foreach (var session in state.Sessions)
            {
                if (session.Busy) continue;
                var draft = await ReadTextQueryAsync<TextDraft>("window.gitx.fileDraft(" + JsonSerializer.Serialize(session.Id) + ")");
                if (draft is null || _closed || !_draftEnabled || root != _model.Root || epoch != _model.RepositoryGeneration || draft.RootKey != WorkingTextFiles.RootKey(root) ||
                    !_model.SourceTabs.Any(t => t.Id == draft.Id && t.LocalFile && t.Path == draft.Path)) continue;
                var key = draft.RootKey + "/" + draft.Path;
                _draftOwned.TryGetValue(key, out var owned);
                if (!draft.Dirty)
                {
                    if (owned is not null) { await Task.Run(() => _draftStore.Discard(root, owned)); _draftOwned.Remove(key); }
                    continue;
                }
                if (TextDraftStore.IsExcluded(draft.Path)) continue;
                if (!forClose && owned?.Text == draft.Text && owned.Baseline == draft.Baseline && DateTimeOffset.UtcNow - owned.Updated < TextDraftStore.Retention)
                {
                    if (!await Task.Run(() => _draftStore.Contains(root, owned))) throw new IOException("恢复副本已变化或被删除。");
                    continue;
                }
                var record = await Task.Run(() => _draftStore.Put(root, draft.Path, draft.Baseline, draft.Text, owned?.Revision));
                _draftOwned[key] = record;
            }
        }
        catch { if (!_closed && root == _model.Root && epoch == _model.RepositoryGeneration) _model.ReportError("草稿检查点未保存。请查看“本地草稿恢复”，或保存文件；当前内存草稿未丢弃。"); }
        finally { _draftGate.Release(); }
    }
    private async Task ShowDraftRecoveryAsync()
    {
        if (_draftDialogOpen) return;
        var root = _model.Root;
        var epoch = _model.RepositoryGeneration;
        _draftDialogOpen = true; _draftTimer.Stop();
        await _draftGate.WaitAsync();
        try
        {
            var result = await DraftRecoveryDialog.ShowAsync(this, root, _draftStore, _draftEnabled);
            _draftEnabled = _draftStore.Enabled;
            // Do not adopt somebody else's new revision. Missing old ownership
            // may be dropped only after the user explicitly reviews this dialog.
            if (root is null) return;
            var rootKey = WorkingTextFiles.RootKey(root);
            foreach (var pair in _draftOwned.Where(p => p.Value.Baseline.RootKey == rootKey).ToArray())
                if (!await Task.Run(() => _draftStore.Contains(root, pair.Value, includeExpired: true))) _draftOwned.Remove(pair.Key);
            if (result is null || _closed || root != _model.Root || epoch != _model.RepositoryGeneration) return;
            _pendingDraft = (root, epoch, result);
            if (!await _model.OpenRecentFileAsync(root, result.Path, null))
            { _pendingDraft = null; _model.ReportError("文件不存在、被移动或不可读；恢复副本保留，可在恢复窗口复制内容。不会创建原文件。"); }
            else await ApplyPendingDraftAsync();
        }
        catch { _model.ReportError("本地草稿恢复未完成；副本和原文件保留。请重新打开恢复窗口。"); }
        finally { _draftDialogOpen = false; _draftGate.Release(); ScheduleDraftCheckpoint(); }
    }
    private async Task ApplyPendingDraftAsync()
    {
        if (_pendingDraft is not { } pending || !_ready || _model.ActiveSourceTabId is not { } id || _loadedEditorVersion != _model.DocumentVersion) return;
        if (_closed || pending.Root != _model.Root || pending.Epoch != _model.RepositoryGeneration) { _pendingDraft = null; return; }
        if (!_model.SourceTabs.Any(t => t.Id == id && t.LocalFile && t.Path == pending.Record.Path)) return;
        _pendingDraft = null;
        DraftRestoreResult? restored;
        try { restored = await ReadTextQueryAsync<DraftRestoreResult>("({restored:window.gitx.fileRestore(" + JsonSerializer.Serialize(id) + "," + JsonSerializer.Serialize(pending.Record, TextJson) + ")})"); }
        catch
        {
            _model.ReportError("无法确认草稿恢复结果；编辑器内容可能已恢复，磁盘文件和恢复副本保留。请检查当前编辑器后再操作。");
            return;
        }
        if (restored?.Restored is null)
        { _model.ReportError("无法确认草稿恢复结果；编辑器内容可能已恢复，磁盘文件和恢复副本保留。请检查当前编辑器后再操作。"); return; }
        if (_closed || pending.Root != _model.Root || pending.Epoch != _model.RepositoryGeneration || id != _model.ActiveSourceTabId) return;
        if (restored?.Restored == true) _draftOwned[pending.Record.Baseline.RootKey + "/" + pending.Record.Path] = pending.Record;
        else _model.ReportError("当前文件已有编辑、只读或已变化；未替换编辑器内容，恢复副本保留。");
    }
}
