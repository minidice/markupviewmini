namespace MarkUpViewMini.App.Web;

public enum WebSurfaceLifecycleState
{
    Uninitialized,
    Initializing,
    AwaitingReady,
    Ready,
    Failed,
}

public enum WebSurfaceFailure
{
    None,
    InitializationFailed,
    NavigationFailed,
    ProcessFailed,
    RenderFailed,
    Timeout,
}

public readonly record struct WebActivationStamp(long Generation, Guid TabId);

public readonly record struct WebResponseContext(Guid RequestId, Guid TabId, long Revision);

public sealed class WebSurfaceActivationCoordinator
{
    private long generation;
    private Guid? bootstrapTabId;

    internal event Action? CurrentResponseChanged;

    public WebSurfaceLifecycleState State { get; private set; }

    public WebSurfaceFailure Failure { get; private set; }

    public Guid? RequestedTabId { get; private set; }

    public WebResponseContext? CurrentResponse { get; private set; }

    public bool HasActiveDocument { get; private set; }

    public long CurrentGeneration => generation;

    public bool CanRetry => State == WebSurfaceLifecycleState.Failed && RequestedTabId is not null;

    public WebActivationStamp BeginActivation(Guid tabId)
    {
        if (tabId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty tab ID is required.", nameof(tabId));
        }

        if (State == WebSurfaceLifecycleState.Failed)
        {
            State = WebSurfaceLifecycleState.Uninitialized;
            Failure = WebSurfaceFailure.None;
            bootstrapTabId = null;
        }

        generation++;
        RequestedTabId = tabId;
        var responseChanged = CurrentResponse is not null;
        CurrentResponse = null;
        HasActiveDocument = false;
        var activation = new WebActivationStamp(generation, tabId);
        if (responseChanged)
        {
            CurrentResponseChanged?.Invoke();
        }

        return activation;
    }

    public WebActivationStamp BeginRetry()
    {
        if (!CanRetry)
        {
            throw new InvalidOperationException("The document surface is not waiting for a retry.");
        }

        State = WebSurfaceLifecycleState.Uninitialized;
        Failure = WebSurfaceFailure.None;
        bootstrapTabId = null;
        generation++;
        return new WebActivationStamp(generation, RequestedTabId!.Value);
    }

    public bool IsCurrent(WebActivationStamp activation) =>
        activation.Generation == generation && RequestedTabId == activation.TabId;

    public void MarkInitializing(WebActivationStamp activation)
    {
        if (IsCurrent(activation) &&
            State is not WebSurfaceLifecycleState.Ready and
                not WebSurfaceLifecycleState.AwaitingReady)
        {
            State = WebSurfaceLifecycleState.Initializing;
        }
    }

    public void MarkAwaitingReady(WebActivationStamp activation, Guid readyTabId)
    {
        if (!IsCurrent(activation))
        {
            return;
        }

        State = WebSurfaceLifecycleState.AwaitingReady;
        bootstrapTabId = readyTabId;
    }

    public bool TryMarkReady(Guid readyTabId)
    {
        if (State != WebSurfaceLifecycleState.AwaitingReady || bootstrapTabId != readyTabId)
        {
            return false;
        }

        State = WebSurfaceLifecycleState.Ready;
        Failure = WebSurfaceFailure.None;
        return true;
    }

    public bool TryRecordPosted(
        WebActivationStamp activation,
        Guid requestId,
        long revision)
    {
        if (!IsCurrent(activation) ||
            State != WebSurfaceLifecycleState.Ready ||
            requestId == Guid.Empty ||
            revision < 0)
        {
            return false;
        }

        CurrentResponse = new WebResponseContext(requestId, activation.TabId, revision);
        HasActiveDocument = true;
        CurrentResponseChanged?.Invoke();
        return true;
    }

    public void MarkFailed(WebActivationStamp activation, WebSurfaceFailure failure)
    {
        if (!IsCurrent(activation))
        {
            return;
        }

        MarkFailed(failure);
    }

    public bool TryMarkRenderFailed(WebResponseContext response)
    {
        if (State != WebSurfaceLifecycleState.Ready || CurrentResponse != response)
        {
            return false;
        }

        MarkFailed(WebSurfaceFailure.RenderFailed);
        return true;
    }

    public bool TryUpdateCurrentResponse(
        WebResponseContext expected,
        Guid requestId,
        long revision)
    {
        if (State != WebSurfaceLifecycleState.Ready ||
            CurrentResponse != expected ||
            requestId == Guid.Empty ||
            revision < 0)
        {
            return false;
        }

        CurrentResponse = new WebResponseContext(requestId, expected.TabId, revision);
        CurrentResponseChanged?.Invoke();
        return true;
    }

    public void MarkFailed(WebSurfaceFailure failure)
    {
        if (RequestedTabId is null)
        {
            return;
        }

        if (failure == WebSurfaceFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        generation++;
        State = WebSurfaceLifecycleState.Failed;
        Failure = failure;
        bootstrapTabId = null;
        var responseChanged = CurrentResponse is not null;
        CurrentResponse = null;
        HasActiveDocument = false;
        if (responseChanged)
        {
            CurrentResponseChanged?.Invoke();
        }
    }

    public void CancelActivation(WebActivationStamp activation)
    {
        if (!IsCurrent(activation))
        {
            return;
        }

        generation++;
        bootstrapTabId = null;
        var responseChanged = CurrentResponse is not null;
        CurrentResponse = null;
        HasActiveDocument = false;
        Failure = WebSurfaceFailure.None;
        State = WebSurfaceLifecycleState.Uninitialized;
        if (responseChanged)
        {
            CurrentResponseChanged?.Invoke();
        }
    }

    public void Deactivate()
    {
        generation++;
        RequestedTabId = null;
        bootstrapTabId = null;
        var responseChanged = CurrentResponse is not null;
        CurrentResponse = null;
        HasActiveDocument = false;
        Failure = WebSurfaceFailure.None;
        State = WebSurfaceLifecycleState.Uninitialized;
        if (responseChanged)
        {
            CurrentResponseChanged?.Invoke();
        }
    }
}
