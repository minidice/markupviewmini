using MarkUpViewMini.App.Composition;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.App.Tests;

public sealed class EditingAcceptanceTests
{
    [Fact]
    public void Edit_and_history_commands_require_provider_capability_mode_and_exact_surface_revision()
    {
        var editable = LoadedTab(new MarkdownDocumentProvider());
        var current = new WebResponseContext(Guid.NewGuid(), editable.Id, editable.Revision);

        Assert.True(WindowInputPolicy.CanExecuteModeToggle(editable, current));
        Assert.False(WindowInputPolicy.CanExecuteEditorHistory(editable, current));
        editable.SetMode(DocumentMode.Edit);
        Assert.True(WindowInputPolicy.CanExecuteEditorHistory(editable, current));
        Assert.True(WindowInputPolicy.CanExecuteFind(editable, current));
        Assert.False(WindowInputPolicy.CanExecuteEditorHistory(
            editable,
            current with { Revision = editable.Revision + 1 }));

        var readOnly = LoadedTab(new ReadOnlyProvider());
        var readOnlyCurrent = new WebResponseContext(Guid.NewGuid(), readOnly.Id, readOnly.Revision);
        Assert.False(WindowInputPolicy.CanExecuteModeToggle(readOnly, readOnlyCurrent));
        Assert.False(WindowInputPolicy.CanExecuteEditorHistory(readOnly, readOnlyCurrent));
        Assert.False(WindowInputPolicy.CanExecuteSave(readOnly));
    }

    [Fact]
    public async Task Read_only_provider_rejects_a_forged_current_owner_edit()
    {
        App.RegisterEncodingProviders();
        var provider = new ReadOnlyProvider();
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([provider]),
            (_, _, _) => Task.FromResult(Loaded()),
            (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target("readonly.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;

        Dirty(shell, tab, "forged");

        Assert.Equal("text", tab.Text);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Accepted_edit_schedules_recovery_and_exact_saved_revision_removes_it()
    {
        var scheduled = new List<DocumentBufferSnapshot>();
        var removed = new List<Guid>();
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('b'), buffer.Revision)),
            buffer => scheduled.Add(buffer.CaptureSnapshot()),
            (tabId, _) =>
            {
                removed.Add(tabId);
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;

        Dirty(shell, tab, "!");
        await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);

        Assert.Single(scheduled);
        Assert.Equal(tab.Id, scheduled[0].TabId);
        Assert.Equal("text!", scheduled[0].Text);
        Assert.False(tab.IsDirty);
        Assert.Equal([tab.Id], removed);
    }

    [Fact]
    public async Task Cancel_during_multi_tab_close_stops_before_any_save_discard_or_recovery_removal()
    {
        var saves = 0;
        var removed = new List<Guid>();
        var shell = CreateShell(
            (buffer, _, _) =>
            {
                saves++;
                return Task.FromResult<SaveResult>(new SaveResult.Saved(Version('b'), buffer.Revision));
            },
            _ => { },
            (tabId, _) =>
            {
                removed.Add(tabId);
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var first = shell.ActiveTab!;
        Dirty(shell, first, "1");
        await shell.OpenAsync(Target("two.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);
        var second = shell.ActiveTab!;
        Dirty(shell, second, "2");
        var prompted = new List<Guid>();

        var allowed = await shell.TryResolveDirtyTabsForCloseAsync(
            shell.Tabs,
            tab =>
            {
                prompted.Add(tab.Id);
                return tab == first ? DirtyCloseChoice.Save : DirtyCloseChoice.Cancel;
            },
            CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal([first.Id, second.Id], prompted);
        Assert.Equal(0, saves);
        Assert.Empty(removed);
        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
    }

    [Fact]
    public async Task Application_close_collects_every_window_choice_before_discarding_recovery()
    {
        var removed = new List<Guid>();
        var firstShell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("save must not run"),
            _ => { },
            (tabId, _) =>
            {
                removed.Add(tabId);
                return Task.CompletedTask;
            });
        var secondShell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("save must not run"),
            _ => { },
            (tabId, _) =>
            {
                removed.Add(tabId);
                return Task.CompletedTask;
            });
        await firstShell.OpenAsync(Target("first-window.md"), OpenGesture.Normal, CancellationToken.None);
        await secondShell.OpenAsync(Target("second-window.md"), OpenGesture.Normal, CancellationToken.None);
        Dirty(firstShell, firstShell.ActiveTab!, " first dirty body");
        Dirty(secondShell, secondShell.ActiveTab!, " second dirty body");

        var allowed = await DirtyCloseCoordinator.TryResolveAsync(
            [
                new DirtyCloseRequest(firstShell, firstShell.Tabs, _ => DirtyCloseChoice.Discard),
                new DirtyCloseRequest(secondShell, secondShell.Tabs, _ => DirtyCloseChoice.Cancel),
            ],
            CancellationToken.None);

        Assert.False(allowed);
        Assert.Empty(removed);
        Assert.True(firstShell.ActiveTab!.IsDirty);
        Assert.True(secondShell.ActiveTab!.IsDirty);
    }

    [Fact]
    public async Task Application_close_collects_every_window_choice_before_saving()
    {
        var saves = 0;
        var firstShell = CreateShell(
            (buffer, _, _) =>
            {
                saves++;
                return Task.FromResult<SaveResult>(new SaveResult.Saved(Version('b'), buffer.Revision));
            },
            _ => { },
            (_, _) => Task.CompletedTask);
        var secondShell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("save must not run"),
            _ => { },
            (_, _) => Task.CompletedTask);
        await firstShell.OpenAsync(Target("first-window.md"), OpenGesture.Normal, CancellationToken.None);
        await secondShell.OpenAsync(Target("second-window.md"), OpenGesture.Normal, CancellationToken.None);
        Dirty(firstShell, firstShell.ActiveTab!, " first dirty body");
        Dirty(secondShell, secondShell.ActiveTab!, " second dirty body");

        var allowed = await DirtyCloseCoordinator.TryResolveAsync(
            [
                new DirtyCloseRequest(firstShell, firstShell.Tabs, _ => DirtyCloseChoice.Save),
                new DirtyCloseRequest(secondShell, secondShell.Tabs, _ => DirtyCloseChoice.Cancel),
            ],
            CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal(0, saves);
        Assert.True(firstShell.ActiveTab!.IsDirty);
        Assert.True(secondShell.ActiveTab!.IsDirty);
    }

    [Fact]
    public async Task Application_close_aborts_when_an_initially_clean_window_becomes_dirty_during_save()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var scheduled = scenario.SecondScheduled;
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task;

        Dirty(scenario.SecondShell, scenario.SecondShell.ActiveTab!, " newly dirty");
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.False(scenario.FirstShell.ActiveTab!.IsDirty);
        Assert.True(scenario.SecondShell.ActiveTab!.IsDirty);
        Assert.Equal("text newly dirty", scheduled[^1].Text);
        Assert.DoesNotContain(scenario.SecondShell.ActiveTab!.Id, scenario.Removed);
    }

    [Fact]
    public async Task Application_close_aborts_when_a_new_dirty_tab_appears_during_save()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task;

        await scenario.SecondShell.OpenAsync(
            Target("new-tab.md"),
            OpenGesture.ExplicitNewTab,
            CancellationToken.None);
        Dirty(scenario.SecondShell, scenario.SecondShell.ActiveTab!, " new tab dirty");
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.Equal(2, scenario.SecondShell.Tabs.Count);
        Assert.True(scenario.SecondShell.ActiveTab!.IsDirty);
        Assert.DoesNotContain(scenario.SecondShell.ActiveTab!.Id, scenario.Removed);
    }

    [Fact]
    public async Task Application_close_aborts_when_the_window_set_changes_during_save()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var windowSetGeneration = 1;
        var pending = scenario.ResolveAsync(() => windowSetGeneration == 1);
        await scenario.SaveStarted.Task;

        windowSetGeneration++;
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.False(scenario.FirstShell.ActiveTab!.IsDirty);
        Assert.DoesNotContain(scenario.SecondShell.ActiveTab!.Id, scenario.Removed);
        Assert.True(scenario.FirstShell.HasEditingError);
        Assert.True(scenario.SecondShell.HasEditingError);
    }

    [Fact]
    public async Task Application_close_aborts_and_preserves_recovery_for_a_new_dirty_window()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var windowSetGeneration = 1;
        var firstWindow = new ShutdownAbortProbe(scenario.FirstShell);
        var secondWindow = new ShutdownAbortProbe(scenario.SecondShell);
        var currentWindows = new List<ShutdownAbortProbe> { firstWindow, secondWindow };
        var pending = scenario.ResolveAsync(() => windowSetGeneration == 1);
        await scenario.SaveStarted.Task;
        var thirdScheduled = new List<DocumentBufferSnapshot>();
        var thirdShell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("third save must not run"),
            buffer => thirdScheduled.Add(buffer.CaptureSnapshot()),
            (_, _) => Task.CompletedTask);
        await thirdShell.OpenAsync(Target("third-window.md"), OpenGesture.Normal, CancellationToken.None);
        Dirty(thirdShell, thirdShell.ActiveTab!, " third dirty");
        thirdScheduled.Clear();
        var thirdWindow = new ShutdownAbortProbe(thirdShell);
        currentWindows.Add(thirdWindow);
        windowSetGeneration++;
        scenario.CompleteSave();

        Assert.False(await pending);
        ApplicationShutdownAbortCoordinator.AbortCurrentWindows(
            currentWindows,
            static window => window.AbortApplicationShutdown());

        Assert.True(thirdShell.ActiveTab!.IsDirty);
        Assert.Equal("text third dirty", Assert.Single(thirdScheduled).Text);
        Assert.Equal(1, thirdWindow.AbortCalls);
        Assert.True(thirdShell.HasEditingError);
        Assert.Single(scenario.FirstShell.Tabs);
        Assert.Single(scenario.SecondShell.Tabs);
        Assert.Single(thirdShell.Tabs);
        Assert.DoesNotContain(thirdShell.ActiveTab.Id, scenario.Removed);
    }

    [Fact]
    public async Task Application_abort_visits_a_new_clean_window_without_scheduling_recovery()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var windowSetGeneration = 1;
        var currentWindows = new List<ShutdownAbortProbe>
        {
            new(scenario.FirstShell),
            new(scenario.SecondShell),
        };
        var pending = scenario.ResolveAsync(() => windowSetGeneration == 1);
        await scenario.SaveStarted.Task;
        var thirdScheduled = new List<DocumentBufferSnapshot>();
        var thirdShell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("third save must not run"),
            buffer => thirdScheduled.Add(buffer.CaptureSnapshot()),
            (_, _) => Task.CompletedTask);
        await thirdShell.OpenAsync(Target("third-clean-window.md"), OpenGesture.Normal, CancellationToken.None);
        var thirdWindow = new ShutdownAbortProbe(thirdShell);
        currentWindows.Add(thirdWindow);
        windowSetGeneration++;
        scenario.CompleteSave();

        Assert.False(await pending);
        ApplicationShutdownAbortCoordinator.AbortCurrentWindows(
            currentWindows,
            static window => window.AbortApplicationShutdown());

        Assert.Equal(1, thirdWindow.AbortCalls);
        Assert.Empty(thirdScheduled);
        Assert.True(thirdShell.HasEditingError);
        Assert.False(thirdShell.ActiveTab!.IsDirty);
        Assert.Single(thirdShell.Tabs);
    }

    [Fact]
    public async Task Application_abort_excludes_a_new_window_that_closed_before_abort()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var windowSetGeneration = 1;
        var currentWindows = new List<ShutdownAbortProbe>
        {
            new(scenario.FirstShell),
            new(scenario.SecondShell),
        };
        var pending = scenario.ResolveAsync(() => windowSetGeneration == 1);
        await scenario.SaveStarted.Task;
        var thirdScheduled = new List<DocumentBufferSnapshot>();
        var thirdShell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("third save must not run"),
            buffer => thirdScheduled.Add(buffer.CaptureSnapshot()),
            (_, _) => Task.CompletedTask);
        await thirdShell.OpenAsync(Target("third-closed-window.md"), OpenGesture.Normal, CancellationToken.None);
        Dirty(thirdShell, thirdShell.ActiveTab!, " third dirty");
        thirdScheduled.Clear();
        var thirdWindow = new ShutdownAbortProbe(thirdShell);
        currentWindows.Add(thirdWindow);
        windowSetGeneration++;
        thirdWindow.Close();
        currentWindows.Remove(thirdWindow);
        windowSetGeneration++;
        scenario.CompleteSave();

        Assert.False(await pending);
        ApplicationShutdownAbortCoordinator.AbortCurrentWindows(
            currentWindows,
            static window => window.AbortApplicationShutdown());

        Assert.Equal(0, thirdWindow.AbortCalls);
        Assert.Empty(thirdScheduled);
        Assert.False(thirdShell.HasEditingError);
        Assert.DoesNotContain(thirdWindow, currentWindows);
    }

    [Fact]
    public void Application_abort_uses_one_snapshot_when_the_registry_mutates_during_callbacks()
    {
        var currentWindows = new List<ShutdownAbortProbe>();
        var lateWindow = new ShutdownAbortProbe();
        var closingWindow = new ShutdownAbortProbe();
        var stableWindow = new ShutdownAbortProbe();
        var mutatingWindow = new ShutdownAbortProbe(onAbort: () =>
        {
            closingWindow.Close();
            currentWindows.Remove(closingWindow);
            currentWindows.Add(lateWindow);
        });
        currentWindows.AddRange([mutatingWindow, closingWindow, stableWindow, stableWindow]);

        ApplicationShutdownAbortCoordinator.AbortCurrentWindows(
            currentWindows,
            static window => window.AbortApplicationShutdown());

        Assert.Equal(1, mutatingWindow.AbortCalls);
        Assert.Equal(0, closingWindow.AbortCalls);
        Assert.Equal(1, stableWindow.AbortCalls);
        Assert.Equal(0, lateWindow.AbortCalls);
    }

    [Fact]
    public async Task Application_close_aborts_when_a_clean_tab_is_replaced_during_save()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var original = scenario.SecondShell.ActiveTab!;
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task;

        await scenario.SecondShell.OpenAsync(
            Target("replacement.md"),
            OpenGesture.Normal,
            CancellationToken.None);
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.Same(original, scenario.SecondShell.ActiveTab);
        Assert.EndsWith("replacement.md", original.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(original.Id, scenario.Removed);
    }

    [Fact]
    public async Task Application_close_aborts_when_navigation_ownership_changes_without_replacing_the_buffer()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var tab = scenario.SecondShell.ActiveTab!;
        var buffer = tab.Buffer;
        var revision = tab.Revision;
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scenario.SecondShell.OpenAsync(
            new DocumentTarget(tab.Path, 2, null),
            OpenGesture.Normal,
            CancellationToken.None);
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.Same(buffer, tab.Buffer);
        Assert.True(tab.Revision > revision);
        Assert.DoesNotContain(tab.Id, scenario.Removed);
    }

    [Fact]
    public async Task Application_close_aborts_when_save_generation_changes_with_stable_clean_buffer_state()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync(
            secondSave: (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('c'), buffer.Revision)));
        var tab = scenario.SecondShell.ActiveTab!;
        var buffer = tab.Buffer;
        var revision = tab.Revision;
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scenario.SecondShell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.Same(buffer, tab.Buffer);
        Assert.Equal(revision, tab.Revision);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Application_close_aborts_when_a_clean_tab_closes_during_save()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task;

        scenario.SecondShell.CloseActiveTab();
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.Empty(scenario.SecondShell.Tabs);
        Assert.Single(scenario.Removed);
    }

    [Fact]
    public async Task Application_close_aborts_and_preserves_recovery_when_the_planned_revision_changes_during_save()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task;

        Dirty(scenario.FirstShell, scenario.FirstShell.ActiveTab!, " later edit");
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.True(scenario.FirstShell.ActiveTab!.IsDirty);
        Assert.Equal("text first dirty later edit", scenario.FirstScheduled[^1].Text);
        Assert.DoesNotContain(scenario.SecondShell.ActiveTab!.Id, scenario.Removed);
    }

    [Fact]
    public async Task Application_close_does_not_delete_uncommitted_discard_recovery_after_a_save_checkpoint_mismatch()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync(secondDirty: true);
        var pending = scenario.ResolveAsync(secondChoice: DirtyCloseChoice.Discard);
        await scenario.SaveStarted.Task;

        Dirty(scenario.SecondShell, scenario.SecondShell.ActiveTab!, " changed again");
        scenario.CompleteSave();

        Assert.False(await pending);
        Assert.True(scenario.SecondShell.ActiveTab!.IsDirty);
        Assert.DoesNotContain(scenario.SecondShell.ActiveTab!.Id, scenario.Removed);
        Assert.Equal("text second dirty changed again", scenario.SecondScheduled[^1].Text);
    }

    [Fact]
    public async Task Post_coordinator_application_abort_reschedules_every_current_dirty_recovery()
    {
        var scheduled = new List<DocumentBufferSnapshot>();
        var removed = new List<Guid>();
        var shell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("save must not run"),
            buffer => scheduled.Add(buffer.CaptureSnapshot()),
            (tabId, _) =>
            {
                removed.Add(tabId);
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("discarded-before-flush.md"), OpenGesture.Normal, CancellationToken.None);
        Dirty(shell, shell.ActiveTab!, " still authoritative");
        var tab = shell.ActiveTab!;
        var allowed = await DirtyCloseCoordinator.TryResolveAsync(
            [new DirtyCloseRequest(shell, shell.Tabs, _ => DirtyCloseChoice.Discard)],
            CancellationToken.None);
        scheduled.Clear();

        shell.AbortApplicationShutdown();

        Assert.True(allowed);
        Assert.Equal([tab.Id], removed);
        Assert.Equal("text still authoritative", Assert.Single(scheduled).Text);
        Assert.True(shell.HasEditingError);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Post_coordinator_tab_ownership_allows_hints_but_rejects_a_new_edit_before_approval()
    {
        var shell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("save must not run"),
            _ => { },
            (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target("post-coordinator.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        var ownership = shell.CaptureShutdownOwnership();

        tab.ApplyUiHints(tab.UiHints with { ScrollTop = 31 });
        var afterHint = shell.IsCurrentShutdownOwnership(ownership);
        Dirty(shell, tab, " edit during session flush");

        Assert.True(afterHint);
        Assert.False(shell.IsCurrentShutdownOwnership(ownership));
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Application_close_allows_selection_and_hint_changes_that_preserve_ownership()
    {
        var scenario = await DelayedShutdownScenario.CreateAsync();
        var second = scenario.SecondShell.ActiveTab!;
        var pending = scenario.ResolveAsync();
        await scenario.SaveStarted.Task;

        scenario.SecondShell.ActiveTab = second;
        second.ApplyUiHints(second.UiHints with
        {
            SelectionAnchor = 1,
            SelectionHead = 2,
            ScrollTop = 17,
        });
        scenario.CompleteSave();

        Assert.True(await pending);
        Assert.False(scenario.FirstShell.ActiveTab!.IsDirty);
        Assert.Equal(17, second.UiHints.ScrollTop);
        Assert.DoesNotContain(second.Id, scenario.Removed);
    }

    [Fact]
    public async Task Multi_tab_close_saves_and_discards_in_tab_order_without_writing_discarded_original()
    {
        var operations = new List<string>();
        var shell = CreateShell(
            (buffer, decision, _) =>
            {
                operations.Add($"save:{Path.GetFileName(buffer.Path)}:{decision.GetType().Name}");
                return Task.FromResult<SaveResult>(new SaveResult.Saved(Version('c'), buffer.Revision));
            },
            _ => { },
            (tabId, _) =>
            {
                operations.Add($"discard:{tabId:D}");
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var first = shell.ActiveTab!;
        Dirty(shell, first, "1");
        await shell.OpenAsync(Target("two.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);
        var second = shell.ActiveTab!;
        Dirty(shell, second, "2");

        var allowed = await shell.TryResolveDirtyTabsForCloseAsync(
            shell.Tabs,
            tab => tab == first ? DirtyCloseChoice.Save : DirtyCloseChoice.Discard,
            CancellationToken.None);

        Assert.True(allowed);
        Assert.False(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.Equal(
            [$"save:one.md:Normal", $"discard:{first.Id:D}", $"discard:{second.Id:D}"],
            operations);
    }

    [Fact]
    public async Task Save_conflict_keeps_dirty_close_blocked_and_exposes_a_safe_live_error()
    {
        var shell = CreateShell(
            (_, _, _) => Task.FromResult<SaveResult>(new SaveResult.Conflict(Version('z'))),
            _ => { },
            (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "secret body");

        var allowed = await shell.TryResolveDirtyTabsForCloseAsync(
            [tab],
            _ => DirtyCloseChoice.Save,
            CancellationToken.None);

        Assert.False(allowed);
        Assert.True(tab.IsDirty);
        Assert.True(shell.HasEditingError);
        Assert.DoesNotContain("secret body", shell.EditingErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_one_dirty_tab_is_not_blocked_by_an_unrelated_dirty_tab()
    {
        var shell = CreateShell(
            (_, _, _) => throw new InvalidOperationException("save must not run"),
            _ => { },
            (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var first = shell.ActiveTab!;
        Dirty(shell, first, "1");
        await shell.OpenAsync(Target("two.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);
        var second = shell.ActiveTab!;
        Dirty(shell, second, "2");

        var closed = await shell.TryCloseTabAsync(
            first,
            _ => DirtyCloseChoice.Discard,
            CancellationToken.None);

        Assert.True(closed);
        Assert.DoesNotContain(first, shell.Tabs);
        Assert.Contains(second, shell.Tabs);
        Assert.True(second.IsDirty);
    }

    [Fact]
    public async Task Saving_a_background_tab_during_window_close_does_not_clear_the_active_conflict()
    {
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('c'), buffer.Revision)),
            _ => { },
            (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var first = shell.ActiveTab!;
        Dirty(shell, first, "1");
        await shell.OpenAsync(Target("two.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);
        var second = shell.ActiveTab!;
        Dirty(shell, second, "2");
        await shell.HandleExternalChangeAsync(
            second,
            FileChangeNotice.Changed(second.Path, new LoadedDocument(
                "external",
                new EncodingDescriptor("utf-8", false),
                NewLineKind.Lf,
                "\n",
                Version('z'))),
            CancellationToken.None);
        Assert.True(shell.ConflictBar.IsVisible);

        var allowed = await shell.TryResolveDirtyTabsForCloseAsync(
            [first],
            _ => DirtyCloseChoice.Save,
            CancellationToken.None);

        Assert.True(allowed);
        Assert.True(shell.ConflictBar.IsVisible);
        Assert.Equal(3, shell.ConflictBar.AvailableDecisions.Count);
    }

    [Fact]
    public async Task Read_only_recovery_keeps_the_authoritative_dirty_body_in_read_mode_and_blocks_normal_save()
    {
        var saves = 0;
        var scheduled = new List<DocumentBufferSnapshot>();
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([new ReadOnlyProvider(), new NoteProvider()]),
            (_, _, _) => Task.FromResult(Loaded()),
            (_, _) => Task.CompletedTask,
            saveDocument: (buffer, _, _) =>
            {
                saves++;
                return Task.FromResult<SaveResult>(new SaveResult.Saved(Version('b'), buffer.Revision));
            },
            scheduleRecovery: buffer => scheduled.Add(buffer.CaptureSnapshot()),
            removeRecovery: (_, _) => Task.CompletedTask);
        var recovered = DocumentBuffer.Restore(
            Guid.NewGuid(),
            Target("readonly.md").Path,
            "authoritative recovered body",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            Version('a'),
            7);

        await shell.RestoreRecoveredBuffersAsync([recovered], CancellationToken.None);
        var tab = shell.ActiveTab!;
        var current = new WebResponseContext(Guid.NewGuid(), tab.Id, tab.Revision);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None));
        var closeAllowed = await shell.TryResolveDirtyTabsForCloseAsync(
            [tab],
            _ => DirtyCloseChoice.Save,
            CancellationToken.None);

        Assert.Equal("authoritative recovered body", tab.Text);
        Assert.True(tab.IsDirty);
        Assert.Equal(DocumentMode.Read, tab.Mode);
        Assert.False(tab.CanEdit);
        Assert.False(WindowInputPolicy.CanExecuteModeToggle(tab, current));
        Assert.False(WindowInputPolicy.CanExecuteSave(tab));
        Assert.True(WindowInputPolicy.CanExecuteSaveAs(tab));
        Assert.Equal(0, saves);
        Assert.False(closeAllowed);
        Assert.True(shell.HasEditingError);
        Assert.NotEmpty(scheduled);
        Assert.Equal("authoritative recovered body", scheduled[^1].Text);
    }

    [Fact]
    public async Task Read_only_recovery_can_be_saved_as_an_editable_registered_format()
    {
        var saves = 0;
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([new ReadOnlyProvider(), new NoteProvider()]),
            (_, _, _) => Task.FromResult(Loaded()),
            (_, _) => Task.CompletedTask,
            saveDocument: (buffer, _, _) =>
            {
                saves++;
                return Task.FromResult<SaveResult>(new SaveResult.Saved(Version('b'), buffer.Revision));
            });
        var recovered = DocumentBuffer.Restore(
            Guid.NewGuid(),
            Target("readonly.md").Path,
            "authoritative recovered body",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            Version('a'),
            7);
        await shell.RestoreRecoveredBuffersAsync([recovered], CancellationToken.None);

        await shell.SaveActiveAsync(
            new SaveDecision.SaveAs(Target("editable.note").Path, new EncodingDescriptor("utf-8", false)),
            CancellationToken.None);

        Assert.Equal(1, saves);
        Assert.False(shell.ActiveTab!.IsDirty);
        Assert.True(shell.ActiveTab.CanEdit);
        Assert.EndsWith("editable.note", shell.ActiveTab.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static DocumentTabViewModel LoadedTab(IDocumentFormatProvider provider)
    {
        var tab = new DocumentTabViewModel(Target("policy.md"));
        tab.ApplyLoaded(Loaded(), provider);
        return tab;
    }

    private static ShellViewModel CreateShell(
        Func<DocumentBuffer, SaveDecision, CancellationToken, Task<SaveResult>> save,
        Action<DocumentBuffer> scheduleRecovery,
        Func<Guid, CancellationToken, Task> removeRecovery)
    {
        App.RegisterEncodingProviders();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        return new ShellViewModel(
            registry,
            (_, _, _) => Task.FromResult(Loaded()),
            (_, _) => Task.CompletedTask,
            saveDocument: save,
            scheduleRecovery: scheduleRecovery,
            removeRecovery: removeRecovery);
    }

    private static void Dirty(ShellViewModel shell, DocumentTabViewModel tab, string insertedText) =>
        shell.HandleDocumentChanged(new DocumentChangedMessage(
            new WebMessageOwner(Guid.NewGuid(), shell.WindowId, tab.Id, tab.Revision),
            new DocumentEdit(
                tab.Revision,
                [new TextChange(tab.Text.Length, tab.Text.Length, insertedText)])));

    private static DocumentTarget Target(string name) =>
        new(Path.GetFullPath(name), null, null);

    private static LoadedDocument Loaded() =>
        new(
            "text",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            Version('a'));

    private static DiskFileVersion Version(char value) =>
        new(4, DateTime.UnixEpoch.AddDays(value - 'a'), new string(value, 64));

    private sealed class ReadOnlyProvider : IDocumentFormatProvider
    {
        public DocumentFormatDescriptor Descriptor { get; } = new(
            "readonly",
            [".md"],
            DocumentCapabilities.Read);
    }

    private sealed class NoteProvider : IDocumentFormatProvider
    {
        public DocumentFormatDescriptor Descriptor { get; } = new(
            "note",
            [".note"],
            DocumentCapabilities.Read | DocumentCapabilities.Edit);
    }

    private sealed class DelayedShutdownScenario
    {
        private readonly long savedRevision;
        private readonly TaskCompletionSource<SaveResult> saveCompletion;

        private DelayedShutdownScenario(
            ShellViewModel firstShell,
            ShellViewModel secondShell,
            long savedRevision,
            TaskCompletionSource<SaveResult> saveCompletion,
            TaskCompletionSource saveStarted,
            List<DocumentBufferSnapshot> firstScheduled,
            List<DocumentBufferSnapshot> secondScheduled,
            List<Guid> removed)
        {
            FirstShell = firstShell;
            SecondShell = secondShell;
            this.savedRevision = savedRevision;
            this.saveCompletion = saveCompletion;
            SaveStarted = saveStarted;
            FirstScheduled = firstScheduled;
            SecondScheduled = secondScheduled;
            Removed = removed;
        }

        internal ShellViewModel FirstShell { get; }

        internal ShellViewModel SecondShell { get; }

        internal TaskCompletionSource SaveStarted { get; }

        internal List<DocumentBufferSnapshot> FirstScheduled { get; }

        internal List<DocumentBufferSnapshot> SecondScheduled { get; }

        internal List<Guid> Removed { get; }

        internal static async Task<DelayedShutdownScenario> CreateAsync(
            bool secondDirty = false,
            Func<DocumentBuffer, SaveDecision, CancellationToken, Task<SaveResult>>? secondSave = null)
        {
            var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saveCompletion = new TaskCompletionSource<SaveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstScheduled = new List<DocumentBufferSnapshot>();
            var secondScheduled = new List<DocumentBufferSnapshot>();
            var removed = new List<Guid>();
            var firstShell = CreateShell(
                (_, _, _) =>
                {
                    saveStarted.TrySetResult();
                    return saveCompletion.Task;
                },
                buffer => firstScheduled.Add(buffer.CaptureSnapshot()),
                (tabId, _) =>
                {
                    removed.Add(tabId);
                    return Task.CompletedTask;
                });
            var secondShell = CreateShell(
                secondSave ?? ((_, _, _) => throw new InvalidOperationException("second save must not run")),
                buffer => secondScheduled.Add(buffer.CaptureSnapshot()),
                (tabId, _) =>
                {
                    removed.Add(tabId);
                    return Task.CompletedTask;
                });
            await firstShell.OpenAsync(Target("first-window.md"), OpenGesture.Normal, CancellationToken.None);
            await secondShell.OpenAsync(Target("second-window.md"), OpenGesture.Normal, CancellationToken.None);
            Dirty(firstShell, firstShell.ActiveTab!, " first dirty");
            if (secondDirty)
            {
                Dirty(secondShell, secondShell.ActiveTab!, " second dirty");
            }

            return new DelayedShutdownScenario(
                firstShell,
                secondShell,
                firstShell.ActiveTab!.Revision,
                saveCompletion,
                saveStarted,
                firstScheduled,
                secondScheduled,
                removed);
        }

        internal Task<bool> ResolveAsync(
            Func<bool>? validateGlobalOwnership = null,
            DirtyCloseChoice secondChoice = DirtyCloseChoice.Discard) =>
            DirtyCloseCoordinator.TryResolveAsync(
                [
                    new DirtyCloseRequest(FirstShell, FirstShell.Tabs, _ => DirtyCloseChoice.Save),
                    new DirtyCloseRequest(SecondShell, SecondShell.Tabs, _ => secondChoice),
                ],
                validateGlobalOwnership ?? (() => true),
                CancellationToken.None);

        internal void CompleteSave() =>
            saveCompletion.SetResult(new SaveResult.Saved(Version('b'), savedRevision));
    }

    private sealed class ShutdownAbortProbe(
        ShellViewModel? shell = null,
        Action? onAbort = null)
    {
        private bool closed;

        internal int AbortCalls { get; private set; }

        internal void AbortApplicationShutdown()
        {
            if (closed)
            {
                return;
            }

            AbortCalls++;
            onAbort?.Invoke();
            shell?.AbortApplicationShutdown();
        }

        internal void Close() => closed = true;
    }
}
