using System.Text.Json;
using MarkUpViewMini.App.Composition;
using MarkUpViewMini.App.Services;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Folders;
using MarkUpViewMini.Infrastructure.Recovery;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.Tests;

public sealed class SessionRestoreTests
{
    public SessionRestoreTests() => App.RegisterEncodingProviders();

    [Fact]
    public void App_session_mutation_policy_keeps_startup_exit_and_last_close_automatic()
    {
        // Break caught: capture callers themselves are mistaken for user changes and overwrite a future session schema.
        var tracker = new AppSessionMutationTracker();
        tracker.BeginStartup();

        Assert.Equal(SessionSaveReason.AutomaticRestore, tracker.RecordMutation());
        tracker.CompleteStartup();
        Assert.Equal(SessionSaveReason.AutomaticRestore, tracker.CaptureWithoutMutation());
        Assert.Equal(SessionSaveReason.AutomaticRestore, tracker.CaptureLastWindowClose());
        Assert.Equal(0, tracker.MutationGeneration);
        Assert.Equal(SessionSaveReason.UserMutation, tracker.RecordMutation());
        Assert.Equal(1, tracker.MutationGeneration);
    }

    [Fact]
    public async Task Startup_runs_settings_recovery_session_then_appends_command_line_targets()
    {
        // Break caught: session tabs are restored before recovery decisions or command-line input replaces them.
        var order = new List<string>();
        var restored = new RecordingWindow(order);
        var coordinator = new SessionStartupCoordinator(
            _ => AddAsync("settings"),
            new DelegateRecoveryResolver(_ =>
            {
                order.Add("recovery");
                return Task.FromResult(RecoveryStartupResolution.Completed());
            }),
            _ =>
            {
                order.Add("session-load");
                return Task.FromResult(new SessionLoadResult(
                    new SessionV1 { Windows = [new SessionWindowV1 { WindowId = Guid.NewGuid() }] },
                    0));
            },
            _ => restored,
            count => order.Add($"summary:{count}"));

        var windows = await coordinator.StartAsync(
            ["command.md"],
            Path.GetFullPath("base"),
            CancellationToken.None);

        Assert.Same(restored, Assert.Single(windows));
        Assert.Equal(
            ["settings", "recovery", "session-load", "restore", "command:command.md"],
            order);

        Task AddAsync(string item)
        {
            order.Add(item);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Restore_skips_failed_windows_and_reports_one_combined_nonmodal_summary()
    {
        // Break caught: one missing/unsupported tab aborts later windows or emits repeated sensitive error messages.
        var summaries = new List<int>();
        var first = new RecordingWindow([], restoredSkips: 2);
        var failed = new RecordingWindow([], failure: new IOException("private body"));
        var last = new RecordingWindow([], restoredSkips: 1);
        var queue = new Queue<ISessionWindow>([first, failed, last]);
        var coordinator = new SessionStartupCoordinator(
            _ => Task.CompletedTask,
            new DelegateRecoveryResolver(_ => Task.FromResult(RecoveryStartupResolution.Completed())),
            _ => Task.FromResult(new SessionLoadResult(
                new SessionV1
                {
                    Windows =
                    [
                        new SessionWindowV1 { WindowId = Guid.NewGuid() },
                        new SessionWindowV1 { WindowId = Guid.NewGuid() },
                        new SessionWindowV1 { WindowId = Guid.NewGuid() },
                    ],
                },
                4)),
            _ => queue.Dequeue(),
            summaries.Add);

        var windows = await coordinator.StartAsync([], null, CancellationToken.None);

        Assert.Equal(2, windows.Count);
        Assert.Equal([8], summaries);
        Assert.Equal(1, first.CommitCount);
        Assert.Equal(0, first.AbandonCount);
        Assert.Equal(0, failed.CommitCount);
        Assert.Equal(1, failed.AbandonCount);
        Assert.Equal(1, last.CommitCount);
    }

    [Fact]
    public async Task Empty_session_creates_one_window_before_command_line_append()
    {
        // Break caught: a first launch with no session has nowhere to append explicit command-line tabs.
        var window = new RecordingWindow([]);
        var coordinator = new SessionStartupCoordinator(
            _ => Task.CompletedTask,
            new DelegateRecoveryResolver(_ => Task.FromResult(RecoveryStartupResolution.Completed())),
            _ => Task.FromResult(new SessionLoadResult(SessionV1.CreateDefault(), 0)),
            _ => window,
            _ => { });

        var windows = await coordinator.StartAsync(["one.md", "two.md"], null, CancellationToken.None);

        Assert.Same(window, Assert.Single(windows));
        Assert.Equal(["one.md", "two.md"], window.CommandLineArguments);
    }

    [Fact]
    public async Task Session_load_stays_blocked_until_every_recovery_decision_completes()
    {
        // Break caught: merely enumerating recovery records lets normal session restore race unresolved dirty content.
        var release = new TaskCompletionSource<RecoveryStartupResolution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionLoadStarted = false;
        var window = new RecordingWindow([]);
        var coordinator = new SessionStartupCoordinator(
            _ => Task.CompletedTask,
            new DelegateRecoveryResolver(_ => release.Task),
            _ =>
            {
                sessionLoadStarted = true;
                return Task.FromResult(new SessionLoadResult(SessionV1.CreateDefault(), 0));
            },
            _ => window,
            _ => { });

        var startup = coordinator.StartAsync([], null, CancellationToken.None);
        await Task.Yield();
        Assert.False(sessionLoadStarted);
        Assert.False(startup.IsCompleted);

        release.SetResult(RecoveryStartupResolution.Completed());
        await startup;

        Assert.True(sessionLoadStarted);
    }

    [Fact]
    public async Task Recovery_cancellation_aborts_session_and_command_line_without_constructing_a_window()
    {
        // Break caught: canceling an unresolved recovery prompt silently continues and opens clean/session documents.
        var sessionLoads = 0;
        var creates = 0;
        var coordinator = new SessionStartupCoordinator(
            _ => Task.CompletedTask,
            new DelegateRecoveryResolver(_ => Task.FromResult(RecoveryStartupResolution.Cancelled())),
            _ =>
            {
                sessionLoads++;
                return Task.FromResult(new SessionLoadResult(SessionV1.CreateDefault(), 0));
            },
            _ =>
            {
                creates++;
                return new RecordingWindow([]);
            },
            _ => { });

        var windows = await coordinator.StartAsync(["command.md"], null, CancellationToken.None);

        Assert.Empty(windows);
        Assert.Equal(0, sessionLoads);
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task Recovered_buffer_is_sent_to_the_window_that_owned_its_session_tab()
    {
        // Break caught: all recovery buffers are attached to the first window, breaking multi-window tab ownership.
        var firstWindow = new RecordingWindow([]);
        var secondWindow = new RecordingWindow([]);
        var queue = new Queue<ISessionWindow>([firstWindow, secondWindow]);
        var recovered = DocumentBuffer.Restore(
            Guid.NewGuid(),
            Path.GetFullPath("owned-by-second.md"),
            "dirty",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(1, DateTime.UnixEpoch, new string('d', 64)),
            4);
        var firstState = new SessionWindowV1
        {
            WindowId = Guid.NewGuid(),
            Tabs = [SessionTab("first.md", DocumentMode.Read, 0, 0, 0)],
        };
        var secondState = new SessionWindowV1
        {
            WindowId = Guid.NewGuid(),
            Tabs = [SessionTab("owned-by-second.md", DocumentMode.Read, 0, 0, 0) with { TabId = recovered.TabId }],
        };
        var coordinator = new SessionStartupCoordinator(
            _ => Task.CompletedTask,
            new DelegateRecoveryResolver(_ => Task.FromResult(
                RecoveryStartupResolution.Completed([recovered]))),
            _ => Task.FromResult(new SessionLoadResult(
                new SessionV1 { Windows = [firstState, secondState] },
                0)),
            _ => queue.Dequeue(),
            _ => { });

        await coordinator.StartAsync([], null, CancellationToken.None);

        Assert.Empty(firstWindow.RecoveredBuffers);
        Assert.Equal(recovered.TabId, Assert.Single(secondWindow.RecoveredBuffers).TabId);
        Assert.False(secondWindow.WasCommittedWhenRecoveryAttached);
    }

    [Fact]
    public async Task Unsupported_recovery_is_skipped_before_commit_and_later_valid_recovery_continues()
    {
        // Break caught: a bad recovery extension exposes a committed orphan or prevents later recovery records/windows.
        var unsupported = RecoveredBuffer("old.txt", "unsupported");
        var valid = RecoveredBuffer("valid.md", "valid dirty");
        var window = new SelectiveRecoveryWindow();
        var summaries = new List<int>();
        var coordinator = new SessionStartupCoordinator(
            _ => Task.CompletedTask,
            new DelegateRecoveryResolver(_ => Task.FromResult(
                RecoveryStartupResolution.Completed([unsupported, valid]))),
            _ => Task.FromResult(new SessionLoadResult(SessionV1.CreateDefault(), 0)),
            _ => window,
            summaries.Add);

        var result = await coordinator.StartAsync([], null, CancellationToken.None);

        Assert.Same(window, Assert.Single(result));
        Assert.Equal([valid.TabId], window.Attached.Select(buffer => buffer.TabId));
        Assert.Equal(1, window.CommitCount);
        Assert.Equal(0, window.AbandonCount);
        Assert.False(window.WasCommittedWhenRecoveryAttached);
        Assert.Equal([1], summaries);
    }

    [Fact]
    public async Task Fatal_recovery_surface_rollback_abandons_every_hidden_candidate_before_commit()
    {
        // Break caught: a poisoned hidden surface is committed after rollback itself fails.
        var first = new RecordingWindow(
            [],
            recoveryFailure: new RecoverySurfaceRollbackException(
                new InvalidOperationException("activation"),
                new InvalidOperationException("rollback")));
        var second = new RecordingWindow([]);
        var queue = new Queue<ISessionWindow>([first, second]);
        var recovered = RecoveredBuffer("fatal.md", "dirty");
        var coordinator = new SessionStartupCoordinator(
            _ => Task.CompletedTask,
            new DelegateRecoveryResolver(_ => Task.FromResult(
                RecoveryStartupResolution.Completed([recovered]))),
            _ => Task.FromResult(new SessionLoadResult(
                new SessionV1
                {
                    Windows =
                    [
                        new SessionWindowV1 { WindowId = Guid.NewGuid() },
                        new SessionWindowV1 { WindowId = Guid.NewGuid() },
                    ],
                },
                0)),
            _ => queue.Dequeue(),
            _ => { });

        await Assert.ThrowsAsync<RecoverySurfaceRollbackException>(() =>
            coordinator.StartAsync([], null, CancellationToken.None));

        Assert.Equal(0, first.CommitCount);
        Assert.Equal(1, first.AbandonCount);
        Assert.Equal(0, second.CommitCount);
        Assert.Equal(1, second.AbandonCount);
    }

    [Fact]
    public async Task Compare_remains_pending_then_restore_is_dirty_while_use_original_removes_after_read()
    {
        // Break caught: Compare resolves/destructively mutates a record, or Use Original removes before confirming the file.
        const string recoveredSecret = "RECOVERED-BODY-SECRET";
        var restoreRecord = Recovery("restore.md", recoveredSecret);
        var originalRecord = Recovery("original.md", "other recovered");
        var dialog = new ScriptedRecoveryDialog(
            RecoveryDecisionKind.Compare,
            RecoveryDecisionKind.Restore,
            RecoveryDecisionKind.UseOriginal);
        var operations = new List<string>();
        var resolver = new RecoveryDecisionResolver(
            _ => Task.FromResult<IReadOnlyList<RecoveryRecord>>([restoreRecord, originalRecord]),
            dialog,
            (path, _) =>
            {
                operations.Add($"read:{Path.GetFileName(path)}");
                return Task.FromResult($"original:{Path.GetFileName(path)}");
            },
            (tabId, _) =>
            {
                operations.Add($"remove:{tabId}");
                return Task.CompletedTask;
            });

        var result = await resolver.ResolveAsync(CancellationToken.None);

        Assert.False(result.IsCancelled);
        var buffer = Assert.Single(result.RestoredBuffers);
        Assert.True(buffer.IsDirty);
        Assert.Equal(recoveredSecret, buffer.Text);
        var comparison = Assert.Single(dialog.Comparisons);
        Assert.True(comparison.Recovered.IsReadOnly);
        Assert.True(comparison.Original.IsReadOnly);
        Assert.Equal(
            [
                "read:restore.md",
                "read:original.md",
                $"remove:{originalRecord.TabId}",
            ],
            operations);
    }

    [Fact]
    public async Task Recovered_buffer_replaces_the_matching_clean_session_tab_without_changing_tab_order()
    {
        // Break caught: normal session creates the same TabId first and causes the chosen dirty recovery body to be skipped.
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) => Task.FromResult(Loaded(path)),
            (_, _) => Task.CompletedTask);
        var first = SessionTab("recover-match.md", DocumentMode.Read, 2, 3, 8);
        var second = SessionTab("second.md", DocumentMode.Read, 0, 0, 0);
        await shell.RestoreSessionTabAsync(first, CancellationToken.None);
        await shell.RestoreSessionTabAsync(second, CancellationToken.None);
        var recovered = DocumentBuffer.Restore(
            first.TabId,
            first.Path,
            "chosen dirty body",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(1, DateTime.UnixEpoch, new string('c', 64)),
            9);

        await shell.RestoreRecoveredBuffersAsync([recovered], CancellationToken.None);

        Assert.Equal([first.TabId, second.TabId], shell.Tabs.Select(tab => tab.Id));
        Assert.Equal("chosen dirty body", shell.Tabs[0].Text);
        Assert.True(shell.Tabs[0].IsDirty);
        Assert.Equal(9, shell.Tabs[0].Revision);
    }

    [Fact]
    public async Task Failed_recovery_activation_rolls_back_the_hidden_shell_mutation()
    {
        // Break caught: a recovery activation failure leaves a partial hidden tab that later becomes visible on Commit.
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            (path, _, _) => Task.FromResult(Loaded(path)),
            (_, _) => Task.FromException(new InvalidOperationException("surface unavailable")));
        var recovered = RecoveredBuffer("activation.md", "dirty");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            shell.RestoreRecoveredBuffersAsync([recovered], CancellationToken.None));

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.ActiveTab);
    }

    [Fact]
    public async Task Failed_recovery_surface_switch_deactivates_then_rehydrates_the_exact_previous_owner()
    {
        // Break caught: model rollback leaves the web surface on a partly switched recovery document.
        var events = new List<string>();
        var activationCount = 0;
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) => Task.FromResult(Loaded(path)),
            (tab, _) =>
            {
                activationCount++;
                events.Add($"activate:{Path.GetFileName(tab.Path)}:{tab.Revision}:{tab.UiHints.ScrollTop}");
                return activationCount == 4
                    ? Task.FromException(new InvalidOperationException("switched then failed"))
                    : Task.CompletedTask;
            },
            () => events.Add("deactivate"));
        var first = SessionTab("first.md", DocumentMode.Read, 2, 4, 27);
        var target = SessionTab("target.md", DocumentMode.Read, 3, 5, 39);
        var firstTab = await shell.RestoreSessionTabAsync(first, CancellationToken.None);
        var targetTab = await shell.RestoreSessionTabAsync(target, CancellationToken.None);
        await shell.ActivateAsync(firstTab!, CancellationToken.None);
        events.Clear();
        var recovered = DocumentBuffer.Restore(
            target.TabId,
            target.Path,
            "dirty replacement",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(1, DateTime.UnixEpoch, new string('f', 64)),
            12);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            shell.RestoreRecoveredBuffersAsync([recovered], CancellationToken.None));

        Assert.Same(firstTab, shell.ActiveTab);
        Assert.Equal("loaded:target.md", targetTab!.Text);
        Assert.False(targetTab.IsDirty);
        Assert.Equal(
            [
                "activate:target.md:12:39",
                "deactivate",
                $"activate:first.md:{firstTab!.Revision}:27",
            ],
            events);
    }

    [Fact]
    public async Task Failed_first_recovery_activation_deactivates_surface_without_a_phantom_owner()
    {
        // Break caught: no-prior-tab failure leaves a partial surface active after model rollback.
        var events = new List<string>();
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            (path, _, _) => Task.FromResult(Loaded(path)),
            (tab, _) =>
            {
                events.Add($"activate:{Path.GetFileName(tab.Path)}");
                return Task.FromException(new InvalidOperationException("surface failed"));
            },
            () => events.Add("deactivate"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RestoreRecoveredBuffersAsync(
            [RecoveredBuffer("only.md", "dirty")],
            CancellationToken.None));

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.ActiveTab);
        Assert.Equal(["activate:only.md", "deactivate"], events);
    }

    [Fact]
    public async Task Failed_new_recovery_tab_rehydrates_the_previous_owner_and_removes_all_tab_ownership()
    {
        // Break caught: a new recovery tab rollback restores the model selection but not the prior surface owner.
        var events = new List<string>();
        var activationCount = 0;
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            (path, _, _) => Task.FromResult(Loaded(path)),
            (tab, _) =>
            {
                activationCount++;
                events.Add($"activate:{Path.GetFileName(tab.Path)}:{tab.Revision}");
                return activationCount == 2
                    ? Task.FromException(new InvalidOperationException("switched then failed"))
                    : Task.CompletedTask;
            },
            () => events.Add("deactivate"));
        var owner = await shell.RestoreSessionTabAsync(
            SessionTab("owner.md", DocumentMode.Read, 0, 0, 7),
            CancellationToken.None);
        events.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RestoreRecoveredBuffersAsync(
            [RecoveredBuffer("new.md", "dirty")],
            CancellationToken.None));

        Assert.Same(owner, Assert.Single(shell.Tabs));
        Assert.Same(owner, shell.ActiveTab);
        Assert.Equal(
            [
                "activate:new.md:3",
                "deactivate",
                $"activate:owner.md:{owner!.Revision}",
            ],
            events);
    }

    [Fact]
    public async Task Failed_previous_owner_rehydrate_marks_the_hidden_candidate_fatal()
    {
        // Break caught: failed rollback rehydrate is downgraded to a safe per-record skip.
        var activationCount = 0;
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            (path, _, _) => Task.FromResult(Loaded(path)),
            (_, _) =>
            {
                activationCount++;
                return activationCount >= 4
                    ? Task.FromException(new InvalidOperationException("surface unavailable"))
                    : Task.CompletedTask;
            });
        var first = SessionTab("owner.md", DocumentMode.Read, 0, 0, 8);
        var target = SessionTab("target.md", DocumentMode.Read, 0, 0, 9);
        var owner = await shell.RestoreSessionTabAsync(first, CancellationToken.None);
        await shell.RestoreSessionTabAsync(target, CancellationToken.None);
        await shell.ActivateAsync(owner!, CancellationToken.None);
        var recovered = DocumentBuffer.Restore(
            target.TabId,
            target.Path,
            "dirty replacement",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(1, DateTime.UnixEpoch, new string('f', 64)),
            12);

        await Assert.ThrowsAsync<RecoverySurfaceRollbackException>(() =>
            shell.RestoreRecoveredBuffersAsync([recovered], CancellationToken.None));

        Assert.Same(owner, shell.ActiveTab);
    }

    [Fact]
    public async Task Failed_surface_deactivation_marks_the_hidden_candidate_fatal_and_restores_model()
    {
        // Break caught: a deactivation failure leaves uncertain surface ownership but is treated as recoverable.
        var shell = new ShellViewModel(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            (path, _, _) => Task.FromResult(Loaded(path)),
            (_, _) => Task.FromException(new InvalidOperationException("activation failed")),
            () => throw new InvalidOperationException("deactivation failed"));
        var recovered = RecoveredBuffer("only.md", "dirty");

        await Assert.ThrowsAsync<RecoverySurfaceRollbackException>(() =>
            shell.RestoreRecoveredBuffersAsync([recovered], CancellationToken.None));

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.ActiveTab);
    }

    [Fact]
    public async Task Window_restore_uses_registry_and_shell_generation_then_applies_exact_session_state()
    {
        // Break caught: restore bypasses Shell ownership, keeps failed tabs, or loses IDs/order/active/history/hints.
        var loaded = new List<string>();
        var activated = new List<Guid>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) =>
            {
                loaded.Add(path);
                return Task.FromResult(Loaded(path));
            },
            (tab, _) =>
            {
                activated.Add(tab.Id);
                return Task.CompletedTask;
            });
        var appliedLayouts = new List<SessionWindowLayoutV1>();
        var controller = new SessionWindowStateController(
            shell,
            path => !path.EndsWith("missing.md", StringComparison.OrdinalIgnoreCase),
            () => SessionWindowLayoutV1.CreateDefault(),
            appliedLayouts.Add);
        var first = SessionTab("first.md", DocumentMode.Edit, 12, 14, 77);
        var missing = SessionTab("missing.md", DocumentMode.Read, 0, 0, 0);
        var unsupported = SessionTab("unsupported.txt", DocumentMode.Read, 0, 0, 0);
        var second = SessionTab("second.md", DocumentMode.Read, 2, 3, 8);
        var state = new SessionWindowV1
        {
            WindowId = Guid.NewGuid(),
            Tabs = [first, missing, unsupported, second],
            ActiveTabId = first.TabId,
            Layout = new SessionWindowLayoutV1(40, 50, 900, 700, true),
        };

        var skipped = await controller.RestoreAsync(state, CancellationToken.None);

        Assert.Equal(2, skipped);
        Assert.Equal([first.Path, second.Path], loaded);
        Assert.Equal([first.TabId, second.TabId], shell.Tabs.Select(tab => tab.Id));
        Assert.Equal(first.TabId, shell.ActiveTab?.Id);
        Assert.Equal(DocumentMode.Edit, shell.Tabs[0].Mode);
        Assert.Equal(new DocumentUiHints(12, 14, 77, 0.6), shell.Tabs[0].UiHints);
        var history = shell.Tabs[0].NavigationHistory.Capture();
        Assert.Equal(1, history.CurrentIndex);
        Assert.Equal(["history.md", "first.md"], history.Entries.Select(entry => Path.GetFileName(entry.Path)));
        Assert.Equal(state.Layout, Assert.Single(appliedLayouts));
        Assert.True(activated.Count >= 3);
    }

    [Fact]
    public async Task Window_capture_excludes_dirty_body_and_find_search_queries()
    {
        // Break caught: session capture serializes the C# authoritative dirty buffer or tab-local find/search query.
        const string secret = "DIRTY-BODY-SECRET";
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) => Task.FromResult(Loaded(path)),
            (_, _) => Task.CompletedTask);
        await shell.OpenAsync(
            new DocumentTarget(Path.GetFullPath("dirty.md"), null, null),
            OpenGesture.Normal,
            CancellationToken.None);
        var tab = shell.ActiveTab!;
        tab.ApplyEdit(new DocumentEdit(tab.Revision, [new TextChange(0, tab.Text.Length, secret)]));
        var controller = new SessionWindowStateController(
            shell,
            _ => true,
            () => new SessionWindowLayoutV1(1, 2, 800, 600, false),
            _ => { });

        var captured = controller.Capture(shell.WindowId);
        var json = JsonSerializer.Serialize(captured, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("query", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tab.Id, Assert.Single(captured.Tabs).TabId);
    }

    [Fact]
    public async Task Command_line_documents_append_after_an_existing_restored_tab()
    {
        // Break caught: the first explicit command-line file replaces the clean active tab restored from session.json.
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) => Task.FromResult(Loaded(path)),
            (_, _) => Task.CompletedTask);
        await shell.OpenAsync(
            new DocumentTarget(Path.GetFullPath("restored.md"), null, null),
            OpenGesture.Normal,
            CancellationToken.None);

        await shell.OpenCommandLineTargetsAsync(
            ["command.md"],
            Path.GetFullPath("base"),
            CancellationToken.None);

        Assert.Equal(
            ["restored.md", "command.md"],
            shell.Tabs.Select(tab => Path.GetFileName(tab.Path)));
        Assert.Equal("command.md", Path.GetFileName(shell.ActiveTab?.Path));
    }

    [Fact]
    public async Task Saved_root_wins_after_follow_current_document_side_effects_during_restore()
    {
        // Break caught: loading tabs in FollowCurrentDocument mode overwrites the exact root restored from session.json.
        using var sidebar = new SidebarViewModel(
            new FolderTreeService(),
            new EmptySearchService(),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            action => action());
        sidebar.RootMode = RootFollowMode.FollowCurrentDocument;
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) => Task.FromResult(Loaded(path)),
            (_, _) => Task.CompletedTask,
            sidebar: sidebar);
        var controller = new SessionWindowStateController(
            shell,
            _ => true,
            SessionWindowLayoutV1.CreateDefault,
            _ => { });
        var savedRoot = Path.GetFullPath("saved-root");
        var state = new SessionWindowV1
        {
            WindowId = Guid.NewGuid(),
            RootPath = savedRoot,
            Tabs = [SessionTab(Path.Combine("other-root", "document.md"), DocumentMode.Read, 0, 0, 0)],
        };

        await controller.RestoreAsync(state, CancellationToken.None);

        Assert.Equal(savedRoot, sidebar.RootPath);
    }

    [Fact]
    public void Close_capture_excludes_one_of_multiple_windows_but_retains_the_last_window_for_restart()
    {
        // Break caught: closing one window resurrects it next launch, or closing the last window erases restart state.
        var first = new SessionWindowV1 { WindowId = Guid.NewGuid() };
        var second = new SessionWindowV1 { WindowId = Guid.NewGuid() };

        var afterFirstClose = SessionCloseCapture.Create([first, second], first.WindowId);
        var afterLastClose = SessionCloseCapture.Create([second], second.WindowId);

        Assert.Equal([second.WindowId], afterFirstClose.Windows.Select(window => window.WindowId));
        Assert.Equal([second.WindowId], afterLastClose.Windows.Select(window => window.WindowId));
    }

    private static SessionTabV1 SessionTab(
        string path,
        DocumentMode mode,
        int anchor,
        int head,
        double scroll) =>
        new()
        {
            TabId = Guid.NewGuid(),
            Path = Path.GetFullPath(path),
            Mode = mode,
            Hints = new SessionEditorHintsV1(anchor, head, scroll, 0.6),
            History =
            [
                new SessionNavigationEntryV1(Path.GetFullPath("history.md"), 2, null, DocumentMode.Read, 1),
                new SessionNavigationEntryV1(Path.GetFullPath(path), null, "anchor", mode, scroll),
            ],
            HistoryIndex = 1,
        };

    private static LoadedDocument Loaded(string path) => new(
        $"loaded:{Path.GetFileName(path)}",
        new EncodingDescriptor("utf-8", false),
        NewLineKind.Lf,
        "\n",
        new DiskFileVersion(1, DateTime.UnixEpoch, new string('a', 64)));

    private static RecoveryRecord Recovery(string path, string body) => new(
        RecoveryRecord.CurrentSchemaVersion,
        Guid.NewGuid(),
        Path.GetFullPath(path),
        new DiskFileVersion(1, DateTime.UnixEpoch, new string('b', 64)),
        new EncodingDescriptor("utf-8", false),
        NewLineKind.Lf,
        "\n",
        7,
        DateTime.UnixEpoch,
        RecoveryRecord.EncodeBody(body));

    private static DocumentBuffer RecoveredBuffer(string path, string body) =>
        DocumentBuffer.Restore(
            Guid.NewGuid(),
            Path.GetFullPath(path),
            body,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(1, DateTime.UnixEpoch, new string('e', 64)),
            3);

    private sealed class EmptySearchService : IDocumentSearchService
    {
        public async IAsyncEnumerable<SearchEvent> SearchAsync(
            SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new SearchSummary(query.RequestId, 0, 0, 0, false);
        }
    }

    private sealed class RecordingWindow(
        List<string> order,
        int restoredSkips = 0,
        Exception? failure = null,
        Exception? recoveryFailure = null) : ISessionWindow
    {
        public List<string> CommandLineArguments { get; } = [];

        public int CommitCount { get; private set; }

        public int AbandonCount { get; private set; }

        public List<DocumentBuffer> RecoveredBuffers { get; } = [];

        public bool WasCommittedWhenRecoveryAttached { get; private set; }

        public void Commit() => CommitCount++;

        public void Abandon() => AbandonCount++;

        public Task RestoreRecoveredAsync(
            IReadOnlyList<DocumentBuffer> buffers,
            CancellationToken cancellationToken)
        {
            WasCommittedWhenRecoveryAttached |= CommitCount > 0;
            RecoveredBuffers.AddRange(buffers);
            return recoveryFailure is null
                ? Task.CompletedTask
                : Task.FromException(recoveryFailure);
        }

        public Task<int> RestoreAsync(SessionWindowV1 state, CancellationToken cancellationToken)
        {
            order.Add("restore");
            return failure is null
                ? Task.FromResult(restoredSkips)
                : Task.FromException<int>(failure);
        }

        public Task OpenCommandLineTargetsAsync(
            IReadOnlyList<string> arguments,
            string? baseDirectory,
            CancellationToken cancellationToken)
        {
            CommandLineArguments.AddRange(arguments);
            foreach (var argument in arguments)
            {
                order.Add($"command:{argument}");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SelectiveRecoveryWindow : ISessionWindow
    {
        public List<DocumentBuffer> Attached { get; } = [];

        public int CommitCount { get; private set; }

        public int AbandonCount { get; private set; }

        public bool WasCommittedWhenRecoveryAttached { get; private set; }

        public void Commit() => CommitCount++;

        public void Abandon() => AbandonCount++;

        public Task<int> RestoreAsync(SessionWindowV1 state, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task RestoreRecoveredAsync(
            IReadOnlyList<DocumentBuffer> buffers,
            CancellationToken cancellationToken)
        {
            WasCommittedWhenRecoveryAttached |= CommitCount > 0;
            var buffer = Assert.Single(buffers);
            if (Path.GetExtension(buffer.Path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("private recovery body");
            }

            Attached.Add(buffer);
            return Task.CompletedTask;
        }

        public Task OpenCommandLineTargetsAsync(
            IReadOnlyList<string> arguments,
            string? baseDirectory,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DelegateRecoveryResolver(
        Func<CancellationToken, Task<RecoveryStartupResolution>> resolve) : IRecoveryDecisionResolver
    {
        public Task<RecoveryStartupResolution> ResolveAsync(CancellationToken cancellationToken) =>
            resolve(cancellationToken);
    }

    private sealed class ScriptedRecoveryDialog(params RecoveryDecisionKind[] choices) : IRecoveryDecisionDialog
    {
        private readonly Queue<RecoveryDecisionKind> remaining = new(choices);

        public List<RecoveryComparisonViewModel> Comparisons { get; } = [];

        public Task<RecoveryDecisionKind> ChooseAsync(
            RecoveryPromptViewModel prompt,
            RecoveryComparisonViewModel? comparison,
            CancellationToken cancellationToken)
        {
            if (comparison is not null)
            {
                Comparisons.Add(comparison);
            }

            return Task.FromResult(remaining.Dequeue());
        }

        public void ShowOriginalReadError() => throw new InvalidOperationException();
    }
}
