namespace MarkUpViewMini.Core.Search;

public sealed record SearchQuery(
    Guid RequestId,
    string Root,
    string Text,
    SearchMode Mode,
    bool MatchCase,
    bool WholeWord,
    bool UseRegex,
    IReadOnlySet<string> Extensions,
    long MaxBodyBytes);

public sealed class SearchQueryException : Exception
{
    public SearchQueryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
