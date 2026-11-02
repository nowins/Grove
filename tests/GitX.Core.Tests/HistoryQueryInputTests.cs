using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryQueryInputTests
{
    [Fact]
    public void Inclusive_dates_accept_same_day_and_normalize_other_fields()
    {
        var query = HistoryQueryInput.Create(" fix ", " author ", " 2024-02-29 ", "2024-02-29", " src/file.cs ");
        Assert.Equal(new DateTimeOffset(new DateTime(2024, 2, 29)), query.From);
        Assert.Equal(new DateTimeOffset(new DateTime(2024, 3, 1)), query.Until);
        Assert.Equal("fix", query.Text); Assert.Equal("author", query.Author); Assert.Equal("src/file.cs", query.Path);
        Assert.True(HistoryQueryInput.Create("", "", " ", "", "").IsEmpty);
    }

    [Theory]
    [InlineData("2026-99-01", "", "开始日期无效")]
    [InlineData("", "2025-02-29", "结束日期无效")]
    [InlineData("2026-10-29", "2026-10-28", "结束日期不能早于开始日期")]
    [InlineData("", "9999-12-31", "结束日期超出支持范围")]
    public void Invalid_drafts_return_actionable_errors(string from, string until, string message)
    {
        Assert.StartsWith(message, Assert.Throws<IOException>(() => HistoryQueryInput.Create("", "", from, until, "")).Message);
    }

    [Fact]
    public async Task Invalid_query_preserves_results_and_correcting_to_active_query_clears_error()
    {
        using var repo = new TestRepository(); repo.Write("file.cs", "content\n"); repo.Commit("base");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(2);
        model.HistoryText = "base"; await model.ApplyHistoryQueryAsync();
        var commits = model.Commits; var summary = model.HistoryFilterSummary;
        model.HistoryFrom = "2026-99-01"; await model.ApplyHistoryQueryAsync();
        Assert.StartsWith("开始日期无效", model.Error); Assert.Same(commits, model.Commits); Assert.Equal(summary, model.HistoryFilterSummary);
        model.HistoryFrom = ""; await model.ApplyHistoryQueryAsync();
        Assert.Equal("", model.Error); Assert.Same(commits, model.Commits); Assert.Equal(summary, model.HistoryFilterSummary);
    }
}
