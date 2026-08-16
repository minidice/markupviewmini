using System.Collections.Concurrent;

namespace MarkUpViewMini.Infrastructure.Files;

public sealed class DocumentSaveArbiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates =
        new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var normalizedPath = Path.GetFullPath(targetPath);
        var gate = gates.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
