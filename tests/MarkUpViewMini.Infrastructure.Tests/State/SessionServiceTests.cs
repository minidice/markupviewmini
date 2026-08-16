using System.Text.Json;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Paths;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.Infrastructure.Tests.State;

public sealed class SessionServiceTests
{
    [Fact]
    public async Task Two_window_session_roundtrips_exact_order_active_tabs_history_hints_and_layout()
    {
        // Break caught: persistence reorders tabs/windows or drops the active/history/UI metadata needed for restore.
        var store = new ControlledStore();
        await using var service = new SessionService(store);
        var firstTab = Tab("first.md", DocumentMode.Edit, 17, 19, 240.5);
        var secondTab = Tab("second.md", DocumentMode.Read, 3, 3, 10);
        var snapshot = new SessionV1
        {
            Windows =
            [
                Window(Guid.NewGuid(), [firstTab, secondTab], secondTab.TabId, "root-a", 10, 20),
                Window(Guid.NewGuid(), [Tab("third.md", DocumentMode.Edit, 1, 2, 99)], null, "root-b", 30, 40),
            ],
        };

        service.ScheduleSave(snapshot);
        await service.FlushAsync(CancellationToken.None);
        store.Loaded = store.Saved.Single();

        var loaded = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(snapshot.Windows.Select(window => window.WindowId), loaded.Session.Windows.Select(window => window.WindowId));
        var restoredFirst = loaded.Session.Windows[0];
        Assert.Equal([firstTab.TabId, secondTab.TabId], restoredFirst.Tabs.Select(tab => tab.TabId));
        Assert.Equal(secondTab.TabId, restoredFirst.ActiveTabId);
        Assert.Equal(firstTab.Mode, restoredFirst.Tabs[0].Mode);
        Assert.Equal(firstTab.History, restoredFirst.Tabs[0].History);
        Assert.Equal(firstTab.Hints, restoredFirst.Tabs[0].Hints);
        Assert.Equal(snapshot.Windows[1].Layout, loaded.Session.Windows[1].Layout);
        Assert.Equal(0, loaded.SkippedEntries);
    }

    [Fact]
    public async Task Flush_drains_a_newer_generation_scheduled_during_an_in_flight_save()
    {
        // Break caught: a window opened while session.json is being written is lost behind the stale captured snapshot.
        var store = new ControlledStore { BlockFirstSave = true };
        await using var service = new SessionService(store);
        var old = new SessionV1 { Windows = [Window(Guid.NewGuid(), [], null, null, 1, 2)] };
        var latest = old with
        {
            Windows = [.. old.Windows, Window(Guid.NewGuid(), [], null, null, 3, 4)],
        };
        service.ScheduleSave(old);

        var flush = service.FlushAsync(CancellationToken.None);
        await store.FirstSaveStarted.Task;
        service.ScheduleSave(latest);
        store.ReleaseFirstSave.TrySetResult();
        await flush;

        Assert.Equal([1, 2], store.Saved.Select(item => item.Windows.Count));
        Assert.Equal(latest.Windows.Select(window => window.WindowId), store.Saved[^1].Windows.Select(window => window.WindowId));
    }

    [Fact]
    public async Task Concurrent_flush_and_dispose_share_writes_and_finish_on_the_latest_generation()
    {
        // Break caught: window-close and App.OnExit race writes the same generation twice or dispose misses a newer state.
        var store = new ControlledStore { BlockFirstSave = true };
        var service = new SessionService(store);
        service.ScheduleSave(new SessionV1 { Windows = [Window(Guid.NewGuid(), [], null, null, 1, 2)] });

        var flush = service.FlushAsync(CancellationToken.None);
        await store.FirstSaveStarted.Task;
        var dispose = service.DisposeAsync().AsTask();
        store.ReleaseFirstSave.TrySetResult();
        await Task.WhenAll(flush, dispose);

        Assert.Single(store.Saved);
        Assert.Throws<ObjectDisposedException>(() => service.ScheduleSave(SessionV1.CreateDefault()));
    }

    [Fact]
    public void Schema_has_no_place_for_dirty_body_recovery_body_or_find_search_query()
    {
        // Break caught: normal session serialization begins retaining private document/search content.
        var json = JsonSerializer.Serialize(new SessionV1
        {
            Windows = [Window(Guid.NewGuid(), [Tab("privacy.md", DocumentMode.Edit, 4, 4, 12)], null, null, 1, 2)],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("body", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("text", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("search", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("find", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_skips_invalid_individual_entries_without_discarding_valid_windows()
    {
        // Break caught: one malformed tab path or unsupported enum causes all otherwise valid session state to disappear.
        var valid = Tab("valid.md", DocumentMode.Read, 0, 0, 0);
        var invalid = valid with { TabId = Guid.Empty, Path = "\0invalid" };
        var store = new ControlledStore
        {
            Loaded = new SessionV1
            {
                Windows =
                [
                    Window(Guid.NewGuid(), [invalid, valid], valid.TabId, "root", 1, 2),
                    Window(Guid.Empty, [Tab("other.md", DocumentMode.Read, 0, 0, 0)], null, null, 3, 4),
                ],
            },
        };
        await using var service = new SessionService(store);

        var result = await service.LoadAsync(CancellationToken.None);

        var window = Assert.Single(result.Session.Windows);
        Assert.Equal(valid.TabId, Assert.Single(window.Tabs).TabId);
        Assert.Equal(2, result.SkippedEntries);
    }

    [Fact]
    public async Task Load_keeps_first_unique_window_and_tab_owners_and_summarizes_duplicates()
    {
        // Break caught: duplicate GUID owners construct ambiguous Shell/recovery state or closing one drops another window.
        var duplicateWindowId = Guid.NewGuid();
        var duplicateTab = Tab("first.md", DocumentMode.Read, 0, 0, 0);
        var uniqueTab = Tab("unique.md", DocumentMode.Read, 0, 0, 0);
        var store = new ControlledStore
        {
            Loaded = new SessionV1
            {
                Windows =
                [
                    Window(duplicateWindowId, [duplicateTab, duplicateTab], duplicateTab.TabId, null, 1, 2),
                    Window(duplicateWindowId, [Tab("hidden.md", DocumentMode.Read, 0, 0, 0)], null, null, 3, 4),
                    Window(Guid.NewGuid(), [duplicateTab, uniqueTab], uniqueTab.TabId, null, 5, 6),
                ],
            },
        };
        await using var service = new SessionService(store);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(2, result.Session.Windows.Count);
        Assert.Equal([duplicateTab.TabId], result.Session.Windows[0].Tabs.Select(tab => tab.TabId));
        Assert.Equal([uniqueTab.TabId], result.Session.Windows[1].Tabs.Select(tab => tab.TabId));
        Assert.Equal(3, result.SkippedEntries);
        var afterClose = result.Session.Windows.Where(window => window.WindowId != duplicateWindowId);
        Assert.Equal(result.Session.Windows[1].WindowId, Assert.Single(afterClose).WindowId);
    }

    [Fact]
    public async Task Future_schema_launch_and_exit_preserves_the_exact_source_without_a_user_mutation()
    {
        // Break caught: automatic default-window capture overwrites a future session schema on ordinary launch/exit.
        var directory = Path.Combine(Path.GetTempPath(), nameof(SessionServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new TestPaths(directory);
        const string futureJson = "{\"schemaVersion\":2,\"futurePrivateField\":\"opaque-value\"}";
        await File.WriteAllTextAsync(paths.SessionFile, futureJson);
        try
        {
            var service = new SessionService(paths);
            _ = await service.LoadAsync(CancellationToken.None);
            service.ScheduleSave(
                new SessionV1 { Windows = [Window(Guid.NewGuid(), [], null, null, 1, 2)] },
                SessionSaveReason.AutomaticRestore);

            await service.DisposeAsync();

            Assert.Equal(futureJson, await File.ReadAllTextAsync(paths.SessionFile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Genuine_user_mutation_allows_v1_to_replace_a_future_schema_fallback()
    {
        // Break caught: future-schema preservation becomes permanent even after the user changes the live session.
        var store = new ControlledStore { PreserveSourceOnFallbackValue = true };
        var service = new SessionService(store);
        _ = await service.LoadAsync(CancellationToken.None);
        var startup = new SessionV1 { Windows = [Window(Guid.NewGuid(), [], null, null, 1, 2)] };
        var changed = startup with
        {
            Windows = [.. startup.Windows, Window(Guid.NewGuid(), [], null, null, 3, 4)],
        };
        service.ScheduleSave(startup, SessionSaveReason.AutomaticRestore);
        service.ScheduleSave(changed, SessionSaveReason.UserMutation);

        await service.DisposeAsync();

        Assert.Single(store.Saved);
        Assert.Equal(2, store.Saved[0].Windows.Count);
    }

    private static SessionWindowV1 Window(
        Guid id,
        IReadOnlyList<SessionTabV1> tabs,
        Guid? active,
        string? root,
        double left,
        double top) =>
        new()
        {
            WindowId = id,
            Tabs = tabs,
            ActiveTabId = active,
            RootPath = root is null ? null : Path.GetFullPath(root),
            Layout = new SessionWindowLayoutV1(left, top, 1024, 768, false),
        };

    private static SessionTabV1 Tab(
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
            History =
            [
                new SessionNavigationEntryV1(Path.GetFullPath("before.md"), 2, null, DocumentMode.Read, 1),
                new SessionNavigationEntryV1(Path.GetFullPath(path), null, "current", mode, scroll),
            ],
            HistoryIndex = 1,
            Hints = new SessionEditorHintsV1(anchor, head, scroll, 0.55),
        };

    private sealed class ControlledStore : IJsonStateStore
    {
        public bool PreserveSourceOnFallback => PreserveSourceOnFallbackValue;

        public bool PreserveSourceOnFallbackValue { get; init; }

        public bool BlockFirstSave { get; init; }

        public SessionV1? Loaded { get; set; }

        public List<SessionV1> Saved { get; } = [];

        public TaskCompletionSource FirstSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> LoadAsync<T>(
            int supportedSchemaVersion,
            Func<T> defaultFactory,
            CancellationToken cancellationToken) =>
            Task.FromResult(Loaded is null ? defaultFactory() : (T)(object)Loaded);

        public async Task SaveAsync<T>(T state, CancellationToken cancellationToken)
        {
            var session = Assert.IsType<SessionV1>(state);
            Saved.Add(session);
            if (BlockFirstSave && Saved.Count == 1)
            {
                FirstSaveStarted.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class TestPaths(string directory) : IAppDataPaths
    {
        public string DataDirectory => directory;

        public string SettingsFile => Path.Combine(directory, "settings.json");

        public string SessionFile => Path.Combine(directory, "session.json");

        public string RecoveryDirectory => Path.Combine(directory, "recovery");

        public string LogsDirectory => Path.Combine(directory, "logs");

        public string WebView2Directory => Path.Combine(directory, "webview2");
    }
}
