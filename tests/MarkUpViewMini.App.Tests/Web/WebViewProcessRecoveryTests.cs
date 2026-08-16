using MarkUpViewMini.App.Web;
using Microsoft.Web.WebView2.Core;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebViewProcessRecoveryTests
{
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, WebViewProcessRecoveryAction.RecreateControl)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, WebViewProcessRecoveryAction.Renavigate)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, WebViewProcessRecoveryAction.Renavigate)]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited, WebViewProcessRecoveryAction.Ignore)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited, WebViewProcessRecoveryAction.Ignore)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited, WebViewProcessRecoveryAction.Ignore)]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, WebViewProcessRecoveryAction.Ignore)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited, WebViewProcessRecoveryAction.Ignore)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited, WebViewProcessRecoveryAction.Ignore)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited, WebViewProcessRecoveryAction.ShowRetry)]
    public void Process_failure_kind_selects_the_required_recovery(
        CoreWebView2ProcessFailedKind kind,
        WebViewProcessRecoveryAction expected)
    {
        Assert.Equal(expected, WebViewProcessFailurePolicy.Decide(kind));
    }

    [Fact]
    public void Recreate_replaces_the_control_even_when_handler_unregistration_throws()
    {
        var mounted = new List<TestControl>();
        var disposed = new List<TestControl>();
        var created = 0;
        var lifetime = new WebViewControlLifetime<TestControl>(
            () => new TestControl(++created),
            control => mounted.Add(control),
            control => mounted.Remove(control),
            control => disposed.Add(control));
        var original = lifetime.Current;

        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Recreate(() => throw new InvalidOperationException("unregister failed")));

        Assert.NotSame(original, lifetime.Current);
        Assert.Equal(2, created);
        Assert.Equal([lifetime.Current], mounted);
        Assert.Equal([original], disposed);
    }

    [Fact]
    public void Dispose_releases_the_control_even_when_handler_unregistration_throws()
    {
        var mounted = new List<TestControl>();
        var disposed = new List<TestControl>();
        var lifetime = new WebViewControlLifetime<TestControl>(
            () => new TestControl(1),
            control => mounted.Add(control),
            control => mounted.Remove(control),
            control => disposed.Add(control));
        var original = lifetime.Current;

        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Dispose(() => throw new InvalidOperationException("unregister failed")));

        Assert.Empty(mounted);
        Assert.Equal([original], disposed);
    }

    [Fact]
    public async Task Timed_out_initialization_releases_the_abandoned_control_before_retry()
    {
        var mounted = new List<TestControl>();
        var disposed = new List<TestControl>();
        var created = 0;
        using var controls = new WebViewControlLifetime<TestControl>(
            () => new TestControl(++created),
            control => mounted.Add(control),
            control => mounted.Remove(control),
            control => disposed.Add(control));
        await using var initialization = new WebViewInitializationLifetime();
        var abandonedInitialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonedAttemptFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutatedAfterTimeout = false;
        var original = controls.Current;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            controls.EnsureInitializedAsync(
                initialization,
                async (control, token) =>
                {
                    try
                    {
                        await abandonedInitialization.Task;
                        token.ThrowIfCancellationRequested();
                        mutatedAfterTimeout = true;
                    }
                    finally
                    {
                        abandonedAttemptFinished.TrySetResult();
                    }
                },
                () => { },
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None));

        Assert.NotSame(original, controls.Current);
        Assert.Equal([original], disposed);
        Assert.Equal([controls.Current], mounted);
        var retryControl = controls.Current;

        await controls.EnsureInitializedAsync(
            initialization,
            (control, token) =>
            {
                Assert.Same(retryControl, control);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            () => { },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        abandonedInitialization.SetResult();
        await abandonedAttemptFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(mutatedAfterTimeout);
        Assert.Same(retryControl, controls.Current);
    }

    private sealed record TestControl(int Id);
}
