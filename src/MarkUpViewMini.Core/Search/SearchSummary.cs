namespace MarkUpViewMini.Core.Search;

public sealed record SearchSummary(
    Guid RequestId,
    int FilesScanned,
    int SkippedLargeFiles,
    int UnreadableFiles,
    bool WasCancelled)
    : SearchEvent(RequestId);
