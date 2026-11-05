using System.Text.Json;
using Avalonia.Platform;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;
public sealed partial class MainWindow
{
    private sealed record TextSession(string Id,string Path,string RootKey,bool Dirty,bool Busy,bool Editable,bool Conflict);
    private sealed record TextSessions(TextSession[] Sessions,bool Composing);
    private sealed record TextDraft(string Id,string Path,string RootKey,string Model,int Checkpoint,string Expected,string Text,bool Dirty,WorkingTextEditInfo Baseline);
    private readonly WorkingTextWriter _textWriter=new();
    private readonly HashSet<string> _textEditable=[];
    private readonly HashSet<string> _textSaving=[];
    private bool _textClosePending,_textCloseReady,_textGuardPending,_diskWriteGuardPending;
    private static readonly JsonSerializerOptions TextJson=new(){PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private bool CanSaveText => !_diskWriteGuardPending && _ready && _model.ActiveSourceTabId is {} id && _textEditable.Contains(id) && !_textSaving.Contains(id) && !_model.IsWriting;
    private async Task<T?> ReadTextQueryAsync<T>(string expression)
    {
        if(_webView is null || !_ready) throw new IOException("编辑器尚未就绪，草稿未被关闭。");
        var json=WebViewScript.DecodeObject(await _webView.InvokeScript(WebViewScript.Json(expression)),16*1024*1024);
        return json is null ? throw new IOException("无法读取编辑器草稿，操作已取消。") : JsonSerializer.Deserialize<T>(json,TextJson);
    }
    private async Task<bool> SaveTextAsync(string? id=null)
    {
        id??=_model.ActiveSourceTabId;var root=_model.Root;var epoch=_model.RepositoryGeneration;
        if(_diskWriteGuardPending || id is null || root is null || _closed || _textSaving.Contains(id) || _model.IsWriting) return false;
        if(!_model.SourceTabs.Any(t=>t.Id==id&&t.LocalFile))return false;
        _textSaving.Add(id);RefreshSaveMenuState();TextDraft? draft=null;
        try
        {
            var state=await ReadTextQueryAsync<TextSessions>("window.gitx.fileSessions()");
            if(state is null || state.Composing) throw new IOException("请先完成输入法组合，再保存文件。");
            draft=await ReadTextQueryAsync<TextDraft>("window.gitx.fileDraft("+JsonSerializer.Serialize(id)+")");
            if(draft is null || draft.Id!=id || draft.RootKey!=WorkingTextFiles.RootKey(root) || root!=_model.Root || epoch!=_model.RepositoryGeneration ||
                !_model.SourceTabs.Any(t=>t.Id==id&&t.LocalFile&&t.Path==draft.Path))throw new IOException("文件或仓库已切换，未写入磁盘。");
            if(!draft.Dirty)return true;
            await _webView!.InvokeCommandAsync("window.gitx.fileSaving("+JsonSerializer.Serialize(new{draft.Id,draft.Model},TextJson)+")");
            var info=await Task.Run(()=>_textWriter.SaveAsync(root,draft.Path,draft.Expected,draft.Text));
            if(_closed || _webView is null || root!=_model.Root || epoch!=_model.RepositoryGeneration)return false;
            await _webView.InvokeCommandAsync("window.gitx.fileSaved("+JsonSerializer.Serialize(new{draft.Id,draft.Model,draft.Checkpoint,info},TextJson)+")");
            await CheckpointDraftsAsync(forClose: true);
            await _model.RefreshAsync();return true;
        }
        catch(Exception error)
        {
            if(!_closed && root==_model.Root && epoch==_model.RepositoryGeneration)
            {
                if(draft is not null)await InvokeEditorAsync("window.gitx.fileSaved("+JsonSerializer.Serialize(new{draft.Id,draft.Model,error=error.Message},TextJson)+")");
                _model.ReportError("文件保存失败，草稿保留："+error.Message);
            }
            return false;
        }
        finally{_textSaving.Remove(id);RefreshSaveMenuState();}
    }
    private sealed record DiskWriteLockResult(bool? Locked);
    // Freeze editing before reading dirty state, including tabs opened while a
    // write waits for refresh. The lease lasts through write reconciliation.
    private async Task<SourceTextCloseLease> GuardDiskWriteAsync(string root, IEnumerable<string> paths)
    {
        if (_diskWriteGuardPending || _textGuardPending || _textSaving.Count > 0 || !_ready || _webView is null) return new(false);
        _diskWriteGuardPending = true;
        var epoch = _model.RepositoryGeneration;
        var locked = false;
        async Task Release()
        {
            try { if (locked && !_closed) await InvokeEditorAsync("window.gitx.lockDiskWrites(false)"); }
            finally { _diskWriteGuardPending = false; RefreshSaveMenuState(); }
        }
        try
        {
            locked = true;
            var acknowledgement = await ReadTextQueryAsync<DiskWriteLockResult>("({locked:window.gitx.lockDiskWrites(true)})");
            if (acknowledgement?.Locked != true) throw new IOException("无法确认编辑器已锁定，未修改磁盘文件。");
            var state = await ReadTextQueryAsync<TextSessions>("window.gitx.fileSessions()");
            var affected = new HashSet<string>(paths, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (state is null || state.Composing || !DraftWriteGuard.CanWrite(root, affected, state.Sessions.Select(s => new DraftWriteSession(s.RootKey, s.Path, s.Dirty, s.Busy))))
                throw new IOException("待修改文件有未保存或正在保存的编辑，或输入法组合尚未结束；请先处理草稿。");
            if (_closed || root != _model.Root || epoch != _model.RepositoryGeneration) throw new IOException("仓库已切换，操作已取消。");
            return new(true, Release);
        }
        catch (Exception error) { await Release(); if (!_closed) _model.ReportError(error.Message); return new(false); }
    }
    private async Task<SourceTextCloseLease> ConfirmTextCloseAsync(SourceTextCloseRequest request)
    {
        if(_textGuardPending || _diskWriteGuardPending)return new(false);
        if(request.Ids.Count==0)return new(true);
        if(_textSaving.Count>0){_model.ReportError("文件正在保存，请完成后再关闭或切换仓库。");return new(false);}
        var ids=request.Ids.ToArray();var root=_model.Root;var epoch=_model.RepositoryGeneration;
        _textGuardPending=true;var locked=false;
        async Task Release(){_textGuardPending=false;if(locked&&!_closed){locked=false;await InvokeEditorAsync("window.gitx.lockFiles("+JsonSerializer.Serialize(ids)+",false)");}}
        try
        {
            if(!_ready || _webView is null){_textGuardPending=false;return new(!_textEditable.Overlaps(ids));}
            var state=await ReadTextQueryAsync<TextSessions>("window.gitx.fileSessions()");
            if(state is null || state.Composing)throw new IOException("请先完成输入法组合；草稿未关闭。");
            await _webView.InvokeCommandAsync("window.gitx.lockFiles("+JsonSerializer.Serialize(ids)+",true)");locked=true;
            state=await ReadTextQueryAsync<TextSessions>("window.gitx.fileSessions()");
            if(state is null || state.Composing || state.Sessions.Any(s=>ids.Contains(s.Id)&&s.Busy))throw new IOException("文件仍在输入或保存，操作已取消。");
            var dirty=state.Sessions.Where(s=>ids.Contains(s.Id)&&s.Dirty).ToArray();
            if(dirty.Length>0)
            {
                var choice=await SourceTextCloseDialog.ShowAsync(InteractionWindow,dirty.Select(s=>s.Path).ToArray(), _draftEnabled);
                if(choice==SourceTextCloseChoice.Cancel){await Release();return new(false);}
                if(choice==SourceTextCloseChoice.Keep)
                {
                    await CheckpointDraftsAsync(forClose: true);
                    foreach(var session in dirty)
                    {
                        var captured = await ReadTextQueryAsync<TextDraft>("window.gitx.fileDraft(" + JsonSerializer.Serialize(session.Id) + ")");
                        if(captured is null || !_draftOwned.TryGetValue(captured.RootKey + "/" + captured.Path, out var persisted) || persisted.Text != captured.Text || persisted.Baseline != captured.Baseline || root is null || !await Task.Run(() => _draftStore.Contains(root, persisted)))
                            throw new IOException("部分草稿未持久化（可能被排除、超限或写入失败），未关闭。");
                    }
                }
                if(choice==SourceTextCloseChoice.Discard)
                {
                    await _draftGate.WaitAsync();
                    try
                    {
                        foreach(var session in dirty)
                        {
                            var key = session.RootKey + "/" + session.Path;
                            if(root is not null && _draftOwned.TryGetValue(key, out var persisted))
                            { await Task.Run(() => _draftStore.Discard(root, persisted)); _draftOwned.Remove(key); }
                        }
                    }
                    finally { _draftGate.Release(); }
                }
                if(choice==SourceTextCloseChoice.Save)
                    foreach(var session in dirty)if(!await SaveTextAsync(session.Id)){await Release();return new(false);}
                if(choice==SourceTextCloseChoice.Save)
                {
                    var after=await ReadTextQueryAsync<TextSessions>("window.gitx.fileSessions()");
                    if(after is null || after.Sessions.Any(s=>ids.Contains(s.Id)&&s.Dirty))throw new IOException("仍有未保存的输入，草稿未关闭。");
                }
            }
            if(_closed || root!=_model.Root || epoch!=_model.RepositoryGeneration){await Release();return new(false);}
            return new(true,Release);
        }
        catch(Exception error){await Release();if(!_closed)_model.ReportError("未关闭文本草稿："+error.Message);return new(false);}
    }
    private async Task ConfirmTextWindowCloseAsync()
    {
        if(_textClosePending)return;_textClosePending=true;
        try
        {
            await using var lease=await ConfirmTextCloseAsync(new("window",_model.SourceTabs.Select(t=>t.Id).ToArray()));
            if(!lease.Allowed || _closed){_dockCloseReady=false;return;}_textCloseReady=true;try{Close();}finally{_textCloseReady=false;}
        }
        finally{_textClosePending=false;}
    }
}
