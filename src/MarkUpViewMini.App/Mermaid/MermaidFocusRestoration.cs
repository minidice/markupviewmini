using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Mermaid;

internal sealed record MermaidActionIdentity(Guid ActionId, string ActionOrigin);

internal sealed record MermaidFocusRequest(
    WebMessageOwner Owner,
    MermaidActionIdentity Action,
    object Control,
    string Json);

internal sealed record MermaidFocusCompletedMessage(
    WebMessageOwner Owner,
    MermaidActionIdentity Action);

internal sealed class MermaidFocusRestoration : IDisposable
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(1);
    private readonly object gate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> waitAsync;
    private Pending? pending;
    private bool disposed;

    internal MermaidFocusRestoration(
        Func<TimeSpan, CancellationToken, Task>? waitAsync = null)
    {
        this.waitAsync = waitAsync ?? Task.Delay;
    }

    internal bool HasPending
    {
        get
        {
            lock (gate)
            {
                return pending is not null;
            }
        }
    }

    internal Task Begin(MermaidFocusRequest request, Action<string> postMessage)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(postMessage);
        Pending created;
        Pending? superseded;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            superseded = pending;
            created = new Pending(request);
            pending = created;
        }

        Cancel(superseded);
        try
        {
            postMessage(request.Json);
        }
        catch
        {
            Clear(created);
            throw;
        }

        return ExpireAsync(created);
    }

    internal bool TryAcknowledge(
        MermaidFocusCompletedMessage acknowledgement,
        WebResponseContext? current,
        Guid windowId,
        object currentControl,
        Action focus)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ArgumentNullException.ThrowIfNull(currentControl);
        ArgumentNullException.ThrowIfNull(focus);
        Pending? accepted;
        lock (gate)
        {
            accepted = pending;
            if (disposed ||
                accepted is null ||
                acknowledgement.Owner != accepted.Request.Owner ||
                acknowledgement.Action != accepted.Request.Action ||
                current is not { } response ||
                response.RequestId != accepted.Request.Owner.RequestId ||
                response.TabId != accepted.Request.Owner.TabId ||
                response.Revision != accepted.Request.Owner.DocumentRevision ||
                windowId != accepted.Request.Owner.WindowId ||
                !ReferenceEquals(currentControl, accepted.Request.Control))
            {
                return false;
            }

            pending = null;
        }

        Cancel(accepted);
        focus();
        return true;
    }

    internal void Cancel()
    {
        Pending? canceled;
        lock (gate)
        {
            canceled = pending;
            pending = null;
        }

        Cancel(canceled);
    }

    public void Dispose()
    {
        Pending? canceled;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            canceled = pending;
            pending = null;
        }

        Cancel(canceled);
    }

    private async Task ExpireAsync(Pending expected)
    {
        try
        {
            await waitAsync(AcknowledgementTimeout, expected.Token);
        }
        catch (OperationCanceledException) when (expected.Token.IsCancellationRequested)
        {
        }
        finally
        {
            Clear(expected);
        }
    }

    private void Clear(Pending expected)
    {
        lock (gate)
        {
            if (ReferenceEquals(pending, expected))
            {
                pending = null;
            }
        }

        Cancel(expected);
    }

    private static void Cancel(Pending? value)
    {
        value?.Cancel();
    }

    private sealed class Pending
    {
        private int canceled;

        internal Pending(MermaidFocusRequest request)
        {
            Request = request;
            Cancellation = new CancellationTokenSource();
            Token = Cancellation.Token;
        }

        internal MermaidFocusRequest Request { get; }

        internal CancellationTokenSource Cancellation { get; }

        internal CancellationToken Token { get; }

        internal void Cancel()
        {
            if (Interlocked.Exchange(ref canceled, 1) != 0)
            {
                return;
            }

            try
            {
                Cancellation.Cancel();
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }
}
