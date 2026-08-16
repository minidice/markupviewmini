using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class ShellSaveTests
{
    [Fact]
    public async Task Save_completion_clears_only_the_saved_revision_and_notifies_the_exact_active_owner()
    {
        var notifications = new List<(Guid TabId, long Revision)>();
        var version = Version('b');
        var shell = CreateShell(
            (buffer, decision, token) =>
                Task.FromResult<SaveResult>(new SaveResult.Saved(version, buffer.Revision)),
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");

        var result = await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);

        Assert.IsType<SaveResult.Saved>(result);
        Assert.False(tab.IsDirty);
        Assert.Equal(version, tab.DiskVersion);
        Assert.Equal([(tab.Id, tab.Revision)], notifications);
    }

    [Fact]
    public async Task Background_save_completion_updates_its_owner_but_never_posts_to_the_visible_tab()
    {
        var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<(Guid TabId, long Revision)>();
        var shell = CreateShell(
            (buffer, decision, token) => saving.Task,
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var savedTab = shell.ActiveTab!;
        Dirty(shell, savedTab, "!");
        var savedRevision = savedTab.Revision;
        var pending = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        await shell.OpenAsync(Target("two.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);

        saving.SetResult(new SaveResult.Saved(Version('c'), savedRevision));
        await pending;

        Assert.False(savedTab.IsDirty);
        Assert.Equal("two.md", shell.ActiveTab!.DisplayTitle);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Later_edit_keeps_dirty_and_suppresses_stale_save_completed()
    {
        var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<(Guid TabId, long Revision)>();
        var shell = CreateShell(
            (buffer, decision, token) => saving.Task,
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " first");
        var savedRevision = tab.Revision;
        var pending = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        Dirty(shell, tab, " later");

        saving.SetResult(new SaveResult.Saved(Version('d'), savedRevision));
        await pending;

        Assert.True(tab.IsDirty);
        Assert.Equal(Version('d'), tab.DiskVersion);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task SaveAs_changes_path_and_title_only_after_success()
    {
        var completion = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider(), new NoteProvider()]);
        var shell = CreateShell((buffer, decision, token) => completion.Task, registry: registry);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");
        var target = Path.GetFullPath("renamed.note");
        var decision = new SaveDecision.SaveAs(target, new EncodingDescriptor("utf-16", true));

        var pending = shell.SaveActiveAsync(decision, CancellationToken.None);
        Assert.EndsWith("one.md", tab.Path, StringComparison.OrdinalIgnoreCase);
        completion.SetResult(new SaveResult.Saved(Version('e'), tab.Revision));
        await pending;

        Assert.Equal(target, tab.Path);
        Assert.Equal("renamed.note", tab.DisplayTitle);
        Assert.Equal(new EncodingDescriptor("utf-16", true), tab.Encoding);
        Assert.Equal("note", tab.FormatProvider?.Descriptor.Id);
    }

    [Fact]
    public async Task Exact_current_SaveAs_records_the_normalized_target_once_and_refreshes_recent_documents()
    {
        var original = Path.GetFullPath("one.md");
        var target = Path.GetFullPath("renamed.note");
        var recorded = new List<string>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider(), new NoteProvider()]);
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(new SaveResult.Saved(Version('e'), buffer.Revision)),
            registry: registry,
            recordSuccessfulOpen: path =>
            {
                recorded.Add(path);
                return
                [
                    new RecentDocumentEntry(path),
                    new RecentDocumentEntry(original),
                ];
            });
        await shell.OpenAsync(Target(original), OpenGesture.Normal, CancellationToken.None);
        recorded.Clear();
        Dirty(shell, shell.ActiveTab!, "!");

        await shell.SaveActiveAsync(
            new SaveDecision.SaveAs(target, new EncodingDescriptor("utf-16", true)),
            CancellationToken.None);

        Assert.Equal([target], recorded);
        Assert.Equal([target, original], shell.RecentDocuments.Select(entry => entry.Path));
    }

    [Fact]
    public async Task Conflict_and_stale_SaveAs_completions_do_not_record_recent_documents()
    {
        var recorded = new List<string>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider(), new NoteProvider()]);
        var conflictShell = CreateShell(
            (_, _, _) => Task.FromResult<SaveResult>(new SaveResult.Conflict(Version('z'))),
            registry: registry,
            recordSuccessfulOpen: path =>
            {
                recorded.Add(path);
                return [];
            });
        await conflictShell.OpenAsync(Target("conflict.md"), OpenGesture.Normal, CancellationToken.None);
        recorded.Clear();
        await conflictShell.SaveActiveAsync(
            new SaveDecision.SaveAs("conflict.note", new EncodingDescriptor("utf-8", false)),
            CancellationToken.None);

        var completion = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleShell = CreateShell(
            (_, _, _) => completion.Task,
            registry: registry,
            recordSuccessfulOpen: path =>
            {
                recorded.Add(path);
                return [];
            });
        await staleShell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        await staleShell.OpenAsync(Target("two.md"), OpenGesture.Normal, CancellationToken.None);
        recorded.Clear();
        var tab = staleShell.ActiveTab!;
        Dirty(staleShell, tab, "!");
        var pending = staleShell.SaveActiveAsync(
            new SaveDecision.SaveAs("stale.note", new EncodingDescriptor("utf-8", false)),
            CancellationToken.None);
        await staleShell.GoBackAsync(CancellationToken.None);
        recorded.Clear();
        completion.SetResult(new SaveResult.Saved(Version('y'), tab.Revision));
        await pending;

        Assert.Empty(recorded);
    }

    [Fact]
    public async Task SaveAs_conflict_or_failure_does_not_change_path_or_encoding()
    {
        var original = Path.GetFullPath("one.md");
        var shell = CreateShell((buffer, decision, token) =>
            Task.FromResult<SaveResult>(new SaveResult.Conflict(Version('f'))));
        await shell.OpenAsync(Target(original), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;

        await shell.SaveActiveAsync(
            new SaveDecision.SaveAs("other.markdown", new EncodingDescriptor("utf-16", true)),
            CancellationToken.None);

        Assert.Equal(original, tab.Path);
        Assert.Equal(new EncodingDescriptor("utf-8", false), tab.Encoding);
    }

    [Fact]
    public async Task Closing_the_owner_while_saving_drops_the_late_completion()
    {
        var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<(Guid TabId, long Revision)>();
        var shell = CreateShell(
            (buffer, decision, token) => saving.Task,
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var closed = shell.ActiveTab!;
        Dirty(shell, closed, "!");
        var pending = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);

        shell.CloseActiveTab();
        saving.SetResult(new SaveResult.Saved(Version('f'), closed.Revision));
        await pending;

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.ActiveTab);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task SaveAs_completion_after_same_tab_navigation_cannot_rewrite_the_new_document()
    {
        var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<(Guid TabId, long Revision)>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider(), new NoteProvider()]);
        var shell = CreateShell(
            (buffer, decision, token) => saving.Task,
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            },
            registry);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        await shell.OpenAsync(Target("two.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");
        var savedRevision = tab.Revision;
        var pending = shell.SaveActiveAsync(
            new SaveDecision.SaveAs("late.note", new EncodingDescriptor("utf-16", true)),
            CancellationToken.None);

        await shell.GoBackAsync(CancellationToken.None);
        var currentPath = tab.Path;
        var currentVersion = tab.DiskVersion;
        saving.SetResult(new SaveResult.Saved(Version('g'), savedRevision));
        await pending;

        Assert.EndsWith("one.md", currentPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(currentPath, tab.Path);
        Assert.Equal(currentVersion, tab.DiskVersion);
        Assert.Equal(new EncodingDescriptor("utf-8", false), tab.Encoding);
        Assert.Equal("markdown", tab.FormatProvider?.Descriptor.Id);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Save_completion_after_shell_dispose_does_not_mutate_or_notify()
    {
        var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<(Guid TabId, long Revision)>();
        var shell = CreateShell(
            (buffer, decision, token) => saving.Task,
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");
        var baseline = tab.DiskVersion;
        var savedRevision = tab.Revision;
        var pending = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);

        shell.Dispose();
        saving.SetResult(new SaveResult.Saved(Version('h'), savedRevision));
        await pending;

        Assert.Equal(baseline, tab.DiskVersion);
        Assert.True(tab.IsDirty);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Cancellation_after_commit_updates_owned_disk_state_without_notifying_surface()
    {
        var saving = new TaskCompletionSource<SaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<(Guid TabId, long Revision)>();
        var shell = CreateShell(
            (buffer, decision, token) => saving.Task,
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");
        var savedRevision = tab.Revision;
        using var cancellation = new CancellationTokenSource();
        var pending = shell.SaveActiveAsync(new SaveDecision.Normal(), cancellation.Token);

        cancellation.Cancel();
        saving.SetResult(new SaveResult.Saved(Version('i'), savedRevision));
        var result = await pending;

        Assert.IsType<SaveResult.Saved>(result);
        Assert.Equal(Version('i'), tab.DiskVersion);
        Assert.False(tab.IsDirty);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Older_overlapping_save_cannot_roll_back_the_newer_saved_baseline()
    {
        var saves = new Queue<TaskCompletionSource<SaveResult>>(
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ]);
        var firstCompletion = saves.Peek();
        TaskCompletionSource<SaveResult>? secondCompletion = null;
        var notifications = new List<(Guid TabId, long Revision)>();
        var shell = CreateShell(
            (buffer, decision, token) =>
            {
                var completion = saves.Dequeue();
                secondCompletion ??= saves.Count == 0 ? completion : null;
                return completion.Task;
            },
            (tabId, revision, token) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");
        var revision = tab.Revision;

        var first = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        var second = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        secondCompletion!.SetResult(new SaveResult.Saved(Version('k'), revision));
        await second;
        firstCompletion.SetResult(new SaveResult.Saved(Version('j'), revision));
        await first;

        Assert.Equal(Version('k'), tab.DiskVersion);
        Assert.False(tab.IsDirty);
        Assert.Equal([(tab.Id, revision)], notifications);
    }

    [Fact]
    public async Task Committed_save_removes_recovery_before_a_throwing_surface_notification()
    {
        var order = new List<string>();
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('m'), buffer.Revision)),
            (_, _, _) =>
            {
                order.Add("surface");
                throw new InvalidOperationException("surface failed");
            },
            removeRecovery: (_, token) =>
            {
                Assert.False(token.CanBeCanceled);
                order.Add("recovery");
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");

        var result = await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);

        Assert.IsType<SaveResult.Saved>(result);
        Assert.False(tab.IsDirty);
        Assert.Equal(["recovery", "surface"], order);
        Assert.False(string.IsNullOrWhiteSpace(shell.EditingErrorMessage));
    }

    [Fact]
    public async Task Cancellation_after_disk_commit_still_marks_the_owned_buffer_clean_and_removes_recovery()
    {
        using var cancellation = new CancellationTokenSource();
        var recoveryRemoved = false;
        var shell = CreateShell(
            (buffer, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<SaveResult>(
                    new SaveResult.Saved(Version('n'), buffer.Revision));
            },
            saveCompleted: (_, _, token) => Task.FromCanceled(token),
            removeRecovery: (_, token) =>
            {
                Assert.False(token.CanBeCanceled);
                recoveryRemoved = true;
                return Task.CompletedTask;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");

        var result = await shell.SaveActiveAsync(new SaveDecision.Normal(), cancellation.Token);

        Assert.IsType<SaveResult.Saved>(result);
        Assert.False(tab.IsDirty);
        Assert.True(recoveryRemoved);
        Assert.Null(shell.EditingErrorMessage);
    }

    [Fact]
    public async Task Dirty_close_treats_a_committed_clean_save_as_success_when_surface_notification_fails()
    {
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('o'), buffer.Revision)),
            (_, _, _) => throw new InvalidOperationException("surface failed"),
            removeRecovery: (_, _) => Task.CompletedTask);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");

        var resolved = await shell.TryResolveDirtyTabsForCloseAsync(
            [tab],
            _ => DirtyCloseChoice.Save,
            CancellationToken.None);

        Assert.True(resolved);
        Assert.False(tab.IsDirty);
        Assert.True(shell.HasEditingError);
    }

    [Fact]
    public async Task SaveAs_cleanup_pause_then_navigation_cannot_apply_old_path_bookkeeping_or_surface_completion()
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var versions = new List<(string Path, DiskFileVersion Version)>();
        var recent = new List<string>();
        var notifications = new List<(Guid TabId, long Revision)>();
        var target = Path.GetFullPath("saved.note");
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider(), new NoteProvider()]);
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('p'), buffer.Revision)),
            (tabId, revision, _) =>
            {
                notifications.Add((tabId, revision));
                return Task.CompletedTask;
            },
            registry,
            path =>
            {
                recent.Add(path);
                return [];
            },
            async (_, token) =>
            {
                Assert.False(token.CanBeCanceled);
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            },
            (path, version) => versions.Add((path, version)));
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        await shell.OpenAsync(Target("two.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");
        versions.Clear();
        recent.Clear();
        notifications.Clear();

        var saving = shell.SaveActiveAsync(
            new SaveDecision.SaveAs(target, new EncodingDescriptor("utf-16", true)),
            CancellationToken.None);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var versionWasRecordedBeforeCleanup = versions.SequenceEqual([(target, Version('p'))]);

        await shell.GoBackAsync(CancellationToken.None);
        recent.Clear();
        releaseCleanup.TrySetResult();
        await saving;

        Assert.True(versionWasRecordedBeforeCleanup);
        Assert.Equal([(target, Version('p'))], versions);
        Assert.EndsWith("one.md", tab.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recent);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Older_SaveAs_cleanup_continuation_cannot_record_or_restart_watcher_after_a_later_save()
    {
        var firstCleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCalls = 0;
        var versions = new List<DiskFileVersion>();
        var notifications = new List<long>();
        var watchedPaths = new List<string>();
        var saveCalls = 0;
        var target = Path.GetFullPath("later.note");
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider(), new NoteProvider()]);
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(new SaveResult.Saved(
                Interlocked.Increment(ref saveCalls) == 1 ? Version('q') : Version('r'),
                buffer.Revision)),
            (_, revision, _) =>
            {
                notifications.Add(revision);
                return Task.CompletedTask;
            },
            registry,
            removeRecovery: async (_, _) =>
            {
                if (Interlocked.Increment(ref cleanupCalls) == 1)
                {
                    firstCleanupStarted.TrySetResult();
                    await releaseFirstCleanup.Task;
                }
            },
            recordSavedVersion: (_, version) => versions.Add(version),
            watchExternalChanges: (path, _) =>
            {
                watchedPaths.Add(path);
                return EmptyNotices();
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        watchedPaths.Clear();
        Dirty(shell, tab, " first");

        var first = shell.SaveActiveAsync(
            new SaveDecision.SaveAs(target, new EncodingDescriptor("utf-8", false)),
            CancellationToken.None);
        await firstCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([target], watchedPaths);
        Dirty(shell, tab, " later");
        var secondRevision = tab.Revision;
        await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        releaseFirstCleanup.TrySetResult();
        await first;

        Assert.Equal([Version('q'), Version('r')], versions);
        Assert.Equal([target], watchedPaths);
        Assert.Equal([secondRevision], notifications);
        Assert.Equal(Version('r'), tab.DiskVersion);
        Assert.False(tab.IsDirty);

        static async IAsyncEnumerable<FileChangeNotice> EmptyNotices()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task Save_cleanup_continuation_cannot_clear_a_newer_external_conflict()
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('s'), buffer.Revision)),
            removeRecovery: async (_, _) =>
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, " saved");

        var saving = shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dirty(shell, tab, " newer");
        await shell.HandleExternalChangeAsync(
            tab,
            FileChangeNotice.Changed(tab.Path, Loaded("external.md") with { Version = Version('t') }),
            CancellationToken.None);
        Assert.True(shell.ConflictBar.IsVisible);

        releaseCleanup.TrySetResult();
        await saving;

        Assert.True(shell.ConflictBar.IsVisible);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Reentrant_saved_version_callback_cannot_clear_the_newer_conflict_it_creates()
    {
        ShellViewModel? shell = null;
        DocumentTabViewModel? tab = null;
        var reenter = false;
        shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('x'), buffer.Revision)),
            removeRecovery: (_, _) => Task.CompletedTask,
            recordSavedVersion: (_, _) =>
            {
                if (!reenter)
                {
                    return;
                }

                Dirty(shell!, tab!, " newer");
                shell!.HandleExternalChangeAsync(
                        tab!,
                        FileChangeNotice.Changed(
                            tab!.Path,
                            Loaded("external.md") with { Version = Version('y') }),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            });
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        tab = shell.ActiveTab!;
        Dirty(shell, tab, " saved");
        reenter = true;

        await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);

        Assert.True(shell.ConflictBar.IsVisible);
        Assert.True(tab.IsDirty);
        Assert.Equal(Version('x'), tab.DiskVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAs_cleanup_after_tab_close_or_disposal_has_no_stale_MRU_or_surface_callbacks(bool dispose)
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var versionRecordedBeforeCleanup = false;
        var recent = new List<string>();
        var notifications = new List<Guid>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider(), new NoteProvider()]);
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('u'), buffer.Revision)),
            (tabId, _, _) =>
            {
                notifications.Add(tabId);
                return Task.CompletedTask;
            },
            registry,
            path =>
            {
                recent.Add(path);
                return [];
            },
            async (_, _) =>
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
                throw new IOException("cleanup failed after ownership changed");
            },
            (_, _) => versionRecordedBeforeCleanup = true);
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");
        recent.Clear();
        notifications.Clear();

        var saving = shell.SaveActiveAsync(
            new SaveDecision.SaveAs("closed.note", new EncodingDescriptor("utf-8", false)),
            CancellationToken.None);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recentBeforeMutation = recent.ToArray();
        if (dispose)
        {
            shell.Dispose();
        }
        else
        {
            shell.CloseTab(tab);
        }

        releaseCleanup.TrySetResult();
        await saving;

        Assert.True(versionRecordedBeforeCleanup);
        Assert.NotEmpty(recentBeforeMutation);
        Assert.Equal(recentBeforeMutation, recent);
        Assert.Empty(notifications);
        Assert.Null(shell.EditingErrorMessage);
    }

    [Fact]
    public async Task Recovery_cleanup_cancellation_after_commit_is_reported_without_changing_saved_result()
    {
        var shell = CreateShell(
            (buffer, _, _) => Task.FromResult<SaveResult>(
                new SaveResult.Saved(Version('v'), buffer.Revision)),
            removeRecovery: (_, _) => throw new OperationCanceledException("cleanup failed"));
        await shell.OpenAsync(Target("one.md"), OpenGesture.Normal, CancellationToken.None);
        var tab = shell.ActiveTab!;
        Dirty(shell, tab, "!");

        var result = await shell.SaveActiveAsync(new SaveDecision.Normal(), CancellationToken.None);

        Assert.IsType<SaveResult.Saved>(result);
        Assert.False(tab.IsDirty);
        Assert.True(shell.HasEditingError);
    }

    private static ShellViewModel CreateShell(
        Func<DocumentBuffer, SaveDecision, CancellationToken, Task<SaveResult>> save,
        Func<Guid, long, CancellationToken, Task>? saveCompleted = null,
        DocumentFormatRegistry? registry = null,
        Func<string, IReadOnlyList<RecentDocumentEntry>>? recordSuccessfulOpen = null,
        Func<Guid, CancellationToken, Task>? removeRecovery = null,
        Action<string, DiskFileVersion>? recordSavedVersion = null,
        Func<string, CancellationToken, IAsyncEnumerable<FileChangeNotice>>? watchExternalChanges = null)
    {
        App.RegisterEncodingProviders();
        registry ??= new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        return new ShellViewModel(
            registry,
            (path, encoding, token) => Task.FromResult(Loaded(path)),
            (tab, token) => Task.CompletedTask,
            saveDocument: save,
            saveCompleted: saveCompleted,
            watchExternalChanges: watchExternalChanges,
            recordSavedVersion: recordSavedVersion,
            recordSuccessfulOpen: recordSuccessfulOpen,
            removeRecovery: removeRecovery);
    }

    private sealed class NoteProvider : IDocumentFormatProvider
    {
        public DocumentFormatDescriptor Descriptor { get; } = new(
            "note",
            [".note"],
            DocumentCapabilities.Read | DocumentCapabilities.Edit);
    }

    private static void Dirty(ShellViewModel shell, DocumentTabViewModel tab, string insertedText)
    {
        shell.HandleDocumentChanged(new DocumentChangedMessage(
            new WebMessageOwner(Guid.NewGuid(), shell.WindowId, tab.Id, tab.Revision),
            new DocumentEdit(tab.Revision, [new TextChange(tab.Text.Length, tab.Text.Length, insertedText)])));
    }

    private static DocumentTarget Target(string path) =>
        new(Path.GetFullPath(path), null, null);

    private static LoadedDocument Loaded(string path) =>
        new(
            "text",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            Version('a'));

    private static DiskFileVersion Version(char value) =>
        new(4, DateTime.UnixEpoch.AddDays(value - 'a'), new string(value, 64));
}
