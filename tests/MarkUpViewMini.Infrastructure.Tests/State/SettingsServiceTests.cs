using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.State;
using MarkUpViewMini.Infrastructure.Time;

namespace MarkUpViewMini.Infrastructure.Tests.State;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Defaults_match_the_approved_schema_v1_contract()
    {
        // Break caught: a serialized default silently changes the selected option or initial editor layout.
        var settings = SettingsV1.CreateDefault();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(RootFollowMode.KeepRoot, settings.RootMode);
        Assert.Equal(280, settings.SidebarWidth);
        Assert.Equal(0.5, settings.EditorSplitRatio);
        Assert.Equal(SearchMode.FileName, settings.SidebarSearchMode);
        Assert.Equal(new SearchOptionsV1(false, false, false), settings.SidebarSearchOptions);
        Assert.Equal(new FindOptionsV1(false, false, false), settings.FindOptions);
        Assert.Empty(settings.RecentDocuments);
    }

    [Fact]
    public async Task Debounced_saves_coalesce_to_the_latest_immutable_snapshot()
    {
        // Break caught: an older delayed save can commit after a newer UI snapshot.
        var store = new ControlledStore();
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMilliseconds(250));
        var first = SettingsV1.CreateDefault() with { SidebarWidth = 301 };
        var latest = SettingsV1.CreateDefault() with { SidebarWidth = 377 };

        service.ScheduleSave(first);
        service.ScheduleSave(latest);
        clock.ReleaseNextDelay();
        await store.WaitForSaveCountAsync(1);

        Assert.Equal([377d], store.Saved.Select(item => item.SidebarWidth));
    }

    [Fact]
    public async Task Flush_includes_a_newer_snapshot_scheduled_while_a_write_is_blocked()
    {
        // Break caught: Flush captures once and returns while a concurrently scheduled newer state remains unwritten.
        var store = new ControlledStore { BlockFirstSave = true };
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));
        service.ScheduleSave(SettingsV1.CreateDefault() with { SidebarWidth = 310 });

        var flush = service.FlushAsync(CancellationToken.None);
        await store.FirstSaveStarted.Task;
        service.ScheduleSave(SettingsV1.CreateDefault() with { SidebarWidth = 420 });
        store.ReleaseFirstSave.SetResult();
        await flush;

        Assert.Equal([310d, 420d], store.Saved.Select(item => item.SidebarWidth));
    }

    [Fact]
    public async Task Late_load_cannot_replace_a_newer_scheduled_state()
    {
        // Break caught: startup IO completion overwrites a user change made while loading.
        var store = new ControlledStore { BlockLoad = true };
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));

        var load = service.LoadAsync(CancellationToken.None);
        await store.LoadStarted.Task;
        var newer = SettingsV1.CreateDefault() with { SidebarWidth = 390 };
        service.ScheduleSave(newer);
        store.ReleaseLoad.SetResult(SettingsV1.CreateDefault() with { SidebarWidth = 315 });

        Assert.Equal(newer, await load);
        Assert.Equal(newer, service.Current);
    }

    [Fact]
    public async Task Dispose_drains_the_latest_owned_snapshot_once_and_rejects_resurrection()
    {
        // Break caught: closing leaks a debounce worker or permits a late caller to recreate state after disposal.
        var store = new ControlledStore();
        var clock = new ManualClock();
        var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));
        service.ScheduleSave(SettingsV1.CreateDefault() with { SidebarWidth = 360 });

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal([360d], store.Saved.Select(item => item.SidebarWidth));
        Assert.Throws<ObjectDisposedException>(() =>
            service.ScheduleSave(SettingsV1.CreateDefault()));
    }

    [Fact]
    public async Task Background_and_dispose_save_failures_do_not_block_shutdown()
    {
        // Break caught: a transient settings write failure escapes a debounce task or prevents app shutdown.
        var store = new ControlledStore { SaveFailure = new IOException("state unavailable") };
        var clock = new ManualClock();
        var service = new SettingsService(store, clock, TimeSpan.FromMilliseconds(250));
        service.ScheduleSave(SettingsV1.CreateDefault() with { SidebarWidth = 365 });
        clock.ReleaseNextDelay();
        await store.WaitForSaveCountAsync(1);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task Successful_open_normalizes_deduplicates_moves_to_front_and_caps_at_twenty()
    {
        // Break caught: path spelling duplicates grow the menu or the oldest entry is kept past the exact cap.
        var store = new ControlledStore();
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));
        var paths = Enumerable.Range(0, 21)
            .Select(index => Path.GetFullPath($"doc-{index}.md"))
            .ToArray();

        foreach (var path in paths)
        {
            service.RecordSuccessfulOpen(path);
        }

        service.RecordSuccessfulOpen(paths[5].ToUpperInvariant());

        Assert.Equal(20, service.Current.RecentDocuments.Count);
        Assert.Equal(paths[5], service.Current.RecentDocuments[0].Path, ignoreCase: true);
        Assert.Equal(1, service.Current.RecentDocuments.Count(entry =>
            string.Equals(entry.Path, paths[5], StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(service.Current.RecentDocuments, entry =>
            string.Equals(entry.Path, paths[0], StringComparison.OrdinalIgnoreCase));
        Assert.All(service.Current.RecentDocuments, entry =>
            Assert.Equal(Path.GetFullPath(entry.Path), entry.Path));
    }

    [Fact]
    public async Task Atomic_updates_merge_recent_documents_with_concurrent_field_changes()
    {
        // Break caught: RecordSuccessfulOpen reads outside the scheduling mutation and loses a concurrent field update.
        var store = new ControlledStore();
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));
        var start = new Barrier(3);
        var path = Path.GetFullPath("atomic.md");

        var recent = Task.Run(() =>
        {
            start.SignalAndWait();
            service.RecordSuccessfulOpen(path);
        });
        var preferences = Task.Run(() =>
        {
            start.SignalAndWait();
            service.UpdateSidebarPreferences(
                RootFollowMode.FollowCurrentDocument,
                SearchMode.Body,
                new SearchOptionsV1(true, true, true));
        });
        start.SignalAndWait();
        await Task.WhenAll(recent, preferences);

        Assert.Equal(path, Assert.Single(service.Current.RecentDocuments).Path);
        Assert.Equal(RootFollowMode.FollowCurrentDocument, service.Current.RootMode);
        Assert.Equal(SearchMode.Body, service.Current.SidebarSearchMode);
        Assert.Equal(new SearchOptionsV1(true, true, true), service.Current.SidebarSearchOptions);
    }

    [Fact]
    public async Task Changed_broadcasts_latest_snapshot_outside_the_service_lock()
    {
        // Break caught: other windows keep stale MRU menus, or reentrant subscribers deadlock the service lock.
        var store = new ControlledStore();
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));
        var observed = new List<SettingsV1>();
        service.Changed += (_, change) =>
        {
            observed.Add(change.Snapshot);
            if (observed.Count == 1)
            {
                service.UpdateSidebarWidth(444);
            }
        };

        service.RecordSuccessfulOpen(Path.GetFullPath("broadcast.md"));

        Assert.Equal(2, observed.Count);
        Assert.Single(observed[^1].RecentDocuments);
        Assert.Equal(444, observed[^1].SidebarWidth);
    }

    [Fact]
    public async Task Out_of_order_notifications_carry_monotonic_generations_for_subscriber_rejection()
    {
        // Break caught: T1 commits A, pauses; T2 commits/notifies B; then T1 notifies stale A last.
        var store = new ControlledStore();
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, change) =>
        {
            if (change.Snapshot.SidebarWidth == 301)
            {
                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
        };
        long observedGeneration = 0;
        SettingsV1? observed = null;
        service.Changed += (_, change) =>
        {
            if (change.Generation > Interlocked.Read(ref observedGeneration))
            {
                Interlocked.Exchange(ref observedGeneration, change.Generation);
                observed = change.Snapshot;
            }
        };

        var first = Task.Run(() => service.UpdateSidebarWidth(301));
        await firstEntered.Task;
        await Task.Run(() => service.UpdateSidebarWidth(402));
        releaseFirst.TrySetResult();
        await first;

        Assert.Equal(402, service.Current.SidebarWidth);
        Assert.Equal(402, observed?.SidebarWidth);
        Assert.Equal(2, observedGeneration);
    }

    [Theory]
    [InlineData(0.1, 0.1)]
    [InlineData(0.9, 0.9)]
    [InlineData(0.05, 0.5)]
    [InlineData(0.95, 0.5)]
    public async Task Split_ratio_uses_one_safe_range(double requested, double expected)
    {
        var store = new ControlledStore();
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));

        service.UpdateEditorPreferences(requested, new FindOptionsV1(false, false, false));

        Assert.Equal(expected, service.Current.EditorSplitRatio);
    }

    [Fact]
    public async Task Loaded_out_of_range_split_ratio_falls_back_without_activation_risk()
    {
        var store = new ControlledStore
        {
            LoadedSettings = SettingsV1.CreateDefault() with { EditorSplitRatio = 0.05 },
        };
        var clock = new ManualClock();
        await using var service = new SettingsService(store, clock, TimeSpan.FromMinutes(1));

        var loaded = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(0.5, loaded.EditorSplitRatio);
    }

    private sealed class ManualClock : IClock
    {
        private readonly Queue<TaskCompletionSource> delays = [];

        public DateTime UtcNow => DateTime.UnixEpoch;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            lock (delays)
            {
                delays.Enqueue(completion);
            }

            return completion.Task;
        }

        public void ReleaseNextDelay()
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (delays)
                {
                    return delays.Any(completion => !completion.Task.IsCompleted);
                }
            }, TimeSpan.FromSeconds(1)));
            while (true)
            {
                TaskCompletionSource completion;
                lock (delays)
                {
                    completion = delays.Dequeue();
                }

                if (completion.TrySetResult())
                {
                    return;
                }
            }
        }
    }

    private sealed class ControlledStore : IJsonStateStore
    {
        public bool PreserveSourceOnFallback => false;

        public bool BlockLoad { get; init; }

        public bool BlockFirstSave { get; init; }

        public Exception? SaveFailure { get; init; }

        public SettingsV1? LoadedSettings { get; init; }

        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<SettingsV1> ReleaseLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SettingsV1> Saved { get; } = [];

        public async Task<T> LoadAsync<T>(
            int supportedSchemaVersion,
            Func<T> defaultFactory,
            CancellationToken cancellationToken)
        {
            LoadStarted.TrySetResult();
            if (BlockLoad)
            {
                return (T)(object)await ReleaseLoad.Task.WaitAsync(cancellationToken);
            }

            return LoadedSettings is null ? defaultFactory() : (T)(object)LoadedSettings;
        }

        public async Task SaveAsync<T>(T state, CancellationToken cancellationToken)
        {
            if (state is not SettingsV1 settings)
            {
                throw new InvalidOperationException();
            }

            lock (Saved)
            {
                Saved.Add(settings);
            }

            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            if (BlockFirstSave && Saved.Count == 1)
            {
                FirstSaveStarted.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task WaitForSaveCountAsync(int count)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < timeout)
            {
                lock (Saved)
                {
                    if (Saved.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("The expected state save did not occur.");
        }
    }
}
