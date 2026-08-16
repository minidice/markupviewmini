namespace MarkUpViewMini.App.Web;

public sealed class WebViewInitializationLifetime : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource surfaceCancellation = new();
    private readonly CancellationToken surfaceToken;
    private CancellationTokenSource? attemptCancellation;
    private Task? attemptTask;
    private Task? runnerTask;
    private Task? disposalTask;
    private long generation;
    private bool disposed;

    public WebViewInitializationLifetime()
    {
        surfaceToken = surfaceCancellation.Token;
    }

    public CancellationToken Token => surfaceToken;

    public Task EnsureInitializedAsync(
        Func<CancellationToken, Task> initialize,
        TimeSpan timeout,
        CancellationToken requestCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (attemptTask is null)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    surfaceCancellation.Token);
                if (timeout != Timeout.InfiniteTimeSpan)
                {
                    cancellation.CancelAfter(timeout);
                }

                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var attemptGeneration = ++generation;
                var attemptToken = cancellation.Token;
                attemptCancellation = cancellation;
                attemptTask = completion.Task;
                task = completion.Task;
                runnerTask = RunAttemptAsync(
                    initialize,
                    attemptToken,
                    surfaceToken,
                    cancellation,
                    attemptGeneration,
                    completion);
            }
            else
            {
                task = attemptTask;
            }
        }

        return WaitForAttemptAsync(task, requestCancellationToken);
    }

    public void Reset()
    {
        CancellationTokenSource? cancellation;
        Task? runner;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            generation++;
            cancellation = attemptCancellation;
            runner = runnerTask;
            attemptCancellation = null;
            attemptTask = null;
            runnerTask = null;
        }

        cancellation?.Cancel();
        ObserveFault(runner);
    }

    public void Dispose()
    {
        ObserveFault(BeginDispose());
    }

    public async ValueTask DisposeAsync()
    {
        var completion = BeginDispose();
        if (completion is not null)
        {
            await completion.ConfigureAwait(false);
        }
    }

    private Task? BeginDispose()
    {
        CancellationTokenSource? cancellation;
        Task? runner;
        lock (gate)
        {
            if (disposed)
            {
                return disposalTask;
            }

            disposed = true;
            generation++;
            cancellation = attemptCancellation;
            runner = runnerTask;
            attemptCancellation = null;
            attemptTask = null;
            runnerTask = null;
            disposalTask = runner ?? Task.CompletedTask;
        }

        surfaceCancellation.Cancel();
        cancellation?.Cancel();
        surfaceCancellation.Dispose();
        return disposalTask;
    }

    private async Task RunAttemptAsync(
        Func<CancellationToken, Task> initialize,
        CancellationToken attemptToken,
        CancellationToken stableSurfaceToken,
        CancellationTokenSource cancellation,
        long attemptGeneration,
        TaskCompletionSource completion)
    {
        var succeeded = false;
        try
        {
            await initialize(attemptToken)
                .WaitAsync(attemptToken)
                .ConfigureAwait(false);
            succeeded = true;
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
            when (attemptToken.IsCancellationRequested &&
                  !stableSurfaceToken.IsCancellationRequested)
        {
            completion.TrySetException(new TimeoutException(
                "WebView2 initialization did not complete in time.",
                exception));
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled(stableSurfaceToken.IsCancellationRequested
                ? stableSurfaceToken
                : attemptToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (gate)
            {
                if (generation == attemptGeneration)
                {
                    attemptCancellation = null;
                    if (!succeeded)
                    {
                        attemptTask = null;
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task WaitForAttemptAsync(
        Task task,
        CancellationToken requestCancellationToken)
    {
        await task.WaitAsync(requestCancellationToken).ConfigureAwait(false);
    }

    private static void ObserveFault(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
