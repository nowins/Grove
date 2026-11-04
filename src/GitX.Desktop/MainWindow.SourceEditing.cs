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
    private sealed record TextDraft(string Id,string Path,string RootKey,string Model,int Checkpoint,string Expected,string Text,bool Dirty);
    private readonly WorkingTextWriter _textWriter=new();
    private readonly HashSet<string> _textEditable=[];
    private readonly HashSet<string> _textSaving=[];
    private bool _textClosePending,_textCloseReady,_textGuardPending;
    private static readonly JsonSerializerOptions TextJson=new(){PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private bool CanSaveText => _ready && _model.ActiveSourceTabId is {} id && _textEditable.Contains(id) && !_textSaving.Contains(id) && !_model.IsWriting;
    private async Task<T?> ReadTextQueryAsync<T>(string expression)
    {
        if(_webView is null || !_ready) throw new IOException("编辑器尚未就绪，草稿未被关闭。");
        var json=WebViewScript.DecodeObject(await _webView.InvokeScript(WebViewScript.Json(expression)),16*1024*1024);
        return json is null ? throw new IOException("无法读取编辑器草稿，操作已取消。") : JsonSerializer.Deserialize<T>(json,TextJson);
    }
    private async Task<bool> SaveTextAsync(string? id=null)
    {
        id??=_model.ActiveSourceTabId;var root=_model.Root;var epoch=_model.RepositoryGeneration;
        if(id is null || root is null || _closed || _textSaving.Contains(id) || _model.IsWriting) return false;
        if(!_model.SourceTabs.Any(t=>t.Id==id&&t.LocalFile))return false;
        _textSaving.Add(id);TextDraft? draft=null;
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
        finally{_textSaving.Remove(id);}
    }
    private async Task<SourceTextCloseLease> ConfirmTextCloseAsync(SourceTextCloseRequest request)
    {
        if(_textGuardPending)return new(false);
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
                var choice=await SourceTextCloseDialog.ShowAsync(InteractionWindow,dirty.Select(s=>s.Path).ToArray());
                if(choice==SourceTextCloseChoice.Cancel){await Release();return new(false);}
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
