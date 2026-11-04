using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;
namespace GitX.Core.Tests;
public sealed class SourceTextCloseTests
{
    [Fact]
    public async Task Cancelled_batch_close_and_repository_switch_leave_tabs_and_document_intact()
    {
        using var repo=new TestRepository();repo.Write("a.txt","a");repo.Write("b.txt","b");repo.Commit("base");using var other=new TestRepository();other.Write("a.txt","other");other.Commit("base");
        using var model=new MainViewModel();await model.OpenAsync(repo.Root);await model.OpenEditorEntryAsync(new("a.txt","a.txt",ComparisonKind.WorkingFile));var a=model.ActiveSourceTabId!;await model.OpenEditorEntryAsync(new("b.txt","b.txt",ComparisonKind.WorkingFile));var b=model.ActiveSourceTabId!;var document=model.CurrentDocument;var version=model.DocumentVersion;var root=model.Root;
        var requests=new List<SourceTextCloseRequest>();model.ConfirmSourceTextClose=request=>{requests.Add(request);return Task.FromResult(new SourceTextCloseLease(false));};
        await model.CloseSourceTabsAsync(a,version,SourceTabCloseScope.Others);Assert.Equal(new[]{b},requests[^1].Ids);Assert.Equal(2,model.SourceTabs.Count);Assert.Same(document,model.CurrentDocument);Assert.Equal(version,model.DocumentVersion);
        await model.OpenAsync(other.Root);Assert.Equal("repository",requests[^1].Reason);Assert.Equal(new[]{a,b},requests[^1].Ids);Assert.Equal(root,model.Root);Assert.Same(document,model.CurrentDocument);Assert.Equal(b,model.ActiveSourceTabId);
    }
    [Fact]
    public async Task Changed_tab_set_during_confirmation_cancels_close_and_releases_the_freeze()
    {
        using var repo=new TestRepository();repo.Write("a.txt","a");repo.Write("b.txt","b");repo.Commit("base");using var model=new MainViewModel();await model.OpenAsync(repo.Root);await model.OpenEditorEntryAsync(new("a.txt","a.txt",ComparisonKind.WorkingFile));var a=model.ActiveSourceTabId!;var released=0;
        model.ConfirmSourceTextClose=async _=>{await model.OpenEditorEntryAsync(new("b.txt","b.txt",ComparisonKind.WorkingFile));return new(true,()=>{released++;return Task.CompletedTask;});};
        await model.CloseSourceTabsAsync(a,model.DocumentVersion,SourceTabCloseScope.All);Assert.Equal(2,model.SourceTabs.Count);Assert.Equal("b.txt",model.CurrentDocument!.Path);Assert.Equal(1,released);
    }
    [Fact]
    public async Task Confirmation_can_publish_a_saved_document_before_closing_without_losing_scope()
    {
        using var repo=new TestRepository();repo.Write("a.txt","a");repo.Write("b.txt","b");repo.Commit("base");using var model=new MainViewModel();await model.OpenAsync(repo.Root);await model.OpenEditorEntryAsync(new("a.txt","a.txt",ComparisonKind.WorkingFile));var a=model.ActiveSourceTabId!;await model.OpenEditorEntryAsync(new("b.txt","b.txt",ComparisonKind.WorkingFile));var released=0;
        model.ConfirmSourceTextClose=async _=>{repo.Write("b.txt","saved");await model.RefreshAsync();return new(true,()=>{released++;return Task.CompletedTask;});};
        await model.CloseSourceTabsAsync(a,model.DocumentVersion,SourceTabCloseScope.Others);Assert.Equal(a,Assert.Single(model.SourceTabs).Id);Assert.Equal("a",model.CurrentDocument!.Text);Assert.Equal(1,released);Assert.Equal("saved",File.ReadAllText(Path.Combine(repo.Root,"b.txt")));
    }
}
