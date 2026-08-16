namespace MarkUpViewMini.Infrastructure.Windows;

public interface IBackgroundExecutor
{
    Task RunAsync(Action action);

    Task<T> RunAsync<T>(Func<T> action);
}

public sealed class ThreadPoolBackgroundExecutor : IBackgroundExecutor
{
    public Task RunAsync(Action action) => Task.Run(action);

    public Task<T> RunAsync<T>(Func<T> action) => Task.Run(action);
}
