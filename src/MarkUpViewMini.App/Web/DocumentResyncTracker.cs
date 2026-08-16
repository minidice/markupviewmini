namespace MarkUpViewMini.App.Web;

internal readonly record struct DocumentResyncRequest(Guid RequestId, long Revision);

internal sealed class DocumentResyncTracker
{
    private WebResponseContext? owner;
    private DocumentResyncRequest? pending;

    internal DocumentResyncRequest GetOrBegin(WebResponseContext current, long revision)
    {
        if (pending is { } existing && owner == current)
        {
            return existing;
        }

        var created = new DocumentResyncRequest(Guid.NewGuid(), revision);
        owner = current;
        pending = created;
        return created;
    }

    internal bool IsCurrent(WebResponseContext current, Guid requestId, long revision) =>
        owner == current &&
        pending is { } existing &&
        existing.RequestId == requestId &&
        existing.Revision == revision;

    internal bool TryTake(
        WebResponseContext current,
        long revision,
        out DocumentResyncRequest request)
    {
        if (owner != current || pending is not { } existing || existing.Revision != revision)
        {
            request = default;
            Clear();
            return false;
        }

        request = existing;
        Clear();
        return true;
    }

    internal void Clear()
    {
        owner = null;
        pending = null;
    }
}
