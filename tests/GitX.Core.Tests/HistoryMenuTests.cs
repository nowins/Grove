using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryMenuTests
{
    private static readonly HistoryMenuState State = new("root", new('a', 40), "main", false, false, false);
    private static ActionContext Context() => new(State.Root, "token", State.Head, State.Branch, "", false, false, [], []);

    [Fact]
    public async Task Opening_and_loading_branches_never_starts_full_worktree_validation()
    {
        var calls = 0;
        using var menu = new HistoryMenuSession(State, (_, _) => Task.FromResult<IReadOnlyList<BranchEntry>>([]),
            (_, _) => { calls++; return new TaskCompletionSource<ActionContext>().Task; });
        Assert.Equal(0, calls); Assert.Empty(await menu.ReadBranchesAsync()); Assert.Equal(0, calls);
        var action = menu.ReadContextAsync(); Assert.Equal(1, calls); Assert.False(action.IsCompleted);
        menu.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
    }

    [Fact]
    public async Task Closing_popup_cancels_late_metadata_but_keeps_the_chosen_action_alive()
    {
        var branches = new TaskCompletionSource<IReadOnlyList<BranchEntry>>();
        var context = new TaskCompletionSource<ActionContext>();
        using var menu = new HistoryMenuSession(State, (_, _) => branches.Task, (_, _) => context.Task);
        var metadata = menu.ReadBranchesAsync(); var action = menu.ReadContextAsync(); menu.ClosePopup();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => metadata); Assert.False(action.IsCompleted);
        context.SetResult(Context()); Assert.Equal(Context(), await action);
        branches.SetResult([]); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => menu.ReadBranchesAsync());
    }

    [Fact]
    public async Task Retiring_menu_cancels_ignored_late_action_results()
    {
        var late = new TaskCompletionSource<ActionContext>(); var token = CancellationToken.None;
        var menu = new HistoryMenuSession(State, context: (_, cancellation) => { token = cancellation; return late.Task; });
        var pending = menu.ReadContextAsync(); menu.Dispose(); menu.Dispose();
        Assert.True(token.IsCancellationRequested); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        late.SetResult(Context()); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => menu.ReadContextAsync());
    }

    [Theory]
    [InlineData("root")]
    [InlineData("head")]
    [InlineData("branch")]
    [InlineData("detached")]
    public async Task Action_rejects_changed_repository_or_revision(string change)
    {
        var context = change switch
        {
            "root" => Context() with { Root = "other" }, "head" => Context() with { Head = new('b', 40) },
            "branch" => Context() with { Branch = "other" }, _ => Context() with { Detached = true }
        };
        using var menu = new HistoryMenuSession(State, context: (_, _) => Task.FromResult(context));
        await Assert.ThrowsAsync<IOException>(() => menu.ReadContextAsync());
    }

    [Fact]
    public async Task Fresh_dirty_and_operation_state_is_not_replaced_by_cached_menu_state()
    {
        var dirty = Context() with { Dirty = true, InOperation = true };
        using var menu = new HistoryMenuSession(State, context: (_, _) => Task.FromResult(dirty));
        Assert.Equal(dirty, await menu.ReadContextAsync());
    }

    [Fact]
    public async Task Snapshot_state_uses_reference_identity_instead_of_localized_branch_label()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        using var reader = new RepositoryReader();
        var snapshot = await reader.OpenAsync(repo.Root);
        var state = HistoryMenuState.From(snapshot with { Branch = "localized label" });
        Assert.Equal("main", state.Branch); Assert.False(state.Detached); Assert.False(state.Dirty);
        repo.Git("checkout", "--detach", "HEAD"); repo.Write("a", "dirty");
        state = HistoryMenuState.From(await reader.OpenAsync(repo.Root));
        Assert.True(state.Detached); Assert.True(state.Dirty);
        using var menu = new HistoryMenuSession(state);
        Assert.True((await menu.ReadContextAsync()).Detached);
    }

    [Fact]
    public async Task Authorized_write_survives_popup_disposal_and_stale_files_still_reject_writes()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        using var reader = new RepositoryReader();
        var menu = new HistoryMenuSession(HistoryMenuState.From(await reader.OpenAsync(repo.Root)));
        menu.ClosePopup(); var context = await menu.ReadContextAsync(); menu.Dispose();
        await new BranchWriter().ExecuteAsync(context, BranchAction.Create, name: "chosen-action");
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        var next = new HistoryMenuSession(HistoryMenuState.From(await reader.OpenAsync(repo.Root)));
        context = await next.ReadContextAsync(); next.Dispose(); repo.Write("a", "external change");
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.Create, name: "stale-action"));
        Assert.Empty(repo.Git("branch", "--list", "stale-action"));
        Assert.Equal("external change", File.ReadAllText(Path.Combine(repo.Root, "a")));
    }

    [Fact]
    public async Task Locked_untracked_file_does_not_block_log_menu_branch_metadata()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        using var reader = new RepositoryReader(); var state = HistoryMenuState.From(await reader.OpenAsync(repo.Root));
        repo.Write("busy", "still being written");
        using var busy = new FileStream(Path.Combine(repo.Root, "busy"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var menu = new HistoryMenuSession(state);
        Assert.Equal("main", Assert.Single(await menu.ReadBranchesAsync()).Name);
        if (OperatingSystem.IsWindows()) await Assert.ThrowsAnyAsync<Exception>(() => menu.ReadContextAsync());
    }
}
