using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class ConflictBarViewModelTests
{
    [Fact]
    public async Task Clean_external_change_reloads_exact_owner_and_preserves_mode_before_reactivation()
    {
        var dispatcher = new CheckingDispatcher();
        var activations = new List<(string Text, DocumentMode Mode)>();
        using var shell = CreateShell(
            dispatcher,
            (tab, token) =>
            {
                activations.Add((tab.Text, tab.Mode));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        shell.HandleModeChanged(ModeMessage(shell, tab, DocumentMode.Edit));
        activations.Clear();
        dispatcher.Observe(shell);

        await shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("external", 'b')),
            CancellationToken.None);

        Assert.Equal("external", tab.Text);
        Assert.Equal(DocumentMode.Edit, tab.Mode);
        Assert.False(tab.IsDirty);
        Assert.Equal(("external", DocumentMode.Edit), Assert.Single(activations));
        Assert.False(shell.ConflictBar.IsVisible);
        Assert.True(dispatcher.AllObservableMutationsWereDispatched);
    }

    [Fact]
    public async Task Dirty_external_change_keeps_buffer_and_exposes_exact_three_decisions()
    {
        using var shell = CreateShell(new CheckingDispatcher());
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " mine");
        var mine = tab.Text;

        await shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("theirs", 'c')),
            CancellationToken.None);

        Assert.Equal(mine, tab.Text);
        Assert.True(tab.IsDirty);
        Assert.True(shell.ConflictBar.IsVisible);
        Assert.Equal(
            [ExternalChangeDecision.ReloadExternal, ExternalChangeDecision.KeepMine, ExternalChangeDecision.Compare],
            shell.ConflictBar.AvailableDecisions);
    }

    [Fact]
    public async Task KeepMine_records_exact_observed_token_for_later_explicit_overwrite_and_never_writes()
    {
        var saves = new List<SaveDecision>();
        using var shell = CreateShell(
            new CheckingDispatcher(),
            save: (buffer, decision, token) =>
            {
                saves.Add(decision);
                return Task.FromResult<SaveResult>(new SaveResult.Saved(buffer.BaselineVersion, buffer.Revision));
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " mine");
        var external = Loaded("theirs", 'd');
        await shell.HandleExternalChangeAsync(tab, FileChangeNotice.Changed(tab.Path, external), CancellationToken.None);

        await shell.ResolveExternalChangeAsync(ExternalChangeDecision.KeepMine, CancellationToken.None);

        Assert.Empty(saves);
        Assert.Equal(external.Version, shell.KeepMineObservedVersion);
        Assert.False(shell.ConflictBar.IsVisible);
        Assert.True(tab.IsDirty);

        await shell.OpenAsync(Target("two.md"), OpenGesture.ControlClick, CancellationToken.None);
        Assert.Null(shell.KeepMineObservedVersion);
        await shell.ActivateAsync(tab, CancellationToken.None);
        Assert.Equal(external.Version, shell.KeepMineObservedVersion);
    }

    [Fact]
    public async Task Compare_is_an_immutable_read_only_snapshot_and_mutates_neither_side()
    {
        using var shell = CreateShell(new CheckingDispatcher());
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " mine");
        var mine = tab.Text;
        var external = Loaded("theirs", 'e');
        await shell.HandleExternalChangeAsync(tab, FileChangeNotice.Changed(tab.Path, external), CancellationToken.None);

        await shell.ResolveExternalChangeAsync(ExternalChangeDecision.Compare, CancellationToken.None);
        var comparison = Assert.IsType<DocumentComparisonViewModel>(shell.ConflictBar.Comparison);

        Assert.Equal(mine, comparison.Mine.Text);
        Assert.Equal("theirs", comparison.External.Text);
        Assert.True(comparison.Mine.IsReadOnly);
        Assert.True(comparison.External.IsReadOnly);
        Assert.Equal(mine, tab.Text);
        Assert.Equal("theirs", external.Text);
        Dirty(shell, tab, " later");
        Assert.Equal(mine, comparison.Mine.Text);
    }

    [Fact]
    public async Task ReloadExternal_explicitly_discards_dirty_body_but_preserves_mode()
    {
        using var shell = CreateShell(new CheckingDispatcher());
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        shell.HandleModeChanged(ModeMessage(shell, tab, DocumentMode.Edit));
        Dirty(shell, tab, " mine");
        await shell.HandleExternalChangeAsync(tab, FileChangeNotice.Changed(tab.Path, Loaded("theirs", 'f')), CancellationToken.None);

        await shell.ResolveExternalChangeAsync(ExternalChangeDecision.ReloadExternal, CancellationToken.None);

        Assert.Equal("theirs", tab.Text);
        Assert.False(tab.IsDirty);
        Assert.Equal(DocumentMode.Edit, tab.Mode);
        Assert.False(shell.ConflictBar.IsVisible);
    }

    [Fact]
    public async Task ReloadExternal_removes_recovery_before_best_effort_activation_failure()
    {
        var order = new List<string>();
        using var shell = CreateShell(
            new CheckingDispatcher(),
            activate: (_, _) =>
            {
                order.Add("activate");
                throw new OperationCanceledException("surface cancelled");
            },
            removeRecovery: (_, token) =>
            {
                Assert.False(token.CanBeCanceled);
                order.Add("recovery");
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        order.Clear();
        Dirty(shell, tab, " mine");
        await shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("theirs", 'f')),
            CancellationToken.None);

        await shell.ResolveExternalChangeAsync(
            ExternalChangeDecision.ReloadExternal,
            CancellationToken.None);

        Assert.Equal("theirs", tab.Text);
        Assert.False(tab.IsDirty);
        Assert.Equal(["recovery", "activate"], order);
        Assert.False(string.IsNullOrWhiteSpace(shell.EditingErrorMessage));
    }

    [Fact]
    public async Task ReloadExternal_cleanup_failure_keeps_committed_state_and_still_activates_exact_owner()
    {
        var activations = 0;
        using var shell = CreateShell(
            new CheckingDispatcher(),
            activate: (_, _) =>
            {
                activations++;
                return Task.CompletedTask;
            },
            removeRecovery: (_, _) => throw new IOException("cleanup failed"));
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        activations = 0;
        Dirty(shell, tab, " mine");
        await shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("theirs", 'v')),
            CancellationToken.None);

        await shell.ResolveExternalChangeAsync(
            ExternalChangeDecision.ReloadExternal,
            CancellationToken.None);

        Assert.Equal("theirs", tab.Text);
        Assert.False(tab.IsDirty);
        Assert.Equal(1, activations);
        Assert.True(shell.HasEditingError);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("close")]
    [InlineData("navigate")]
    [InlineData("dispose")]
    [InlineData("cancel")]
    public async Task ReloadExternal_cleanup_continuation_activates_only_the_exact_unchanged_owner(string mutation)
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activations = new List<(Guid TabId, string Path, long Revision)>();
        using var cancellation = new CancellationTokenSource();
        var shell = CreateShell(
            new CheckingDispatcher(),
            activate: (tab, _) =>
            {
                activations.Add((tab.Id, tab.Path, tab.Revision));
                return Task.CompletedTask;
            },
            removeRecovery: async (_, token) =>
            {
                Assert.False(token.CanBeCanceled);
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        if (mutation == "navigate")
        {
            await shell.OpenAsync(Target("two.md"), OpenGesture.Normal, CancellationToken.None);
        }

        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " mine");
        await shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("theirs", 'w')),
            CancellationToken.None);
        activations.Clear();

        var resolving = shell.ResolveExternalChangeAsync(
            ExternalChangeDecision.ReloadExternal,
            cancellation.Token);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("theirs", tab.Text);
        Assert.False(tab.IsDirty);

        switch (mutation)
        {
            case "switch":
                await shell.OpenAsync(Target("other.md"), OpenGesture.ControlClick, CancellationToken.None);
                break;
            case "close":
                shell.CloseTab(tab);
                break;
            case "navigate":
                await shell.GoBackAsync(CancellationToken.None);
                break;
            case "dispose":
                shell.Dispose();
                break;
            case "cancel":
                cancellation.Cancel();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var activationCountAfterMutation = activations.Count;
        releaseCleanup.TrySetResult();
        await resolving;

        Assert.Equal(activationCountAfterMutation, activations.Count);
        if (mutation == "switch")
        {
            Assert.EndsWith("other.md", shell.ActiveTab!.Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(FileChangeKind.Deleted)]
    [InlineData(FileChangeKind.Renamed)]
    [InlineData(FileChangeKind.Inaccessible)]
    public async Task Path_failures_preserve_buffer_and_expose_error_without_conflict_choices(FileChangeKind kind)
    {
        using var shell = CreateShell(new CheckingDispatcher());
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " mine");
        var before = tab.Text;
        var notice = kind switch
        {
            FileChangeKind.Deleted => FileChangeNotice.Deleted(tab.Path),
            FileChangeKind.Renamed => FileChangeNotice.Renamed(tab.Path, tab.Path + ".moved"),
            _ => FileChangeNotice.Inaccessible(tab.Path, nameof(IOException)),
        };

        await shell.HandleExternalChangeAsync(tab, notice, CancellationToken.None);

        Assert.Equal(before, tab.Text);
        Assert.True(tab.IsDirty);
        Assert.True(shell.ConflictBar.IsVisible);
        Assert.Empty(shell.ConflictBar.AvailableDecisions);
        Assert.False(string.IsNullOrWhiteSpace(shell.ConflictBar.Message));
    }

    [Fact]
    public async Task Queued_external_result_cannot_overwrite_a_newer_edit()
    {
        var dispatcher = new QueuedDispatcher();
        using var shell = CreateShell(dispatcher);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        dispatcher.Drain();
        var tab = shell.ActiveTab!;

        var handling = shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("external", 'g')),
            CancellationToken.None);
        await dispatcher.WaitUntilQueuedAsync();
        Dirty(shell, tab, " newer");
        dispatcher.Drain();
        await handling;

        Assert.EndsWith(" newer", tab.Text, StringComparison.Ordinal);
        Assert.True(tab.IsDirty);
        Assert.False(shell.ConflictBar.IsVisible);
    }

    [Fact]
    public async Task Queued_external_result_cannot_roll_back_a_newer_completed_save()
    {
        var dispatcher = new QueuedDispatcher();
        using var shell = CreateShell(
            dispatcher,
            save: (buffer, decision, token) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Loaded("saved", 's').Version, buffer.Revision)));
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        dispatcher.Drain();
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " mine");
        var mine = tab.Text;

        var handling = shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("external", 'r')),
            CancellationToken.None);
        await dispatcher.WaitUntilQueuedAsync();
        var saving = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        await dispatcher.DrainUntilCompletedAsync(saving);
        dispatcher.Drain();
        await handling;

        Assert.Equal(mine, tab.Text);
        Assert.False(tab.IsDirty);
        Assert.False(shell.ConflictBar.IsVisible);
    }

    [Fact]
    public async Task Background_navigation_save_and_dispose_invalidate_late_external_results()
    {
        foreach (var invalidate in new[] { "background", "navigation", "save", "dispose" })
        {
            var dispatcher = new QueuedDispatcher();
            var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var shell = CreateShell(
                dispatcher,
                save: (buffer, decision, token) => saving.Task);
            await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
            dispatcher.Drain();
            var original = shell.ActiveTab!;
            if (invalidate == "save")
            {
                Dirty(shell, original, " dirty");
            }

            var handling = shell.HandleExternalChangeAsync(
                original,
                FileChangeNotice.Changed(original.Path, Loaded("external", 'h')),
                CancellationToken.None);
            await dispatcher.WaitUntilQueuedAsync();

            Task? saveTask = null;
            switch (invalidate)
            {
                case "background":
                    await shell.OpenAsync(Target("two.md"), OpenGesture.ControlClick, CancellationToken.None);
                    break;
                case "navigation":
                    await shell.OpenAsync(Target("two.md"), OpenGesture.Normal, CancellationToken.None);
                    break;
                case "save":
                    saveTask = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
                    break;
                case "dispose":
                    shell.Dispose();
                    break;
            }

            dispatcher.Drain();
            await handling;
            if (saveTask is not null)
            {
                saving.SetResult(new SaveResult.Conflict(Loaded("disk", 'i').Version));
                await saveTask;
            }

            Assert.NotEqual("external", original.Text);
            Assert.False(shell.ConflictBar.IsVisible);
        }
    }

    [Fact]
    public async Task Navigation_close_and_window_disposal_cancel_each_owned_watch_without_leaks()
    {
        var watches = new TrackingWatches();
        var shell = CreateShell(new CheckingDispatcher(), watch: watches.WatchAsync);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        await watches.WaitForStartsAsync(1);

        await shell.OpenAsync(Target("two.md"), OpenGesture.Normal, CancellationToken.None);
        await watches.WaitForStartsAsync(2);
        await watches.WaitForStopsAsync(1);

        shell.CloseActiveTab();
        await watches.WaitForStopsAsync(2);
        await shell.OpenAsync(Target("three.md"), OpenGesture.Normal, CancellationToken.None);
        await watches.WaitForStartsAsync(3);

        shell.Dispose();
        await watches.WaitForStopsAsync(3);

        Assert.Equal(0, watches.ActiveCount);
    }

    [Fact]
    public async Task Superseded_save_completion_does_not_record_a_saved_watcher_token()
    {
        var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new List<(string Path, DiskFileVersion Version)>();
        using var shell = CreateShell(
            new CheckingDispatcher(),
            save: (buffer, decision, token) => saving.Task,
            recordSavedVersion: (path, version) => recorded.Add((path, version)));
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        Dirty(shell, shell.ActiveTab!, " mine");
        var pending = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        shell.Dispose();
        var savedVersion = Loaded("saved", 'z').Version;

        saving.SetResult(new SaveResult.Saved(savedVersion, 2));
        await pending;

        Assert.Empty(recorded);
    }

    [Fact]
    public async Task Successful_SaveAs_reowns_watcher_to_new_path_and_ignores_old_queued_notice()
    {
        var watches = new TrackingWatches();
        var savedVersion = Loaded("mine", 's').Version;
        using var shell = CreateShell(
            new CheckingDispatcher(),
            save: (buffer, decision, token) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(savedVersion, buffer.Revision)),
            watch: watches.WatchAsync);
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        await watches.WaitForStartsAsync(1);
        var tab = shell.ActiveTab!;
        var oldPath = tab.Path;
        Dirty(shell, tab, " mine");
        watches.Emit(oldPath, FileChangeNotice.Changed(oldPath, Loaded("late old", 'o')));

        var newPath = Path.GetFullPath("b.md");
        await shell.SaveActiveAsync(
            new SaveDecision.SaveAs(newPath, new EncodingDescriptor("utf-8", false)),
            CancellationToken.None);
        await watches.WaitForStartsAsync(2);
        await watches.WaitForStopsAsync(1);
        watches.Emit(newPath, FileChangeNotice.Changed(newPath, Loaded("new external", 'n')));
        await WaitUntilAsync(() => tab.Text == "new external");

        Assert.Equal(newPath, tab.Path);
        Assert.Equal("new external", tab.Text);
        Assert.DoesNotContain("late old", tab.Text, StringComparison.Ordinal);
        shell.Dispose();
        await watches.WaitForStopsAsync(2);
        Assert.Equal(0, watches.ActiveCount);
    }

    [Fact]
    public async Task Failed_SaveAs_keeps_exact_old_watcher_and_never_starts_new_path()
    {
        var watches = new TrackingWatches();
        using var shell = CreateShell(
            new CheckingDispatcher(),
            save: (buffer, decision, token) => Task.FromResult<SaveResult>(
                new SaveResult.Conflict(Loaded("disk", 'd').Version)),
            watch: watches.WatchAsync);
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        await watches.WaitForStartsAsync(1);
        var tab = shell.ActiveTab!;
        var oldPath = tab.Path;
        Dirty(shell, tab, " mine");

        await shell.SaveActiveAsync(
            new SaveDecision.SaveAs("b.md", new EncodingDescriptor("utf-8", false)),
            CancellationToken.None);

        Assert.Equal(oldPath, tab.Path);
        Assert.Equal(1, watches.StartCount);
        Assert.Equal(1, watches.ActiveCount);
    }

    [Fact]
    public async Task Background_clean_notice_is_revalidated_on_reactivation_without_touching_visible_tab()
    {
        var watches = new TrackingWatches();
        var disk = new System.Collections.Concurrent.ConcurrentDictionary<string, LoadedDocument>(
            StringComparer.OrdinalIgnoreCase);
        var activations = new List<(Guid Id, string Text)>();
        using var shell = CreateShell(
            new CheckingDispatcher(),
            activate: (tab, token) =>
            {
                activations.Add((tab.Id, tab.Text));
                return Task.CompletedTask;
            },
            watch: watches.WatchAsync,
            load: (path, encoding, token) => Task.FromResult(
                disk.GetOrAdd(path, _ => Loaded("original", 'a'))));
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tabA = shell.ActiveTab!;
        await shell.OpenAsync(Target("b.md"), OpenGesture.ControlClick, CancellationToken.None);
        var tabB = shell.ActiveTab!;
        var visibleText = tabB.Text;
        var activationCount = activations.Count;
        var external = Loaded("external A", 'x');
        disk[tabA.Path] = external;

        watches.Emit(tabA.Path, FileChangeNotice.Changed(tabA.Path, Loaded("older A", 'w')));
        await WaitUntilAsync(() => shell.PendingExternalCount == 1);

        Assert.Same(tabB, shell.ActiveTab);
        Assert.Equal(visibleText, tabB.Text);
        Assert.Equal(activationCount, activations.Count);
        Assert.False(shell.ConflictBar.IsVisible);

        await shell.ActivateAsync(tabA, CancellationToken.None);

        Assert.Equal("external A", tabA.Text);
        Assert.False(tabA.IsDirty);
        Assert.Equal((tabA.Id, "external A"), activations[^1]);
        Assert.Equal(0, shell.PendingExternalCount);
    }

    [Fact]
    public async Task Background_dirty_notice_revalidates_newest_disk_and_shows_conflict_without_mutating_mine()
    {
        var watches = new TrackingWatches();
        var disk = new System.Collections.Concurrent.ConcurrentDictionary<string, LoadedDocument>(
            StringComparer.OrdinalIgnoreCase);
        using var shell = CreateShell(
            new CheckingDispatcher(),
            watch: watches.WatchAsync,
            load: (path, encoding, token) => Task.FromResult(
                disk.GetOrAdd(path, _ => Loaded("original", 'a'))));
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tabA = shell.ActiveTab!;
        Dirty(shell, tabA, " mine");
        var mine = tabA.Text;
        await shell.OpenAsync(Target("b.md"), OpenGesture.ControlClick, CancellationToken.None);
        watches.Emit(tabA.Path, FileChangeNotice.Changed(tabA.Path, Loaded("external 1", 'b')));
        watches.Emit(tabA.Path, FileChangeNotice.Changed(tabA.Path, Loaded("external 2", 'c')));
        disk[tabA.Path] = Loaded("external newest", 'd');
        await WaitUntilAsync(() => shell.PendingExternalCount == 1);

        await shell.ActivateAsync(tabA, CancellationToken.None);

        Assert.Equal(mine, tabA.Text);
        Assert.True(tabA.IsDirty);
        Assert.True(shell.ConflictBar.IsVisible);
        Assert.Equal(
            [ExternalChangeDecision.ReloadExternal, ExternalChangeDecision.KeepMine, ExternalChangeDecision.Compare],
            shell.ConflictBar.AvailableDecisions);
        await shell.ResolveExternalChangeAsync(ExternalChangeDecision.Compare, CancellationToken.None);
        Assert.Equal("external newest", shell.ConflictBar.Comparison?.External.Text);
    }

    [Fact]
    public async Task Stale_reactivation_keeps_the_exact_pending_notice_for_the_next_reactivation()
    {
        var watches = new TrackingWatches();
        var revalidationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedRevalidation = new TaskCompletionSource<LoadedDocument>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        using var shell = CreateShell(
            new CheckingDispatcher(),
            watch: watches.WatchAsync,
            load: (path, encoding, token) => Interlocked.Increment(ref loadCount) switch
            {
                1 => Task.FromResult(Loaded("original A", 'a')),
                2 => Task.FromResult(Loaded("original B", 'b')),
                3 => StartDelayedRevalidation(),
                4 => Task.FromResult(Loaded("external latest", 'x')),
                _ => throw new InvalidOperationException("Unexpected load."),
            });
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tabA = shell.ActiveTab!;
        await shell.OpenAsync(Target("b.md"), OpenGesture.ControlClick, CancellationToken.None);
        var tabB = shell.ActiveTab!;
        watches.Emit(tabA.Path, FileChangeNotice.Changed(tabA.Path, Loaded("noticed", 'n')));
        await WaitUntilAsync(() => shell.PendingExternalCount == 1);

        var staleActivation = shell.ActivateAsync(tabA, CancellationToken.None);
        await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shell.ActivateAsync(tabB, CancellationToken.None);
        delayedRevalidation.SetResult(Loaded("stale result", 's'));
        await staleActivation;

        Assert.Equal(1, shell.PendingExternalCount);
        Assert.Equal("original A", tabA.Text);

        await shell.ActivateAsync(tabA, CancellationToken.None);

        Assert.Equal("external latest", tabA.Text);
        Assert.Equal(0, shell.PendingExternalCount);
        Assert.Equal(4, loadCount);
        shell.Dispose();
        await watches.WaitForStopsAsync(2);
        Assert.Equal(0, watches.ActiveCount);
        Assert.Equal(0, shell.PendingExternalCount);

        Task<LoadedDocument> StartDelayedRevalidation()
        {
            revalidationStarted.TrySetResult();
            return delayedRevalidation.Task;
        }
    }

    [Fact]
    public async Task Older_revalidation_cannot_consume_a_newer_background_notice()
    {
        var revalidationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedRevalidation = new TaskCompletionSource<LoadedDocument>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        using var shell = CreateShell(
            new CheckingDispatcher(),
            load: (path, encoding, token) => Interlocked.Increment(ref loadCount) switch
            {
                1 => Task.FromResult(Loaded("original A", 'a')),
                2 => Task.FromResult(Loaded("original B", 'b')),
                3 => StartDelayedRevalidation(),
                4 => Task.FromResult(Loaded("external newest", 'z')),
                _ => throw new InvalidOperationException("Unexpected load."),
            });
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tabA = shell.ActiveTab!;
        await shell.OpenAsync(Target("b.md"), OpenGesture.ControlClick, CancellationToken.None);
        var tabB = shell.ActiveTab!;
        await shell.HandleExternalChangeAsync(
            tabA,
            FileChangeNotice.Changed(tabA.Path, Loaded("external old", 'o')),
            CancellationToken.None);

        var oldActivation = shell.ActivateAsync(tabA, CancellationToken.None);
        await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shell.ActivateAsync(tabB, CancellationToken.None);
        await shell.HandleExternalChangeAsync(
            tabA,
            FileChangeNotice.Changed(tabA.Path, Loaded("external newer notice", 'n')),
            CancellationToken.None);
        delayedRevalidation.SetResult(Loaded("external stale read", 's'));
        await oldActivation;

        Assert.Equal(1, shell.PendingExternalCount);
        await shell.ActivateAsync(tabA, CancellationToken.None);

        Assert.Equal("external newest", tabA.Text);
        Assert.Equal(0, shell.PendingExternalCount);

        Task<LoadedDocument> StartDelayedRevalidation()
        {
            revalidationStarted.TrySetResult();
            return delayedRevalidation.Task;
        }
    }

    [Fact]
    public async Task Pending_identity_does_not_repeat_after_save_invalidation_during_revalidation()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRead = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        using var shell = CreateShell(
            new CheckingDispatcher(),
            save: (buffer, decision, token) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Loaded("saved", 's').Version, buffer.Revision)),
            load: (path, encoding, token) => Interlocked.Increment(ref loadCount) switch
            {
                1 => Task.FromResult(Loaded("original A", 'a')),
                2 => Task.FromResult(Loaded("original B", 'b')),
                3 => Start(firstStarted, firstRead),
                4 => Start(secondStarted, secondRead),
                _ => throw new InvalidOperationException("Unexpected load."),
            });
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tabA = shell.ActiveTab!;
        await shell.OpenAsync(Target("b.md"), OpenGesture.ControlClick, CancellationToken.None);
        var tabB = shell.ActiveTab!;
        var repeatedNotice = FileChangeNotice.Changed(tabA.Path, Loaded("same notice", 'n'));
        await shell.HandleExternalChangeAsync(tabA, repeatedNotice, CancellationToken.None);

        var oldActivation = shell.ActivateAsync(tabA, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        await shell.ActivateAsync(tabB, CancellationToken.None);
        await shell.HandleExternalChangeAsync(tabA, repeatedNotice, CancellationToken.None);
        var newActivation = shell.ActivateAsync(tabA, CancellationToken.None);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        firstRead.SetResult(Loaded("stale read", 'o'));
        await oldActivation;
        secondRead.SetResult(Loaded("external newest", 'z'));
        await newActivation;

        Assert.Equal("external newest", tabA.Text);
        Assert.Equal(0, shell.PendingExternalCount);

        static Task<LoadedDocument> Start(
            TaskCompletionSource started,
            TaskCompletionSource<LoadedDocument> read)
        {
            started.TrySetResult();
            return read.Task;
        }
    }

    [Fact]
    public async Task Shell_disposal_cancels_owned_pending_revalidation_io()
    {
        var revalidationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revalidationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        var shell = CreateShell(
            new CheckingDispatcher(),
            load: (path, encoding, token) => Interlocked.Increment(ref loadCount) switch
            {
                1 => Task.FromResult(Loaded("original A", 'a')),
                2 => Task.FromResult(Loaded("original B", 'b')),
                3 => WaitForCancellation(token),
                _ => throw new InvalidOperationException("Unexpected load."),
            });
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tabA = shell.ActiveTab!;
        await shell.OpenAsync(Target("b.md"), OpenGesture.ControlClick, CancellationToken.None);
        await shell.HandleExternalChangeAsync(
            tabA,
            FileChangeNotice.Changed(tabA.Path, Loaded("external", 'x')),
            CancellationToken.None);
        var activation = shell.ActivateAsync(tabA, CancellationToken.None);
        await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        shell.Dispose();

        await revalidationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await activation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, shell.PendingExternalCount);

        async Task<LoadedDocument> WaitForCancellation(CancellationToken token)
        {
            revalidationStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Revalidation was not cancelled.");
            }
            finally
            {
                if (token.IsCancellationRequested)
                {
                    revalidationCancelled.TrySetResult();
                }
            }
        }
    }

    [Fact]
    public async Task Closing_background_tab_discards_pending_notice_and_stops_revalidation_ownership()
    {
        var watches = new TrackingWatches();
        var loads = 0;
        using var shell = CreateShell(
            new CheckingDispatcher(),
            watch: watches.WatchAsync,
            load: (path, encoding, token) =>
            {
                loads++;
                return Task.FromResult(Loaded("original", 'a'));
            });
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tabA = shell.ActiveTab!;
        await shell.OpenAsync(Target("b.md"), OpenGesture.ControlClick, CancellationToken.None);
        watches.Emit(tabA.Path, FileChangeNotice.Changed(tabA.Path, Loaded("external", 'e')));
        await WaitUntilAsync(() => shell.PendingExternalCount == 1);

        shell.CloseTab(tabA);
        await WaitUntilAsync(() => shell.PendingExternalCount == 0);

        Assert.Equal(2, loads);
        Assert.DoesNotContain(tabA, shell.Tabs);
    }

    [Fact]
    public async Task External_reload_roundtrips_exact_owned_selection_and_scroll_hints_to_new_revision()
    {
        var activated = new List<(long Revision, DocumentUiHints Hints)>();
        using var shell = CreateShell(
            new CheckingDispatcher(),
            activate: (tab, token) =>
            {
                activated.Add((tab.Revision, tab.UiHints));
                return Task.CompletedTask;
            },
            save: (buffer, decision, token) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Loaded("saved", 's').Version, buffer.Revision)));
        await shell.OpenAsync(Target("a.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        var hints = new DocumentUiHints(7, 3, 91.25);
        shell.HandleDocumentUiHintsChanged(new DocumentUiHintsChangedMessage(
            new WebMessageOwner(Guid.NewGuid(), shell.WindowId, tab.Id, tab.Revision),
            hints));
        Dirty(shell, tab, " typed");
        await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        Assert.False(tab.IsDirty);

        await shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("external body", 'x')),
            CancellationToken.None);

        Assert.Equal(hints, tab.UiHints);
        Assert.Equal((tab.Revision, hints), activated[^1]);
        shell.HandleDocumentUiHintsChanged(new DocumentUiHintsChangedMessage(
            new WebMessageOwner(Guid.NewGuid(), shell.WindowId, tab.Id, tab.Revision - 1),
            hints with { ScrollTop = 999 }));
        Assert.Equal(hints, tab.UiHints);
    }

    private static ShellViewModel CreateShell(
        Action<Action> dispatcher,
        Func<DocumentTabViewModel, CancellationToken, Task>? activate = null,
        Func<DocumentBuffer, SaveDecision, CancellationToken, Task<SaveResult>>? save = null,
        Func<string, CancellationToken, IAsyncEnumerable<FileChangeNotice>>? watch = null,
        Action<string, DiskFileVersion>? recordSavedVersion = null,
        Func<string, Encoding?, CancellationToken, Task<LoadedDocument>>? load = null,
        Func<Guid, CancellationToken, Task>? removeRecovery = null)
    {
        App.RegisterEncodingProviders();
        return new ShellViewModel(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            load ?? ((path, encoding, token) => Task.FromResult(Loaded("original", 'a'))),
            activate ?? ((tab, token) => Task.CompletedTask),
            saveDocument: save,
            dispatcher: dispatcher,
            watchExternalChanges: watch,
            recordSavedVersion: recordSavedVersion,
            removeRecovery: removeRecovery);
    }

    private static void Dirty(ShellViewModel shell, DocumentTabViewModel tab, string insertedText) =>
        shell.HandleDocumentChanged(new DocumentChangedMessage(
            new WebMessageOwner(Guid.NewGuid(), shell.WindowId, tab.Id, tab.Revision),
            new DocumentEdit(tab.Revision, [new TextChange(tab.Text.Length, tab.Text.Length, insertedText)])));

    private static DocumentModeChangedMessage ModeMessage(
        ShellViewModel shell,
        DocumentTabViewModel tab,
        DocumentMode mode) =>
        new(new WebMessageOwner(Guid.NewGuid(), shell.WindowId, tab.Id, tab.Revision), mode);

    private static DocumentTarget Target(string path) => new(Path.GetFullPath(path), null, null);

    private static LoadedDocument Loaded(string text, char hash) =>
        new(
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(text.Length, DateTime.UnixEpoch.AddDays(hash), new string(hash, 64)));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class CheckingDispatcher
    {
        private bool dispatching;
        internal bool AllObservableMutationsWereDispatched { get; private set; } = true;

        public void Invoke(Action action)
        {
            dispatching = true;
            try
            {
                action();
            }
            finally
            {
                dispatching = false;
            }
        }

        internal void Observe(ShellViewModel shell)
        {
            shell.PropertyChanged += Check;
            shell.ConflictBar.PropertyChanged += Check;
            shell.Tabs.CollectionChanged += CheckCollection;
            foreach (var tab in shell.Tabs)
            {
                tab.PropertyChanged += Check;
            }
        }

        private void Check(object? sender, PropertyChangedEventArgs args) =>
            AllObservableMutationsWereDispatched &= dispatching;

        private void CheckCollection(object? sender, NotifyCollectionChangedEventArgs args) =>
            AllObservableMutationsWereDispatched &= dispatching;

        public static implicit operator Action<Action>(CheckingDispatcher dispatcher) => dispatcher.Invoke;
    }

    private sealed class QueuedDispatcher
    {
        private readonly Queue<Action> actions = [];
        private readonly SemaphoreSlim queued = new(0);

        public void Invoke(Action action)
        {
            actions.Enqueue(action);
            queued.Release();
        }

        internal async Task WaitUntilQueuedAsync() =>
            await queued.WaitAsync(TimeSpan.FromSeconds(5));

        internal void Drain()
        {
            while (actions.TryDequeue(out var action))
            {
                action();
            }

            while (queued.Wait(0))
            {
            }
        }

        internal async Task DrainUntilCompletedAsync(Task task)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!task.IsCompleted)
            {
                if (await queued.WaitAsync(TimeSpan.FromMilliseconds(10), timeout.Token))
                {
                    Drain();
                }
            }

            await task;
        }

        public static implicit operator Action<Action>(QueuedDispatcher dispatcher) => dispatcher.Invoke;
    }

    private sealed class TrackingWatches
    {
        private readonly Channel<int> starts = Channel.CreateUnbounded<int>();
        private readonly Channel<int> stops = Channel.CreateUnbounded<int>();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Channel<FileChangeNotice>> notices =
            new(StringComparer.OrdinalIgnoreCase);
        private int startCount;
        private int stopCount;
        private int activeCount;

        internal int ActiveCount => Volatile.Read(ref activeCount);
        internal int StartCount => Volatile.Read(ref startCount);

        internal async IAsyncEnumerable<FileChangeNotice> WatchAsync(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var started = Interlocked.Increment(ref startCount);
            Interlocked.Increment(ref activeCount);
            starts.Writer.TryWrite(started);
            var channel = Channel.CreateUnbounded<FileChangeNotice>();
            notices[Path.GetFullPath(path)] = channel;
            try
            {
                await foreach (var notice in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return notice;
                }
            }
            finally
            {
                notices.TryRemove(Path.GetFullPath(path), out _);
                Interlocked.Decrement(ref activeCount);
                var stopped = Interlocked.Increment(ref stopCount);
                stops.Writer.TryWrite(stopped);
            }
        }

        internal void Emit(string path, FileChangeNotice notice)
        {
            Assert.True(notices.TryGetValue(Path.GetFullPath(path), out var channel));
            Assert.True(channel.Writer.TryWrite(notice));
        }

        internal Task WaitForStartsAsync(int count) => WaitForCountAsync(starts.Reader, count);

        internal Task WaitForStopsAsync(int count) => WaitForCountAsync(stops.Reader, count);

        private static async Task WaitForCountAsync(ChannelReader<int> reader, int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int observed;
            do
            {
                observed = await reader.ReadAsync(timeout.Token);
            }
            while (observed < count);
        }
    }
}
