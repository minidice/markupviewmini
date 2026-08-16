namespace MarkUpViewMini.App.Composition;

internal sealed class SettingsShutdownCoordinator(IAsyncDisposable lifetime)
{
    private readonly object gate = new();
    private Task? completion;

    public void Complete()
    {
        Task work;
        lock (gate)
        {
            completion ??= Task.Run(async () =>
            {
                try
                {
                    await lifetime.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            });
            work = completion;
        }

        work.GetAwaiter().GetResult();
    }
}
