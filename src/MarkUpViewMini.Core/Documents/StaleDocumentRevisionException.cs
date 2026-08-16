namespace MarkUpViewMini.Core.Documents;

public sealed class StaleDocumentRevisionException : InvalidOperationException
{
    public StaleDocumentRevisionException(long expectedRevision, long actualRevision)
        : base($"Expected document revision {expectedRevision}, but the current revision is {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}
