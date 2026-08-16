using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Core.Persistence;

public abstract record SaveResult
{
    private SaveResult()
    {
    }

    public sealed record Saved(DiskFileVersion Version, long SavedRevision) : SaveResult;

    public sealed record Conflict(DiskFileVersion? Current) : SaveResult;
}
