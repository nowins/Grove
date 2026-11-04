using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop;
using GitX.Desktop.ViewModels;

internal static partial class Program
{
    private static async Task<JsonElement> TextState(NativeWebView web, string id)
    {
        var code="window.groveNative.json(() => ({...window.gitx.fileSessions(),draft:window.gitx.fileDraft("+JsonSerializer.Serialize(id)+")}))";
        var raw=await web.InvokeScript(code);using var outer=JsonDocument.Parse(raw!);
        using var value=JsonDocument.Parse(outer.RootElement.ValueKind==JsonValueKind.String?outer.RootElement.GetString()!:raw!);
        return value.RootElement.Clone();
    }
    private static bool TextDirty(JsonElement state,string id) => state.GetProperty("sessions").EnumerateArray().Any(s=>s.GetProperty("id").GetString()==id&&s.GetProperty("dirty").GetBoolean());
    private static async Task ChooseTextClose(string label)
    {
        var lifetime=(IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        await UntilAsync(()=>lifetime.Windows.Any(w=>w.Title=="未保存的文件"));
        var dialog=lifetime.Windows.Single(w=>w.Title=="未保存的文件");
        var button=dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string==label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task VerifyNativeTextEditingAsync(MainWindow main,MainViewModel model,nint window)
    {
        var web=Private<NativeWebView>(main,"_webView");
        // Navigation deliberately finishes in the linked worktree. Establish
        // this test's owner before reading or observing the main fixture file.
        var expectedRoot=await RepositoryReader.ResolveWorkingDirectoryAsync(_fixtureRepository);
        await model.OpenAsync(_fixtureRepository);await UntilAsync(()=>!model.Busy&&model.Root==expectedRoot);
        Check(model.Root==expectedRoot,"Editing probe owns the wrong working directory.");
        var path=Path.Combine(expectedRoot,"sample.cs");
        var bytes=File.ReadAllBytes(path);var before=FixtureState(_fixtureRepository);var linkedBefore=FixtureState(_linkedRepository);
        try
        {
            await model.OpenEditorEntryAsync(new("sample.cs","sample.cs",ComparisonKind.WorkingFile));await NavigationReady(main,model);
            var id=model.ActiveSourceTabId!;
            await UntilAsync(async()=> (await TextState(web,id)).GetProperty("sessions").EnumerateArray().Any(s=>s.GetProperty("id").GetString()==id&&s.GetProperty("editable").GetBoolean()));
            await FocusSource(main,web,window);Check(TestKey(window,"x",7,0)==0,"Cannot queue native source input.");
            await UntilAsync(async()=>TextDirty(await TextState(web,id),id));
            var capture=(await TextState(web,id)).GetProperty("draft");
            Check(capture.GetProperty("rootKey").GetString()==WorkingTextFiles.RootKey(expectedRoot),"Draft ownership does not match the file being observed.");
            var draft=capture.GetProperty("text").GetString()!;
            Check(TestKey(window,"s",1,1)==0,"Cannot queue native Command+S.");
            try { await UntilAsync(()=>File.ReadAllText(path)==draft); }
            catch
            {
                Samples.Add(new{phase="native-text-save-failure",model.Error,model.Root,model.DocumentVersion,
                    hostLoaded=Private<long>(main,"_loadedEditorVersion"),sessions=await TextState(web,id),nativeMenu=ReadMenus(),
                    expectedCharacters=draft.Length,diskCharacters=File.ReadAllText(path).Length,
                    editable=Private<HashSet<string>>(main,"_textEditable").Contains(id),saving=Private<HashSet<string>>(main,"_textSaving").Contains(id),focus=ReadFocus(window)});
                throw;
            }
            await UntilAsync(async()=>!TextDirty(await TextState(web,id),id));await NavigationReady(main,model);
            Samples.Add(new{phase="native-text-save",actualCocoaInput=true,commandS=true,utf8BytesMatched=File.ReadAllBytes(path).SequenceEqual(new UTF8Encoding(false,true).GetBytes(draft)),physicalInputTested=false});
            await FocusSource(main,web,window);Check(TestKey(window,"y",16,0)==0,"Cannot queue second native source input.");
            await UntilAsync(async()=>TextDirty(await TextState(web,id),id));
            var unsaved=(await TextState(web,id)).GetProperty("draft").GetProperty("text").GetString()!;
            await model.OpenEditorEntryAsync(new("other.ts","other.ts",ComparisonKind.WorkingFile));await NavigationReady(main,model);
            var other=model.ActiveSourceTabId!;
            await model.SelectSourceTabAsync(id,model.DocumentVersion);await NavigationReady(main,model);
            Check((await TextState(web,id)).GetProperty("draft").GetProperty("text").GetString()==unsaved,"Switching tabs replaced the draft.");
            await model.RefreshAsync();await NavigationReady(main,model);
            Check((await TextState(web,id)).GetProperty("draft").GetProperty("text").GetString()==unsaved,"Refresh replaced the draft.");
            // This part deliberately tests DOM context events inside real WKWebView;
            // it is not a physical trackpad/Control-click acceptance claim.
            await NavigationJS(web,"(()=>{const t=[...document.querySelectorAll('.source-tab')].find(t=>t.dataset.id==="+JsonSerializer.Serialize(other)+");t.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,button:2,ctrlKey:true}));})()");
            var menuRaw=await web.InvokeScript("window.groveNative.json(()=>({open:!document.querySelector('.source-tab-menu').hidden,count:document.querySelectorAll('.source-tab-menu button').length}))");
            using(var outer=JsonDocument.Parse(menuRaw!))
            using(var menu=JsonDocument.Parse(outer.RootElement.ValueKind==JsonValueKind.String?outer.RootElement.GetString()!:menuRaw!))
                Check(menu.RootElement.GetProperty("open").GetBoolean()&&menu.RootElement.GetProperty("count").GetInt32()==5&&model.ActiveSourceTabId==id,"Background context menu switched files or did not expose all five actions.");
            await NavigationJS(web,"document.querySelector('.source-tab-menu button').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
            var close=model.CloseSourceTabAsync(id,model.DocumentVersion);await ChooseTextClose("取消");await close;
            Check(model.SourceTabs.Any(t=>t.Id==id)&&TextDirty(await TextState(web,id),id),"Cancel closed an unsaved tab.");
            var external=draft+"\n// external change\n";File.WriteAllText(path,external,new UTF8Encoding(false));
            await FocusSource(main,web,window);Check(TestKey(window,"s",1,1)==0,"Cannot queue conflicting save.");
            await UntilAsync(()=>model.Error?.Contains("文件保存失败",StringComparison.Ordinal)==true);
            Check(File.ReadAllText(path)==external&&TextDirty(await TextState(web,id),id),"Conflicting save overwrote disk or lost the draft.");
            var closeAll=model.CloseSourceTabsAsync(id,model.DocumentVersion,SourceTabCloseScope.All);await ChooseTextClose("放弃这些草稿");await closeAll;
            Check(model.SourceTabs.Count==0&&File.ReadAllText(path)==external,"Discard changed disk or failed to close tabs.");
            Samples.Add(new{phase="native-text-protection",draftSurvivedSwitchAndRefresh=true,backgroundTabContextDom=true,closeCancel=true,externalChangeRejected=true,discardPreservedDisk=true,physicalInputTested=false});
        }
        finally
        {
            // Restore only this probe's generated file. No user repository is used.
            File.WriteAllBytes(path,bytes);await model.RefreshAsync();
        }
        Check(FixtureState(_fixtureRepository)==before&&FixtureState(_linkedRepository)==linkedBefore,"Text probe changed generated Git state.");
    }
}
