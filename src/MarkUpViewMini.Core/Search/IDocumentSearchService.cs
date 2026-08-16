namespace MarkUpViewMini.Core.Search;

public interface IDocumentSearchService
{
    IAsyncEnumerable<SearchEvent> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
