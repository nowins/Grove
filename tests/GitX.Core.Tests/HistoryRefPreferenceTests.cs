using GitX.Core;
using GitX.Desktop.Services;
using Xunit;
namespace GitX.Core.Tests;
public sealed class HistoryRefPreferenceTests
{
    [Fact] public async Task Canonical_references_persist_independently_per_worktree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grove-ref-pref-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(dir,"main"); var b = Path.Combine(dir,"linked"); var store = new HistoryReferencePreferenceStore(dir);
            await store.RememberAsync(a,"refs/heads/main"); await store.RememberAsync(a,"refs/remotes/origin/main"); await store.RememberAsync(a,"refs/heads/main");
            await store.ToggleFavoriteAsync(a,"refs/tags/main"); await store.ToggleFavoriteAsync(b,"refs/heads/main");
            var restored = await new HistoryReferencePreferenceStore(dir).ReadAsync(a + Path.DirectorySeparatorChar);
            Assert.Equal(new[] { "refs/heads/main","refs/remotes/origin/main" },restored.Recent); Assert.Equal(new[] { "refs/tags/main" },restored.Favorites);
            Assert.Equal(new[] { "refs/heads/main" },(await store.ReadAsync(b)).Favorites); Assert.Empty((await store.ReadAsync(b)).Recent);
            await store.ToggleFavoriteAsync(a,"refs/tags/main"); Assert.Empty((await store.ReadAsync(a)).Favorites);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir,true); }
    }
    [Fact] public void Same_names_remain_distinct_and_deleted_references_are_not_offered()
    {
        var refs = new[] { new HistoryReference("main","refs/heads/main","a","local"),new HistoryReference("main","refs/remotes/origin/main","b","remote"),new HistoryReference("main","refs/tags/main","c","tag") };
        var choices = new HistoryReferenceChoices(refs); var prefs = new HistoryReferencePreferences(["refs/heads/gone","refs/remotes/origin/main"],["refs/tags/main"]);
        var rows = choices.Build(prefs,"refs/remotes/origin/main","");
        Assert.Equal(new[] { "收藏","最近使用","本地分支","远端分支","标签" },rows.Where(x=>x.IsHeading).Select(x=>x.Name));
        Assert.DoesNotContain(rows,x=>x.Key=="refs/heads/gone"); Assert.All(rows.Where(x=>x.Selected),x=>Assert.Equal("refs/remotes/origin/main",x.Key));
        Assert.Equal(3,rows.Where(x=>x.Reference is not null).Select(x=>x.Key).Distinct().Count());
        Assert.Equal(2,choices.Build(prefs,null,"origin main").Count(x=>x.Selectable)); // recent + remote occurrences, same exact identity
    }
    [Fact] public void Search_and_large_catalog_do_not_resolve_or_mutate_Git()
    {
        var refs = Enumerable.Range(0,20000).Select(i=>new HistoryReference("topic/"+i,"refs/heads/topic/"+i,"a","local"));
        var catalog = new HistoryReferenceChoices(refs); var rows=catalog.Build(HistoryReferencePreferences.Empty,null,"topic/19999");
        Assert.Equal("refs/heads/topic/19999",Assert.Single(rows,x=>x.Selectable).Key);
        var all=catalog.Build(HistoryReferencePreferences.Empty,null,""); Assert.Equal(20002,all.Count(x=>x.Selectable)); Assert.True(all[0].Selected);
    }
    [Fact] public async Task Concurrent_mru_is_bounded_without_duplicate_identities()
    {
        var dir=Path.Combine(Path.GetTempPath(),"grove-ref-pref-"+Guid.NewGuid().ToString("N"));
        try { var store=new HistoryReferencePreferenceStore(dir); await Task.WhenAll(Enumerable.Range(0,24).Select(i=>store.RememberAsync(dir,"refs/heads/"+i))); var p=await store.ReadAsync(dir);Assert.Equal(16,p.Recent.Count);Assert.Equal(16,p.Recent.Distinct().Count()); }
        finally { if(Directory.Exists(dir))Directory.Delete(dir,true); }
    }
    [Fact] public async Task Future_or_oversized_preferences_are_preserved_and_corrupt_json_can_be_recovered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grove-ref-pref-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(dir, "repo"); var store = new HistoryReferencePreferenceStore(dir);
            await store.RememberAsync(root, "refs/heads/main");
            var path = Assert.Single(Directory.GetFiles(Path.Combine(dir, "history-ref-preferences")));
            var future = "{\"Version\":2,\"Recent\":[],\"Favorites\":[]}";
            await File.WriteAllTextAsync(path, future);
            await Assert.ThrowsAsync<IOException>(() => store.ToggleFavoriteAsync(root, "refs/tags/v1"));
            Assert.Equal(future, await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, new string(' ', 8 * 1024 * 1024 + 1));
            await Assert.ThrowsAsync<IOException>(() => store.RememberAsync(root, "refs/heads/main"));
            Assert.Equal(8 * 1024 * 1024 + 1, new FileInfo(path).Length);
            await File.WriteAllTextAsync(path, "{broken"); Assert.Empty((await store.ReadAsync(root)).Recent);
            await store.ToggleFavoriteAsync(root, "refs/heads/main");
            Assert.Equal(new[] { "refs/heads/main" }, (await store.ReadAsync(root)).Favorites);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact] public void All_and_detached_HEAD_keep_exact_selection_and_reference_kinds()
    {
        var refs = new[] { new HistoryReference("topic/中文", "refs/heads/topic/中文", "a", "local"), new HistoryReference("remote/topic/中文", "refs/remotes/remote/topic/中文", "b", "remote") };
        var catalog = new HistoryReferenceChoices(refs);
        var rows = catalog.Build(HistoryReferencePreferences.Empty, "HEAD", "");
        Assert.Equal("HEAD", Assert.Single(rows, r => r.Selected).Key);
        Assert.Contains(rows, r => r.Key == "all");
        Assert.Contains(rows, r => r.KindLabel == "本地"); Assert.Contains(rows, r => r.KindLabel == "远端");
        Assert.DoesNotContain(catalog.Build(HistoryReferencePreferences.Empty, null, "missing"), r => r.Selectable);
    }
}
