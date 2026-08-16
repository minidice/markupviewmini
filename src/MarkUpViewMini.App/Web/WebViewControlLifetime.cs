namespace MarkUpViewMini.App.Web;

public sealed class WebViewControlLifetime<TControl> : IDisposable
    where TControl : class
{
    private readonly Func<TControl> create;
    private readonly Action<TControl> attach;
    private readonly Action<TControl> detach;
    private readonly Action<TControl> dispose;
    private TControl? current;

    public WebViewControlLifetime(
        Func<TControl> create,
        Action<TControl> attach,
        Action<TControl> detach,
        Action<TControl> dispose)
    {
        this.create = create ?? throw new ArgumentNullException(nameof(create));
        this.attach = attach ?? throw new ArgumentNullException(nameof(attach));
        this.detach = detach ?? throw new ArgumentNullException(nameof(detach));
        this.dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        current = CreateAndAttach();
    }

    public TControl Current =>
        current ?? throw new ObjectDisposedException(nameof(WebViewControlLifetime<TControl>));

    public async Task EnsureInitializedAsync(
        WebViewInitializationLifetime initializationLifetime,
        Func<TControl, CancellationToken, Task> initialize,
        Action unregister,
        TimeSpan timeout,
        CancellationToken requestCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializationLifetime);
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(unregister);
        var control = Current;
        try
        {
            await initializationLifetime.EnsureInitializedAsync(
                token => initialize(control, token),
                timeout,
                requestCancellationToken);
        }
        catch (TimeoutException)
        {
            if (ReferenceEquals(current, control))
            {
                try
                {
                    Recreate(unregister);
                }
                catch when (current is not null && !ReferenceEquals(current, control))
                {
                    // Handler removal failed, but Recreate still installed the replacement.
                }
            }

            throw;
        }
    }

    public void Recreate(Action unregister)
    {
        ArgumentNullException.ThrowIfNull(unregister);
        ObjectDisposedException.ThrowIf(current is null, this);

        try
        {
            unregister();
        }
        finally
        {
            ReleaseCurrent();
            current = CreateAndAttach();
        }
    }

    public void Dispose(Action unregister)
    {
        ArgumentNullException.ThrowIfNull(unregister);
        if (current is null)
        {
            return;
        }

        try
        {
            unregister();
        }
        finally
        {
            ReleaseCurrent();
        }
    }

    public void Dispose() => Dispose(() => { });

    private TControl CreateAndAttach()
    {
        var created = create();
        try
        {
            attach(created);
            return created;
        }
        catch
        {
            dispose(created);
            throw;
        }
    }

    private void ReleaseCurrent()
    {
        var releasing = current;
        current = null;
        if (releasing is null)
        {
            return;
        }

        try
        {
            detach(releasing);
        }
        finally
        {
            dispose(releasing);
        }
    }
}
