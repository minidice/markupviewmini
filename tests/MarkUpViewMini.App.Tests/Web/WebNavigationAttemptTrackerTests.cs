using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebNavigationAttemptTrackerTests
{
    private static readonly Uri BootstrapA = new(
        "https://app.markupviewmini.local/index.html?windowId=11111111-1111-4111-8111-111111111111&tabId=22222222-2222-4222-8222-222222222222");
    private static readonly Uri BootstrapB = new(
        "https://app.markupviewmini.local/index.html?windowId=11111111-1111-4111-8111-111111111111&tabId=33333333-3333-4333-8333-333333333333");

    [Fact]
    public void Starting_records_only_the_current_expected_bootstrap_navigation()
    {
        var tracker = new WebNavigationAttemptTracker();
        tracker.Begin(generation: 4, BootstrapA);

        Assert.False(tracker.TryRecordStarting(generation: 3, BootstrapA, navigationId: 39));
        Assert.False(tracker.TryRecordStarting(generation: 4, BootstrapB, navigationId: 40));
        Assert.True(tracker.TryRecordStarting(generation: 4, BootstrapA, navigationId: 41));
        Assert.False(tracker.TryRecordStarting(generation: 4, BootstrapA, navigationId: 42));
        Assert.False(tracker.IsCurrentCompletion(generation: 3, navigationId: 41));
        Assert.False(tracker.IsCurrentCompletion(generation: 4, navigationId: 40));
        Assert.True(tracker.IsCurrentCompletion(generation: 4, navigationId: 41));
    }

    [Fact]
    public void Old_completion_after_a_new_retry_cannot_fail_the_new_attempt()
    {
        var tracker = new WebNavigationAttemptTracker();
        tracker.Begin(generation: 8, BootstrapA);
        Assert.True(tracker.TryRecordStarting(generation: 8, BootstrapA, navigationId: 80));
        tracker.Clear();

        tracker.Begin(generation: 9, BootstrapB);
        Assert.True(tracker.TryRecordStarting(generation: 9, BootstrapB, navigationId: 90));

        Assert.False(tracker.IsCurrentCompletion(generation: 8, navigationId: 80));
        Assert.False(tracker.IsCurrentCompletion(generation: 9, navigationId: 80));
        Assert.True(tracker.IsCurrentCompletion(generation: 9, navigationId: 90));
        Assert.Equal(9, tracker.CurrentGeneration);
    }

    [Theory]
    [InlineData(WebSurfaceFailure.Timeout)]
    [InlineData(WebSurfaceFailure.ProcessFailed)]
    public void Clearing_for_timeout_or_process_failure_ignores_late_completion(
        WebSurfaceFailure failure)
    {
        var tracker = new WebNavigationAttemptTracker();
        tracker.Begin(generation: 12, BootstrapA);
        Assert.True(tracker.TryRecordStarting(generation: 12, BootstrapA, navigationId: 120));

        tracker.Clear();

        Assert.False(tracker.IsCurrentCompletion(generation: 12, navigationId: 120));
        Assert.Null(tracker.CurrentGeneration);
        Assert.NotEqual(WebSurfaceFailure.None, failure);
    }
}
