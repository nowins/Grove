using System.Text;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class WorkingTextFileTests
{
    private static async Task<RepositoryDocument> Read(TestRepository repo, string path = "text.txt") => await new RepositoryReader().DocumentAsync(new(repo.Root, path, ComparisonKind.WorkingFile));
    [Theory]
    [InlineData(false, "\n", false)] [InlineData(true, "\n", true)]
    [InlineData(false, "\r\n", true)] [InlineData(true, "\r\n", false)]
    public async Task Save_preserves_BOM_uniform_newline_and_exact_EOF(bool bom, string eol, bool final)
    {
        using var repo = new TestRepository(); repo.Write("text.txt", "base\n"); repo.Commit("base");
        var full = Path.Combine(repo.Root, "text.txt"); var utf8 = new UTF8Encoding(false, true);
        File.WriteAllBytes(full, utf8.GetBytes((bom ? "\uFEFF" : "") + "中文" + eol + "second" + (final ? eol : "")));
        var before = await Read(repo); Assert.True(before.Editing!.CanEdit); Assert.Equal(bom, before.Editing.Bom);
        var expected = utf8.GetBytes((bom ? "\uFEFF" : "") + "edited" + eol + "第二行" + (final ? eol : ""));
        var info = await new WorkingTextWriter().SaveAsync(repo.Root,"text.txt",before.Editing.Fingerprint,"edited\n第二行"+(final?"\n":""));
        Assert.Equal(expected, File.ReadAllBytes(full)); Assert.NotEqual(before.Editing.Fingerprint, info.Fingerprint);
        Assert.Equal("", repo.Git("diff", "--cached", "--raw")); Assert.Empty(Directory.GetDirectories(repo.Root,".grove-save-*"));
    }
    [Theory]
    [InlineData("a\r\nb\nc")] [InlineData("a\rb")]
    public async Task Mixed_or_CR_newlines_are_readonly(string value)
    { using var repo=new TestRepository(); repo.Write("text.txt",value); repo.Commit("base"); var doc=await Read(repo); Assert.False(doc.Editing!.CanEdit); await Assert.ThrowsAnyAsync<IOException>(()=>new WorkingTextWriter().SaveAsync(repo.Root,"text.txt",doc.Editing.Fingerprint,"new")); }
    [Fact]
    public async Task Invalid_encoding_binary_and_history_are_not_editable()
    {
        using var repo=new TestRepository(); repo.Write("text.txt","base"); repo.Commit("base"); var full=Path.Combine(repo.Root,"text.txt");
        foreach(var bytes in new[]{new byte[]{0xff,0xfe,0x61,0},new byte[]{0xc3,0x28},new byte[]{0x61,0,0x62}})
        { File.WriteAllBytes(full,bytes); var invalid=await Read(repo);Assert.True(invalid.IsNotice);Assert.Null(invalid.Editing);Assert.Null(invalid.CommentContext); }
        var sha=repo.Git("rev-parse","HEAD").Trim(); Assert.Null((await new RepositoryReader().DocumentAsync(new(repo.Root,"text.txt",ComparisonKind.Commit,sha))).Editing);
    }
    [Fact]
    public async Task Raw_bytes_detect_same_size_external_edit_even_when_timestamp_restored()
    {
        using var repo=new TestRepository(); repo.Write("text.txt","one\n"); repo.Commit("base"); var full=Path.Combine(repo.Root,"text.txt"); var doc=await Read(repo); var stamp=File.GetLastWriteTimeUtc(full);
        File.WriteAllText(full,"two\n",new UTF8Encoding(false)); File.SetLastWriteTimeUtc(full,stamp);
        await Assert.ThrowsAnyAsync<IOException>(()=>new WorkingTextWriter().SaveAsync(repo.Root,"text.txt",doc.Editing!.Fingerprint,"mine\n")); Assert.Equal("two\n",File.ReadAllText(full));
        File.Delete(full); await Assert.ThrowsAnyAsync<IOException>(()=>new WorkingTextWriter().SaveAsync(repo.Root,"text.txt",doc.Editing!.Fingerprint,"mine")); Assert.False(File.Exists(full));
    }
    [Fact]
    public async Task Baseline_is_bound_to_working_root_and_head_and_permissions()
    {
        using var a=new TestRepository(); using var b=new TestRepository(); foreach(var r in new[]{a,b}){r.Write("text.txt","same");r.Commit("base");}
        var doc=await Read(a); await Assert.ThrowsAnyAsync<IOException>(()=>new WorkingTextWriter().SaveAsync(b.Root,"text.txt",doc.Editing!.Fingerprint,"mine"));
        a.Git("checkout","-b","next"); await Assert.ThrowsAnyAsync<IOException>(()=>new WorkingTextWriter().SaveAsync(a.Root,"text.txt",doc.Editing!.Fingerprint,"mine"));
        var full=Path.Combine(a.Root,"text.txt"); var attr=File.GetAttributes(full); try{File.SetAttributes(full,attr|FileAttributes.ReadOnly);Assert.False((await Read(a)).Editing!.CanEdit);}finally{File.SetAttributes(full,attr);}
    }
    [Fact]
    public async Task Final_check_rejects_racing_writer_without_losing_draft_or_foreign_bytes()
    {
        using var repo=new TestRepository();repo.Write("text.txt","old");repo.Commit("base");var doc=await Read(repo);var full=Path.Combine(repo.Root,"text.txt");
        var writer=new WorkingTextWriter(()=>{File.WriteAllText(full,"foreign",new UTF8Encoding(false));return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<IOException>(()=>writer.SaveAsync(repo.Root,"text.txt",doc.Editing!.Fingerprint,"draft")); Assert.Equal("foreign",File.ReadAllText(full));
        var recovery=Path.Combine(repo.Root,".git","grove-text-recovery"); Assert.Contains(Directory.GetFiles(recovery,"pending",SearchOption.AllDirectories),p=>File.ReadAllText(p)=="draft");
    }
    [Fact]
    public async Task Failure_after_atomic_install_rolls_back_and_preserves_draft()
    {
        using var repo=new TestRepository();repo.Write("text.txt","old");repo.Commit("base");var doc=await Read(repo);
        var writer=new WorkingTextWriter(null,()=>throw new IOException("injected")); await Assert.ThrowsAnyAsync<IOException>(()=>writer.SaveAsync(repo.Root,"text.txt",doc.Editing!.Fingerprint,"draft"));
        Assert.Equal("old",File.ReadAllText(Path.Combine(repo.Root,"text.txt"))); Assert.Contains(Directory.GetFiles(Path.Combine(repo.Root,".git","grove-text-recovery"),"raced",SearchOption.AllDirectories),p=>File.ReadAllText(p)=="draft");
    }
    [Fact]
    public async Task External_write_after_install_is_preserved_and_no_success_is_reported()
    {
        using var repo=new TestRepository();repo.Write("text.txt","old");repo.Commit("base");var doc=await Read(repo);var full=Path.Combine(repo.Root,"text.txt");
        var writer=new WorkingTextWriter(null,()=>{File.WriteAllText(full,"latest",new UTF8Encoding(false));return Task.CompletedTask;});await Assert.ThrowsAnyAsync<IOException>(()=>writer.SaveAsync(repo.Root,"text.txt",doc.Editing!.Fingerprint,"draft"));
        Assert.Equal("latest",File.ReadAllText(full)); Assert.Contains(Directory.GetFiles(Path.Combine(repo.Root,".git","grove-text-recovery"),"original",SearchOption.AllDirectories),p=>File.ReadAllText(p)=="old");
    }
    [Fact]
    public async Task Noop_is_byte_and_timestamp_preserving_and_unrepresentable_drafts_are_rejected()
    {
        using var repo=new TestRepository();repo.Write("text.txt","old\n");repo.Commit("base");var doc=await Read(repo);var full=Path.Combine(repo.Root,"text.txt");var stamp=File.GetLastWriteTimeUtc(full);var w=new WorkingTextWriter();
        Assert.Equal(doc.Editing,await w.SaveAsync(repo.Root,"text.txt",doc.Editing!.Fingerprint,"old\n"));Assert.Equal(stamp,File.GetLastWriteTimeUtc(full));
        foreach(var value in new[]{"bad\0", "bad\r", new string('\ud800',3),new string('x',20001)})await Assert.ThrowsAnyAsync<IOException>(()=>w.SaveAsync(repo.Root,"text.txt",doc.Editing.Fingerprint,value));Assert.Equal("old\n",File.ReadAllText(full));
    }
}
