using MarkUpViewMini.Core.Search;

namespace MarkUpViewMini.App.ViewModels;

public sealed record SearchResultGroupViewModel(
    string FullPath,
    string RelativePath,
    IReadOnlyList<SearchMatch> Matches);
