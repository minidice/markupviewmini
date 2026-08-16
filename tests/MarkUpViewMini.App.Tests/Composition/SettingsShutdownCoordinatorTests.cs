using MarkUpViewMini.App.Composition;

namespace MarkUpViewMini.App.Tests.Composition;

public sealed class SettingsShutdownCoordinatorTests
{
    [Fact]
    public async Task Complete_waits_for_the_owned_flush_and_disposes_exactly_once()
    {
        // Break caught: WPF returns from async-void OnExit while the final settings write is still blocked.
        var lifetime = new BlockedLifetime();
        var coordinator = new SettingsShutdownCoordinator(lifetime);

        var first = Task.Run(coordinator.Complete);
        await lifetime.Started.Task;
        var second = Task.Run(coordinator.Complete);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        lifetime.Release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public void Complete_keeps_private_dispose_failures_nonfatal()
    {
        var coordinator = new SettingsShutdownCoordinator(
            new FailingLifetime(new IOException("private find text")));

        var exception = Record.Exception(coordinator.Complete);

        Assert.Null(exception);
    }

    private sealed class BlockedLifetime : IAsyncDisposable
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            Started.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class FailingLifetime(Exception failure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(failure);
    }
}
