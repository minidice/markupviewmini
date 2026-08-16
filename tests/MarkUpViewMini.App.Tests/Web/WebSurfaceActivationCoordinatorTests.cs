using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebSurfaceActivationCoordinatorTests
{
    private static readonly Guid TabA = Guid.Parse("2649f27c-f8ad-42f8-ae29-20d77ee2342b");
    private static readonly Guid TabB = Guid.Parse("e550a71e-2d38-40a6-b6c5-0964e32e6b35");

    [Fact]
    public void Newer_activation_supersedes_reversed_initialization_and_ready_continuations()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var first = coordinator.BeginActivation(TabA);
        coordinator.MarkInitializing(first);

        var second = coordinator.BeginActivation(TabB);

        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
        Assert.False(coordinator.TryRecordPosted(first, Guid.NewGuid(), revision: 1));

        coordinator.MarkAwaitingReady(second, TabB);
        Assert.True(coordinator.TryMarkReady(TabB));
        var requestId = Guid.NewGuid();
        Assert.True(coordinator.TryRecordPosted(second, requestId, revision: 4));
        Assert.Equal(new WebResponseContext(requestId, TabB, 4), coordinator.CurrentResponse);
    }

    [Fact]
    public void Newer_activation_can_share_the_pending_valid_bootstrap_ready_signal()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var first = coordinator.BeginActivation(TabA);
        coordinator.MarkAwaitingReady(first, TabA);

        var second = coordinator.BeginActivation(TabB);
        coordinator.MarkInitializing(second);

        Assert.Equal(WebSurfaceLifecycleState.AwaitingReady, coordinator.State);
        Assert.True(coordinator.TryMarkReady(TabA));
        Assert.False(coordinator.TryRecordPosted(first, Guid.NewGuid(), revision: 1));
        Assert.True(coordinator.TryRecordPosted(second, Guid.NewGuid(), revision: 1));
    }

    [Theory]
    [InlineData(WebSurfaceFailure.Timeout)]
    [InlineData(WebSurfaceFailure.NavigationFailed)]
    [InlineData(WebSurfaceFailure.ProcessFailed)]
    public void Failure_clears_correlation_and_reset_creates_a_new_retry_generation(
        WebSurfaceFailure failure)
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var activation = coordinator.BeginActivation(TabA);
        coordinator.MarkInitializing(activation);
        coordinator.MarkAwaitingReady(activation, TabA);
        coordinator.TryRecordPosted(activation, Guid.NewGuid(), revision: 2);

        coordinator.MarkFailed(activation, failure);

        Assert.Equal(WebSurfaceLifecycleState.Failed, coordinator.State);
        Assert.Equal(failure, coordinator.Failure);
        Assert.True(coordinator.CanRetry);
        Assert.Null(coordinator.CurrentResponse);
        Assert.False(coordinator.IsCurrent(activation));

        var retry = coordinator.BeginRetry();

        Assert.Equal(TabA, retry.TabId);
        Assert.True(coordinator.IsCurrent(retry));
        Assert.Equal(WebSurfaceLifecycleState.Uninitialized, coordinator.State);
        Assert.Equal(WebSurfaceFailure.None, coordinator.Failure);
        Assert.False(coordinator.CanRetry);
    }

    [Fact]
    public void Stale_failure_cannot_reset_a_newer_activation()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var stale = coordinator.BeginActivation(TabA);
        var current = coordinator.BeginActivation(TabB);

        coordinator.MarkFailed(stale, WebSurfaceFailure.NavigationFailed);

        Assert.True(coordinator.IsCurrent(current));
        Assert.NotEqual(WebSurfaceLifecycleState.Failed, coordinator.State);
    }

    [Fact]
    public void Correlated_render_failure_is_retryable_and_stale_render_errors_stay_ignored()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var activation = coordinator.BeginActivation(TabA);
        coordinator.MarkAwaitingReady(activation, TabA);
        Assert.True(coordinator.TryMarkReady(TabA));
        var failedRequest = Guid.NewGuid();
        Assert.True(coordinator.TryRecordPosted(activation, failedRequest, revision: 4));
        var failedGeneration = activation.Generation;

        Assert.True(coordinator.TryMarkRenderFailed(
            new WebResponseContext(failedRequest, TabA, Revision: 4)));

        Assert.Equal(WebSurfaceLifecycleState.Failed, coordinator.State);
        Assert.Equal(WebSurfaceFailure.RenderFailed, coordinator.Failure);
        Assert.True(coordinator.CanRetry);
        Assert.False(coordinator.HasActiveDocument);
        Assert.Null(coordinator.CurrentResponse);

        var retry = coordinator.BeginRetry();
        Assert.True(retry.Generation > failedGeneration);
        coordinator.MarkAwaitingReady(retry, TabA);
        Assert.True(coordinator.TryMarkReady(TabA));
        var retryRequest = Guid.NewGuid();
        Assert.True(coordinator.TryRecordPosted(retry, retryRequest, revision: 4));

        Assert.False(coordinator.TryMarkRenderFailed(
            new WebResponseContext(failedRequest, TabA, Revision: 4)));
        Assert.Equal(WebSurfaceLifecycleState.Ready, coordinator.State);
        Assert.Equal(new WebResponseContext(retryRequest, TabA, 4), coordinator.CurrentResponse);
    }

    [Fact]
    public void Deactivate_invalidates_generation_request_and_last_requested_tab()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var activation = coordinator.BeginActivation(TabA);
        coordinator.MarkAwaitingReady(activation, TabA);
        coordinator.TryMarkReady(TabA);
        coordinator.TryRecordPosted(activation, Guid.NewGuid(), revision: 3);

        coordinator.Deactivate();

        Assert.False(coordinator.IsCurrent(activation));
        Assert.Null(coordinator.RequestedTabId);
        Assert.Null(coordinator.CurrentResponse);
        Assert.False(coordinator.HasActiveDocument);
        Assert.False(coordinator.CanRetry);
    }

    [Fact]
    public void Failure_after_deactivation_does_not_resurrect_surface_state()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        coordinator.BeginActivation(TabA);
        coordinator.Deactivate();

        coordinator.MarkFailed(WebSurfaceFailure.ProcessFailed);

        Assert.Equal(WebSurfaceLifecycleState.Uninitialized, coordinator.State);
        Assert.Null(coordinator.RequestedTabId);
        Assert.False(coordinator.CanRetry);
    }

    [Fact]
    public void Expected_activation_cancellation_never_creates_retry_state()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var activation = coordinator.BeginActivation(TabA);
        coordinator.MarkInitializing(activation);

        coordinator.CancelActivation(activation);

        Assert.Equal(WebSurfaceLifecycleState.Uninitialized, coordinator.State);
        Assert.Equal(WebSurfaceFailure.None, coordinator.Failure);
        Assert.Null(coordinator.CurrentResponse);
        Assert.False(coordinator.HasActiveDocument);
        Assert.False(coordinator.CanRetry);
        Assert.False(coordinator.IsCurrent(activation));
    }

    [Fact]
    public void Stale_activation_cancellation_cannot_clear_a_newer_activation()
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var stale = coordinator.BeginActivation(TabA);
        var current = coordinator.BeginActivation(TabB);

        coordinator.CancelActivation(stale);

        Assert.True(coordinator.IsCurrent(current));
        Assert.Equal(TabB, coordinator.RequestedTabId);
    }

    [Fact]
    public void Slow_replacement_invalidates_the_posted_response_and_notifies_command_owners()
    {
        // Break caught: beginning a slow replacement can leave Ctrl+F enabled against the preceding document response.
        var coordinator = CreatePostedCoordinator(TabA, revision: 3, out _);
        var notifications = new List<WebResponseContext?>();
        coordinator.CurrentResponseChanged += () => notifications.Add(coordinator.CurrentResponse);

        coordinator.BeginActivation(TabB);

        Assert.Null(coordinator.CurrentResponse);
        Assert.Equal([null], notifications);
    }

    [Fact]
    public void Deactivation_and_failure_each_notify_that_the_posted_response_is_gone()
    {
        // Break caught: tab deactivation or surface failure can clear correlation without invalidating routed find commands.
        var deactivated = CreatePostedCoordinator(TabA, revision: 2, out _);
        var deactivationNotifications = 0;
        deactivated.CurrentResponseChanged += () => deactivationNotifications++;
        deactivated.Deactivate();

        var failed = CreatePostedCoordinator(TabA, revision: 2, out var activation);
        var failureNotifications = 0;
        failed.CurrentResponseChanged += () => failureNotifications++;
        failed.MarkFailed(activation, WebSurfaceFailure.ProcessFailed);

        Assert.Equal(1, deactivationNotifications);
        Assert.Equal(1, failureNotifications);
        Assert.Null(deactivated.CurrentResponse);
        Assert.Null(failed.CurrentResponse);
    }

    private static WebSurfaceActivationCoordinator CreatePostedCoordinator(
        Guid tabId,
        long revision,
        out WebActivationStamp activation)
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        activation = coordinator.BeginActivation(tabId);
        coordinator.MarkAwaitingReady(activation, tabId);
        Assert.True(coordinator.TryMarkReady(tabId));
        Assert.True(coordinator.TryRecordPosted(activation, Guid.NewGuid(), revision));
        return coordinator;
    }
}
