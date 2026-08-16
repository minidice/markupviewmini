using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebViewInitializationLifetimeTests
{
    [Fact]
    public async Task Disposal_cancels_initialization_and_prevents_late_mutation()
    {
        var nonCancellableAwait = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutatedAfterAwait = false;
        var lifetime = new WebViewInitializationLifetime();
        var initialization = lifetime.EnsureInitializedAsync(
            async token =>
            {
                await nonCancellableAwait.Task;
                token.ThrowIfCancellationRequested();
                mutatedAfterAwait = true;
            },
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        var disposal = lifetime.DisposeAsync().AsTask();
        nonCancellableAwait.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(mutatedAfterAwait);
    }

    [Fact]
    public async Task Timed_out_initialization_can_retry_with_a_new_attempt()
    {
        using var lifetime = new WebViewInitializationLifetime();
        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            lifetime.EnsureInitializedAsync(
                async token =>
                {
                    attempts++;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                },
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None));

        await lifetime.EnsureInitializedAsync(
            token =>
            {
                attempts++;
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Timeout_bounds_an_initializer_that_ignores_cancellation()
    {
        var ignoredCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new WebViewInitializationLifetime();
        try
        {
            var initialization = lifetime.EnsureInitializedAsync(
                _ => ignoredCancellation.Task,
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None);

            var completed = await Task.WhenAny(
                initialization,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(initialization, completed);
            await Assert.ThrowsAsync<TimeoutException>(() => initialization);
        }
        finally
        {
            ignoredCancellation.TrySetResult();
            await lifetime.DisposeAsync();
        }
    }

    [Fact]
    public async Task Already_faulted_initializer_preserves_the_original_failure()
    {
        using var lifetime = new WebViewInitializationLifetime();
        var expected = new FileNotFoundException("missing packaged surface");

        var initialization = lifetime.EnsureInitializedAsync(
            _ => Task.FromException(expected),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        var actual = await Assert.ThrowsAsync<FileNotFoundException>(() => initialization);
        Assert.Same(expected, actual);
    }
}
