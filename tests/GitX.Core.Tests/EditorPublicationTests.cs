using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using LibGit2Sharp;
using Xunit;
namespace GitX.Core.Tests;
public sealed class EditorPublicationTests
{
    [Theory]
    [InlineData(ComparisonKind.WorkingFile)] [InlineData(ComparisonKind.Worktree)] [InlineData(ComparisonKind.Commit)]
    public void Existing_comment_snapshot_matches_the_exact_legacy_JSON_bytes(ComparisonKind kind)
    {
        using var fixture=new TestRepository();fixture.Write("a.txt","base");fixture.Commit("base");using var repo=new Repository(fixture.Root);
        foreach(var text in new[]{"", "中文 <>&\\\"\r\n😀", new string('中',32767),new string('中',32768)})
        {
            var request=new DocumentRequest(fixture.Root,"目录/a.txt",kind);var oldSide=kind==ComparisonKind.WorkingFile?null:new DiffSide("目录/a.txt","before",text,true);var newSide=new DiffSide("目录/a.txt","after",text,true);var revision=repo.Head.Tip.Sha;
            var legacy=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{request.Kind,request.Path,oldSide,newSide,revision}));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(legacy)).ToLowerInvariant(),CommentContext.Create(repo,request,revision,oldSide,newSide).Snapshot);
        }
    }
}
