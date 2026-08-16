namespace MarkUpViewMini.App.Web;

internal static class WebSurfaceActivationTransaction
{
    public static bool TryPost(
        WebSurfaceActivationCoordinator coordinator,
        WebActivationStamp activation,
        Guid requestId,
        long revision,
        Action post)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(post);
        if (!coordinator.IsCurrent(activation) ||
            coordinator.State != WebSurfaceLifecycleState.Ready)
        {
            return false;
        }

        post();
        return coordinator.TryRecordPosted(activation, requestId, revision);
    }
}
