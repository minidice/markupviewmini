namespace MarkUpViewMini.Core.Search;

public sealed record SearchMatch(
    Guid RequestId,
    string Path,
    int? LineNumber,
    string Preview,
    int MatchStart,
    int MatchLength)
    : SearchEvent(RequestId);
