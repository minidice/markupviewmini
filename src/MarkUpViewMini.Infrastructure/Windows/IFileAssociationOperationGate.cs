namespace MarkUpViewMini.Infrastructure.Windows;

public interface IFileAssociationOperationGate
{
    Task RunAsync(Func<Task> operation);

    Task<T> RunAsync<T>(Func<Task<T>> operation);
}

public sealed class FileAssociationOperationGate : IFileAssociationOperationGate
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public static FileAssociationOperationGate ProcessWide { get; } = new();

    public async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
