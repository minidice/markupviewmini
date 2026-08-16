using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Diagnostics;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebViewRecoveryControllerTests
{
    private static readonly Guid TabA = Guid.Parse("2649f27c-f8ad-42f8-ae29-20d77ee2342b");
    private static readonly Guid TabB = Guid.Parse("e550a71e-2d38-40a6-b6c5-0964e32e6b35");

    [Fact]
    public async Task Failure_replaces_one_surface_rehydrates_every_tab_and_activates_the_exact_owner_last()
    {
        var first = Snapshot(
            TabA,
            "authoritative A",
            revision: 7,
            dirty: true,
            DocumentMode.Edit,
            new DocumentUiHints(3, 5, 120, 0.35, true, false, true));
        var second = Snapshot(
            TabB,
            "authoritative B",
            revision: 4,
            dirty: false,
            DocumentMode.Read,
            new DocumentUiHints(1, 1, 40, 0.65, false, true, false));
        var operations = new RecordingOperations([first, second], TabB);
        using var controller = CreateController(operations);

        await controller.HandleProcessFailureAsync();

        Assert.Equal(
            ["replace:1", $"ready:1:{operations.BootstrapTabId}", $"hydrate:1:{TabA}", $"activate:1:{TabB}", "clear:1"],
            operations.Events);
        Assert.Equal(first, Assert.Single(operations.Rehydrated));
        Assert.Equal(second, Assert.Single(operations.Activated));
        Assert.False(controller.CanRetry);
    }

    [Fact]
    public async Task Concurrent_failures_join_the_same_serialized_recovery()
    {
        var operations = new RecordingOperations([Snapshot(TabA)], TabA)
        {
            ReadyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var controller = CreateController(operations);

        var first = controller.HandleProcessFailureAsync();
        await operations.ReadyEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = controller.HandleProcessFailureAsync();

        Assert.Same(first, second);
        operations.ReadyGate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, operations.ReplaceCount);
        Assert.Equal(1, operations.ReadyCount);
    }

    [Fact]
    public async Task New_document_activation_supersedes_waiting_recovery_before_it_posts_any_snapshot()
    {
        var operations = new RecordingOperations([Snapshot(TabA)], TabA)
        {
            ReadyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var controller = CreateController(operations);
        var recovery = controller.HandleProcessFailureAsync();
        await operations.ReadyEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        controller.SupersedeCurrentRecovery();
        operations.SetTabs([Snapshot(TabB, "new owner")], TabB);
        operations.ReadyGate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.Empty(operations.Rehydrated);
        Assert.Empty(operations.Activated);
        Assert.DoesNotContain(operations.Events, entry => entry.StartsWith("clear", StringComparison.Ordinal));
        Assert.DoesNotContain(operations.Events, entry => entry.StartsWith("show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inactive_tab_reloaded_while_waiting_is_rehydrated_from_the_latest_authoritative_snapshot()
    {
        var oldInactive = Snapshot(TabA, "old body", revision: 3);
        var active = Snapshot(TabB, "active body", revision: 4);
        var latestInactive = Snapshot(TabA, "external reload", revision: 5, dirty: false);
        var operations = new RecordingOperations([oldInactive, active], TabB)
        {
            ReadyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var controller = CreateController(operations);
        var recovery = controller.HandleProcessFailureAsync();
        await operations.ReadyEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        operations.SetTabs([latestInactive, active], TabB);
        operations.ReadyGate.SetResult();
        await recovery;

        Assert.Equal(latestInactive, Assert.Single(operations.Rehydrated));
        Assert.Equal(active, Assert.Single(operations.Activated));
    }

    [Fact]
    public async Task Failed_recovery_exposes_retry_and_retry_uses_a_fresh_generation()
    {
        var operations = new RecordingOperations([Snapshot(TabA)], TabA)
        {
            ReadyFailure = new InvalidOperationException("SECRET-DOCUMENT-BODY"),
        };
        var logger = new RecordingLogger();
        using var controller = new WebViewRecoveryController(
            operations,
            logger,
            () => "{\"component\":\"WebView\",\"eventName\":\"RecoveryFailed\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(controller.HandleProcessFailureAsync);

        Assert.True(controller.CanRetry);
        Assert.Equal(["show:1"], operations.Events.Where(entry => entry.StartsWith("show", StringComparison.Ordinal)));
        Assert.DoesNotContain("SECRET-DOCUMENT-BODY", controller.CopyDiagnostics(), StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.EventName == "RecoveryFailed" && entry.Error is not null);

        operations.ReadyFailure = null;
        await controller.RetryAsync();

        Assert.Equal(2, operations.ReplaceCount);
        Assert.Contains("replace:2", operations.Events);
        Assert.Contains("clear:2", operations.Events);
        Assert.False(controller.CanRetry);
    }

    [Fact]
    public async Task Ready_timeout_never_rehydrates_partial_tab_state_and_remains_retryable()
    {
        var operations = new RecordingOperations([Snapshot(TabA), Snapshot(TabB)], TabA)
        {
            ReadyFailure = new TimeoutException("late surface with private context"),
        };
        using var controller = CreateController(operations);

        await Assert.ThrowsAsync<TimeoutException>(controller.HandleProcessFailureAsync);

        Assert.Empty(operations.Rehydrated);
        Assert.Empty(operations.Activated);
        Assert.True(controller.CanRetry);
        Assert.Contains("show:1", operations.Events);
    }

    [Fact]
    public async Task Final_activation_post_failure_exposes_retry_without_clearing_the_error()
    {
        var operations = new RecordingOperations([Snapshot(TabA)], TabA)
        {
            ActivateFailure = new InvalidOperationException("post failed"),
        };
        using var controller = CreateController(operations);

        await Assert.ThrowsAsync<InvalidOperationException>(controller.HandleProcessFailureAsync);

        Assert.True(controller.CanRetry);
        Assert.Contains("show:1", operations.Events);
        Assert.DoesNotContain("clear:1", operations.Events);
    }

    [Fact]
    public async Task Disposal_cancels_recovery_before_any_late_rehydration_or_ui_mutation()
    {
        var operations = new RecordingOperations([Snapshot(TabA)], TabA)
        {
            ReadyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var controller = CreateController(operations);
        var recovery = controller.HandleProcessFailureAsync();
        await operations.ReadyEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        controller.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        operations.ReadyGate.SetResult();
        await Task.Delay(20);
        Assert.Empty(operations.Rehydrated);
        Assert.Empty(operations.Activated);
        Assert.DoesNotContain(operations.Events, entry => entry.StartsWith("show", StringComparison.Ordinal));
        Assert.DoesNotContain(operations.Events, entry => entry.StartsWith("clear", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_or_missing_active_owner_still_replaces_and_initializes_a_deactivated_surface()
    {
        var noTabs = new RecordingOperations([], null);
        using var first = CreateController(noTabs);
        await first.HandleProcessFailureAsync();
        Assert.Equal(["replace:1", $"ready:1:{noTabs.BootstrapTabId}", "deactivate:1", "clear:1"], noTabs.Events);
        Assert.Equal(1, noTabs.ReplaceCount);

        var staleOwner = new RecordingOperations([Snapshot(TabA)], TabB);
        using var second = CreateController(staleOwner);
        await second.HandleProcessFailureAsync();
        Assert.Equal(1, staleOwner.ReplaceCount);
        Assert.Equal([Snapshot(TabA)], staleOwner.Rehydrated);
        Assert.Empty(staleOwner.Activated);
        Assert.Contains("deactivate:1", staleOwner.Events);
    }

    private static WebViewRecoveryController CreateController(RecordingOperations operations) =>
        new(operations, new RecordingLogger(), () => string.Empty);

    private static WebViewRecoveryTabSnapshot Snapshot(
        Guid? tabId = null,
        string text = "body",
        long revision = 3,
        bool dirty = true,
        DocumentMode mode = DocumentMode.Edit,
        DocumentUiHints? hints = null) =>
        new(
            tabId ?? TabA,
            $@"D:\Docs\{tabId ?? TabA}.md",
            text,
            revision,
            dirty,
            mode,
            hints ?? new DocumentUiHints(0, 0, 0),
            "\r\n");

    private sealed class RecordingOperations : IWebViewRecoveryOperations
    {
        private IReadOnlyList<WebViewRecoveryTabSnapshot> tabs;
        private Guid? activeTabId;

        public RecordingOperations(
            IReadOnlyList<WebViewRecoveryTabSnapshot> tabs,
            Guid? activeTabId)
        {
            this.tabs = tabs;
            this.activeTabId = activeTabId;
        }

        public Guid BootstrapTabId { get; } =
            Guid.Parse("f1a3c20a-fcf9-41ec-97d8-28ad8f58fede");
        public List<string> Events { get; } = [];
        public List<WebViewRecoveryTabSnapshot> Rehydrated { get; } = [];
        public List<WebViewRecoveryTabSnapshot> Activated { get; } = [];
        public TaskCompletionSource ReadyEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ReadyGate { get; set; }
        public Exception? ReadyFailure { get; set; }
        public Exception? ActivateFailure { get; set; }
        public int ReplaceCount { get; private set; }
        public int ReadyCount { get; private set; }

        public Guid CaptureBootstrapTabId() => BootstrapTabId;

        public IReadOnlyList<WebViewRecoveryTabSnapshot> CaptureTabs() => tabs;

        public Guid? CaptureActiveTabId() => activeTabId;

        public void SetTabs(IReadOnlyList<WebViewRecoveryTabSnapshot> currentTabs, Guid? currentActiveTabId)
        {
            tabs = currentTabs;
            activeTabId = currentActiveTabId;
        }

        public Task ReplaceBrokenSurfaceAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceCount++;
            Events.Add($"replace:{generation}");
            return Task.CompletedTask;
        }

        public async Task InitializeAndWaitForReadyAsync(
            long generation,
            Guid bootstrapTabId,
            CancellationToken cancellationToken)
        {
            ReadyCount++;
            Events.Add($"ready:{generation}:{bootstrapTabId}");
            ReadyEntered.TrySetResult();
            if (ReadyGate is not null)
            {
                await ReadyGate.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (ReadyFailure is not null)
            {
                throw ReadyFailure;
            }
        }

        public Task RehydrateTabAsync(
            long generation,
            WebViewRecoveryTabSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"hydrate:{generation}:{snapshot.TabId}");
            Rehydrated.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task ActivateTabAsync(
            long generation,
            WebViewRecoveryTabSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ActivateFailure is not null)
            {
                throw ActivateFailure;
            }

            Events.Add($"activate:{generation}:{snapshot.TabId}");
            Activated.Add(snapshot);
            return Task.CompletedTask;
        }

        public void DeactivateRecoveredSurface(long generation)
        {
            Events.Add($"deactivate:{generation}");
        }

        public void ShowRecoveryFailure(long generation)
        {
            Events.Add($"show:{generation}");
        }

        public void ClearRecoveryFailure(long generation)
        {
            Events.Add($"clear:{generation}");
        }
    }

    private sealed class RecordingLogger : ISafeLogger
    {
        public List<(string Component, string EventName, string? Path, Exception? Error)> Entries { get; } = [];

        public void Write(string component, string eventName, string? path, Exception? error) =>
            Entries.Add((component, eventName, path, error));
    }
}
