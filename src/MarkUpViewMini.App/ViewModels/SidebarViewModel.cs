using MarkUpViewMini.App.Localization;
using System.Collections.ObjectModel;
using System.IO;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Folders;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.ViewModels;

public sealed class SidebarViewModel : ObservableObject, IDisposable
{
    private const long MaxBodyBytes = 10 * 1024 * 1024;

    private readonly FolderTreeService folderTreeService;
    private readonly IDocumentSearchService searchService;
    private readonly IReadOnlySet<string> treeExtensions;
    private readonly IReadOnlySet<string> fileNameSearchExtensions;
    private readonly IReadOnlySet<string> bodySearchExtensions;
    private readonly Action<Action> dispatcher;
    private readonly object searchGate = new();
    private string? rootPath;
    private RootFollowMode rootMode = RootFollowMode.KeepRoot;
    private FolderNode? tree;
    private SearchMode searchMode = SearchMode.FileName;
    private string searchText = string.Empty;
    private bool matchCase;
    private bool wholeWord;
    private bool useRegex;
    private SearchSummary? searchSummary;
    private string? searchError;
    private bool isSearching;
    private bool isRefreshingTree;
    private bool canActivateSearchResults;
    private long treeGeneration;
    private long treeRefreshGeneration;
    private long outlineGeneration;
    private long settingsGeneration;
    private SearchOperation? currentSearch;
    private bool disposed;

    public SidebarViewModel(
        FolderTreeService folderTreeService,
        IDocumentSearchService searchService,
        IReadOnlySet<string> treeExtensions,
        IReadOnlySet<string> fileNameSearchExtensions,
        IReadOnlySet<string> bodySearchExtensions,
        Action<Action> dispatcher)
    {
        this.folderTreeService = folderTreeService ?? throw new ArgumentNullException(nameof(folderTreeService));
        this.searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        this.treeExtensions = treeExtensions ?? throw new ArgumentNullException(nameof(treeExtensions));
        this.fileNameSearchExtensions = fileNameSearchExtensions ?? throw new ArgumentNullException(nameof(fileNameSearchExtensions));
        this.bodySearchExtensions = bodySearchExtensions ?? throw new ArgumentNullException(nameof(bodySearchExtensions));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public string? RootPath
    {
        get => rootPath;
        set => dispatcher(() => ChangeRoot(value));
    }

    public RootFollowMode RootMode
    {
        get => rootMode;
        set
        {
            var generation = Interlocked.Increment(ref settingsGeneration);
            dispatcher(() =>
            {
                if (IsCurrentSettings(generation))
                {
                    SetProperty(ref rootMode, value, nameof(RootMode));
                }
            });
        }
    }

    public FolderNode? Tree
    {
        get => tree;
        private set
        {
            if (SetProperty(ref tree, value))
            {
                OnPropertyChanged(nameof(CanActivateTree));
                OnPropertyChanged(nameof(RootError));
                OnPropertyChanged(nameof(HasRootError));
            }
        }
    }

    public string? RootError => Tree?.Error;

    public bool HasRootError => !string.IsNullOrWhiteSpace(RootError);

    public bool IsRefreshingTree
    {
        get => isRefreshingTree;
        private set
        {
            if (SetProperty(ref isRefreshingTree, value))
            {
                OnPropertyChanged(nameof(CanActivateTree));
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    public bool CanActivateTree => !IsRefreshingTree && Tree is not null;

    public bool CanSearch => !IsRefreshingTree && !string.IsNullOrWhiteSpace(RootPath);

    public bool CanActivateSearchResults
    {
        get => canActivateSearchResults;
        private set => SetProperty(ref canActivateSearchResults, value);
    }

    public ObservableCollection<OutlineItemViewModel> Outline { get; } = [];

    public SearchMode SearchMode
    {
        get => searchMode;
        set
        {
            var generation = Interlocked.Increment(ref settingsGeneration);
            dispatcher(() =>
            {
                if (IsCurrentSettings(generation))
                {
                    SetProperty(ref searchMode, value, nameof(SearchMode));
                }
            });
        }
    }

    public string SearchText
    {
        get => searchText;
        set => dispatcher(() => SetProperty(ref searchText, value, nameof(SearchText)));
    }

    public bool MatchCase
    {
        get => matchCase;
        set
        {
            var generation = Interlocked.Increment(ref settingsGeneration);
            dispatcher(() =>
            {
                if (IsCurrentSettings(generation))
                {
                    SetProperty(ref matchCase, value, nameof(MatchCase));
                }
            });
        }
    }

    public bool WholeWord
    {
        get => wholeWord;
        set
        {
            var generation = Interlocked.Increment(ref settingsGeneration);
            dispatcher(() =>
            {
                if (IsCurrentSettings(generation))
                {
                    SetProperty(ref wholeWord, value, nameof(WholeWord));
                }
            });
        }
    }

    public bool UseRegex
    {
        get => useRegex;
        set
        {
            var generation = Interlocked.Increment(ref settingsGeneration);
            dispatcher(() =>
            {
                if (IsCurrentSettings(generation))
                {
                    SetProperty(ref useRegex, value, nameof(UseRegex));
                }
            });
        }
    }

    public ObservableCollection<SearchResultGroupViewModel> SearchGroups { get; } = [];

    public SearchSummary? SearchSummary
    {
        get => searchSummary;
        private set
        {
            if (SetProperty(ref searchSummary, value))
            {
                OnPropertyChanged(nameof(SearchStatusText));
                OnPropertyChanged(nameof(HasSearchStatus));
            }
        }
    }

    public string? SearchError
    {
        get => searchError;
        private set
        {
            if (SetProperty(ref searchError, value))
            {
                OnPropertyChanged(nameof(HasSearchError));
            }
        }
    }

    public bool HasSearchError => !string.IsNullOrWhiteSpace(SearchError);

    public bool HasSearchStatus => SearchSummary is not null;

    public string? SearchStatusText => SearchSummary is null
        ? null
        : Strings.Format("sidebar.searchSummary", SearchSummary.FilesScanned, SearchSummary.SkippedLargeFiles, SearchSummary.UnreadableFiles) +
          (SearchSummary.WasCancelled ? Strings.Get("sidebar.searchCancelledSuffix") : string.Empty);

    public bool IsSearching
    {
        get => isSearching;
        private set => SetProperty(ref isSearching, value);
    }

    public async Task RefreshTreeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var selectedRoot = RootPath;
        var selectedGeneration = Volatile.Read(ref treeGeneration);
        if (string.IsNullOrWhiteSpace(selectedRoot))
        {
            throw new InvalidOperationException("A folder root must be selected before refreshing the tree.");
        }

        var selectedRefreshGeneration = Interlocked.Increment(ref treeRefreshGeneration);

        FolderNode? refreshedTree = null;
        try
        {
            refreshedTree = await folderTreeService.BuildAsync(
                selectedRoot,
                treeExtensions,
                cancellationToken);
        }
        finally
        {
            dispatcher(() =>
            {
                if (!IsCurrentTreeRefresh(selectedGeneration, selectedRefreshGeneration, selectedRoot))
                {
                    return;
                }

                if (refreshedTree is not null)
                {
                    Tree = refreshedTree;
                }

                if (!IsCurrentTreeRefresh(selectedGeneration, selectedRefreshGeneration, selectedRoot))
                {
                    return;
                }

                IsRefreshingTree = false;
            });
        }
    }

    public void SetOutline(IEnumerable<OutlineItemViewModel> items)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(items);
        var generation = Interlocked.Increment(ref outlineGeneration);
        var replacement = items.ToArray();

        dispatcher(() =>
        {
            if (!IsCurrentOutline(generation))
            {
                return;
            }

            Outline.Clear();
            if (!IsCurrentOutline(generation))
            {
                return;
            }

            foreach (var item in replacement)
            {
                Outline.Add(item);
                if (!IsCurrentOutline(generation))
                {
                    return;
                }
            }
        });
    }

    public void ApplySettings(SettingsV1 settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        var generation = Interlocked.Increment(ref settingsGeneration);
        dispatcher(() =>
        {
            if (!IsCurrentSettings(generation))
            {
                return;
            }

            SetProperty(ref rootMode, settings.RootMode, nameof(RootMode));
            if (!IsCurrentSettings(generation))
            {
                return;
            }

            SetProperty(ref searchMode, settings.SidebarSearchMode, nameof(SearchMode));
            if (!IsCurrentSettings(generation))
            {
                return;
            }

            SetProperty(ref matchCase, settings.SidebarSearchOptions.MatchCase, nameof(MatchCase));
            if (!IsCurrentSettings(generation))
            {
                return;
            }

            SetProperty(ref wholeWord, settings.SidebarSearchOptions.WholeWord, nameof(WholeWord));
            if (!IsCurrentSettings(generation))
            {
                return;
            }

            SetProperty(ref useRegex, settings.SidebarSearchOptions.UseRegex, nameof(UseRegex));
        });
    }

    public async Task SearchAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        var selectedRoot = RootPath ?? string.Empty;
        var selectedMode = SearchMode;
        var operation = new SearchOperation(Guid.NewGuid(), cancellationToken);
        _ = operation.Token.Register(
            () => MarkRequestCancelled(operation));
        SearchOperation? previous;
        var published = false;
        lock (searchGate)
        {
            if (!disposed)
            {
                previous = currentSearch;
                currentSearch = operation;
                published = true;
            }
            else
            {
                previous = null;
            }
        }

        if (!published)
        {
            operation.Complete();
            throw new ObjectDisposedException(nameof(SidebarViewModel));
        }

        try
        {
            previous?.CancelAndDisposeWhenComplete();

            dispatcher(() =>
            {
                if (!IsCurrentRequest(operation))
                {
                    return;
                }

                SetProperty(ref searchText, text, nameof(SearchText));
                if (!IsCurrentRequest(operation))
                {
                    return;
                }

                SearchGroups.Clear();
                if (!IsCurrentRequest(operation))
                {
                    return;
                }

                SearchSummary = null;
                if (!IsCurrentRequest(operation))
                {
                    return;
                }

                SearchError = null;
                if (!IsCurrentRequest(operation))
                {
                    return;
                }

                CanActivateSearchResults = true;
                if (!IsCurrentRequest(operation))
                {
                    return;
                }

                IsSearching = true;
            });

            if (operation.IsCancellationRequested)
            {
                MarkRequestCancelled(operation);
            }

            var query = new SearchQuery(
                operation.RequestId,
                selectedRoot,
                text,
                selectedMode,
                MatchCase: MatchCase,
                WholeWord: WholeWord,
                UseRegex: UseRegex,
                Extensions: selectedMode == SearchMode.FileName
                    ? fileNameSearchExtensions
                    : bodySearchExtensions,
                MaxBodyBytes: MaxBodyBytes);
            IAsyncEnumerator<SearchEvent> events;
            try
            {
                events = searchService.SearchAsync(query, operation.Token)
                    .GetAsyncEnumerator(operation.Token);
            }
            catch (Exception exception) when (IsQueryStartException(exception))
            {
                ApplyQueryError(operation, exception);
                return;
            }

            await using (events)
            {
                var receivedEvent = false;
                while (true)
                {
                    SearchEvent searchEvent;
                    try
                    {
                        if (!await events.MoveNextAsync())
                        {
                            break;
                        }

                        searchEvent = events.Current;
                        receivedEvent = true;
                    }
                    catch (OperationCanceledException) when (operation.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception) when (
                        !receivedEvent && IsQueryStartException(exception))
                    {
                        ApplyQueryError(operation, exception);
                        break;
                    }

                    if (searchEvent.RequestId != operation.RequestId)
                    {
                        continue;
                    }

                    if (searchEvent is SearchMatch match)
                    {
                        DispatchIfCurrentMatch(operation, () => AddMatch(selectedRoot, match));
                        continue;
                    }

                    if (searchEvent is SearchSummary summary)
                    {
                        ApplySummary(operation, summary);
                        break;
                    }
                }
            }
        }
        finally
        {
            DispatchIfCurrent(operation, () => IsSearching = false);
            ReleaseSearch(operation);
        }
    }

    private void ApplyQueryError(SearchOperation operation, Exception exception)
    {
        DispatchIfCurrentMatch(operation, () =>
        {
            SearchError = exception.Message;
            if (IsCurrentRequest(operation))
            {
                SearchSummary = null;
            }

            if (IsCurrentRequest(operation))
            {
                IsSearching = false;
            }
        });
    }

    private static bool IsQueryStartException(Exception exception) =>
        exception is SearchQueryException or ArgumentException;

    public void CancelSearch()
    {
        SearchOperation? operation;
        lock (searchGate)
        {
            operation = currentSearch;
        }

        operation?.Cancel();
    }

    internal bool CanActivateFolderNode(FolderNode node) =>
        CanActivateTree && ContainsNode(Tree, node);

    internal bool CanActivateSearchMatch(SearchMatch match) =>
        CanActivateSearchResults && SearchGroups.Any(group => group.Matches.Contains(match));

    public void Dispose()
    {
        SearchOperation? operation;
        lock (searchGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            operation = currentSearch;
            currentSearch = null;
        }

        operation?.CancelAndDisposeWhenComplete();
        GC.SuppressFinalize(this);
    }

    private void AddMatch(string selectedRoot, SearchMatch match)
    {
        var fullPath = Path.GetFullPath(match.Path);
        var relativePath = Path.GetRelativePath(selectedRoot, fullPath);
        var existingIndex = FindGroup(fullPath);
        if (existingIndex >= 0)
        {
            var existing = SearchGroups[existingIndex];
            SearchGroups[existingIndex] = existing with
            {
                Matches = [.. existing.Matches, match],
            };
            return;
        }

        var group = new SearchResultGroupViewModel(fullPath, relativePath, [match]);
        var insertIndex = 0;
        while (insertIndex < SearchGroups.Count && CompareGroups(SearchGroups[insertIndex], group) < 0)
        {
            insertIndex++;
        }

        SearchGroups.Insert(insertIndex, group);
    }

    private bool IsCurrentOutline(long generation) =>
        !Volatile.Read(ref disposed)
        && Volatile.Read(ref outlineGeneration) == generation;

    private bool IsCurrentSettings(long generation) =>
        !Volatile.Read(ref disposed)
        && Volatile.Read(ref settingsGeneration) == generation;

    private void ChangeRoot(string? value)
    {
        if (string.Equals(rootPath, value, StringComparison.Ordinal))
        {
            return;
        }

        SearchOperation? obsoleteSearch;
        lock (searchGate)
        {
            obsoleteSearch = currentSearch;
            currentSearch = null;
        }

        var generation = Interlocked.Increment(ref treeGeneration);
        obsoleteSearch?.CancelAndDisposeWhenComplete();
        if (!IsCurrentRootTransition(generation))
        {
            return;
        }

        var treeChanged = tree is not null;
        var summaryChanged = searchSummary is not null;
        var errorChanged = searchError is not null;
        var searchingChanged = isSearching;
        var resultActivationChanged = canActivateSearchResults;
        var newRefreshingState = !string.IsNullOrWhiteSpace(value);
        var refreshingChanged = isRefreshingTree != newRefreshingState;
        tree = null;
        searchSummary = null;
        searchError = null;
        isSearching = false;
        canActivateSearchResults = false;
        isRefreshingTree = newRefreshingState;
        rootPath = value;

        if (SearchGroups.Count > 0)
        {
            SearchGroups.Clear();
            if (!IsCurrentRootTransition(generation))
            {
                return;
            }
        }

        if (treeChanged && !NotifyRootTransition(
                generation,
                nameof(Tree),
                nameof(CanActivateTree),
                nameof(RootError),
                nameof(HasRootError)))
        {
            return;
        }

        if (summaryChanged && !NotifyRootTransition(
                generation,
                nameof(SearchSummary),
                nameof(SearchStatusText),
                nameof(HasSearchStatus)))
        {
            return;
        }

        if (errorChanged && !NotifyRootTransition(generation, nameof(SearchError), nameof(HasSearchError)))
        {
            return;
        }

        if (searchingChanged && !NotifyRootTransition(generation, nameof(IsSearching)))
        {
            return;
        }

        if (resultActivationChanged && !NotifyRootTransition(generation, nameof(CanActivateSearchResults)))
        {
            return;
        }

        if (refreshingChanged && !NotifyRootTransition(
                generation,
                nameof(IsRefreshingTree),
                nameof(CanActivateTree)))
        {
            return;
        }

        NotifyRootTransition(generation, nameof(RootPath), nameof(CanSearch));
    }

    private bool IsCurrentTreeRefresh(
        long generation,
        long refreshGeneration,
        string selectedRoot) =>
        !Volatile.Read(ref disposed)
        && Volatile.Read(ref treeGeneration) == generation
        && Volatile.Read(ref treeRefreshGeneration) == refreshGeneration
        && string.Equals(RootPath, selectedRoot, StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentRootTransition(long generation) =>
        !Volatile.Read(ref disposed) && Volatile.Read(ref treeGeneration) == generation;

    private bool NotifyRootTransition(long generation, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!IsCurrentRootTransition(generation))
            {
                return false;
            }

            OnPropertyChanged(propertyName);
        }

        return IsCurrentRootTransition(generation);
    }

    private static bool ContainsNode(FolderNode? root, FolderNode expected)
    {
        if (root is null)
        {
            return false;
        }

        if (ReferenceEquals(root, expected))
        {
            return true;
        }

        return root.Children.Any(child => ContainsNode(child, expected));
    }

    private int FindGroup(string fullPath)
    {
        for (var index = 0; index < SearchGroups.Count; index++)
        {
            if (string.Equals(SearchGroups[index].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int CompareGroups(
        SearchResultGroupViewModel left,
        SearchResultGroupViewModel right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
    }

    private void DispatchIfCurrent(SearchOperation operation, Action mutation)
    {
        dispatcher(() =>
        {
            if (IsCurrentRequest(operation))
            {
                mutation();
            }
        });
    }

    private void DispatchIfCurrentMatch(SearchOperation operation, Action mutation)
    {
        dispatcher(() =>
        {
            if (CanAcceptMatch(operation))
            {
                mutation();
            }
        });
    }

    private void ApplySummary(SearchOperation operation, SearchSummary summary)
    {
        dispatcher(() =>
        {
            if (!CanAcceptSummary(operation, summary))
            {
                return;
            }

            SearchSummary = summary;
            if (IsCurrentRequest(operation))
            {
                IsSearching = false;
            }
        });
    }

    private bool IsCurrentRequest(SearchOperation operation)
    {
        lock (searchGate)
        {
            return !disposed && ReferenceEquals(currentSearch, operation);
        }
    }

    private bool CanAcceptMatch(SearchOperation operation)
    {
        lock (searchGate)
        {
            return !disposed
                && ReferenceEquals(currentSearch, operation)
                && !operation.IsCancellationRequested;
        }
    }

    private bool CanAcceptSummary(SearchOperation operation, SearchSummary summary)
    {
        lock (searchGate)
        {
            return !disposed
                && ReferenceEquals(currentSearch, operation)
                && (!operation.IsCancellationRequested || summary.WasCancelled);
        }
    }

    private void MarkRequestCancelled(SearchOperation operation)
    {
        DispatchIfCurrent(operation, () => IsSearching = false);
    }

    private void ReleaseSearch(SearchOperation operation)
    {
        lock (searchGate)
        {
            if (ReferenceEquals(currentSearch, operation))
            {
                currentSearch = null;
            }
        }

        operation.Complete();
    }

    private sealed class SearchOperation
    {
        private readonly object lifecycleGate = new();
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationTokenRegistration externalCancellationRegistration;
        private int leases = 1;
        private bool ownerCompleted;
        private bool disposed;

        public SearchOperation(Guid requestId, CancellationToken cancellationToken)
        {
            RequestId = requestId;
            cancellation = new CancellationTokenSource();
            Token = cancellation.Token;
            externalCancellationRegistration = cancellationToken.Register(Cancel);
        }

        public Guid RequestId { get; }

        public CancellationToken Token { get; }

        public bool IsCancellationRequested => Token.IsCancellationRequested;

        public void Cancel() => CancelCore();

        public void CancelAndDisposeWhenComplete() => CancelCore();

        public void Complete()
        {
            lock (lifecycleGate)
            {
                if (ownerCompleted)
                {
                    return;
                }

                ownerCompleted = true;
            }

            _ = externalCancellationRegistration.Unregister();

            CancellationTokenSource? toDispose = null;
            lock (lifecycleGate)
            {
                leases--;
                if (leases == 0)
                {
                    disposed = true;
                    toDispose = cancellation;
                }
            }

            toDispose?.Dispose();
        }

        private void CancelCore()
        {
            CancellationTokenSource? leasedCancellation = null;
            lock (lifecycleGate)
            {
                if (!disposed)
                {
                    leases++;
                    leasedCancellation = cancellation;
                }
            }

            if (leasedCancellation is null)
            {
                return;
            }

            try
            {
                leasedCancellation.Cancel();
            }
            finally
            {
                ReleaseLease();
            }
        }

        private void ReleaseLease()
        {
            CancellationTokenSource? toDispose = null;
            lock (lifecycleGate)
            {
                leases--;
                if (leases == 0 && ownerCompleted && !disposed)
                {
                    disposed = true;
                    toDispose = cancellation;
                }
            }

            toDispose?.Dispose();
        }
    }
}
