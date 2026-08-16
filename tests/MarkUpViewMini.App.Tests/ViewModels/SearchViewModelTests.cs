using System.Runtime.CompilerServices;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Folders;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class SearchViewModelTests
{
    [Fact]
    public async Task SearchAsync_cancels_previous_request_and_rejects_its_late_match_and_completion()
    {
        // Break caught: a cancellation-resistant old stream can replace newer visible state or end its progress indicator.
        var oldStarted = NewSignal();
        var releaseOld = NewSignal();
        var newStarted = NewSignal();
        var releaseNew = NewSignal();
        CancellationToken oldToken = default;
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, cancellationToken) => query.Text switch
        {
            "old" => OldEvents(query, cancellationToken),
            "new" => NewEvents(query, cancellationToken),
            _ => throw new InvalidOperationException(),
        });
        using var viewModel = CreateViewModel(search, root);

        var oldSearch = viewModel.SearchAsync("old", CancellationToken.None);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var newSearch = viewModel.SearchAsync("new", CancellationToken.None);
        await newStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(oldToken.IsCancellationRequested);
        releaseOld.SetResult();
        await oldSearch.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsSearching);
        Assert.Empty(viewModel.SearchGroups);
        Assert.Null(viewModel.SearchSummary);

        releaseNew.SetResult();
        await newSearch.WaitAsync(TimeSpan.FromSeconds(1));
        var match = Assert.Single(Assert.Single(viewModel.SearchGroups).Matches);
        Assert.Equal("new result", match.Preview);
        Assert.False(viewModel.IsSearching);

        async IAsyncEnumerable<SearchEvent> OldEvents(
            SearchQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            oldToken = cancellationToken;
            oldStarted.SetResult();
            await releaseOld.Task;
            yield return Match(query, "old.md", "old result", 1);
            yield return new SearchSummary(query.RequestId, 1, 0, 0, true);
        }

        async IAsyncEnumerable<SearchEvent> NewEvents(
            SearchQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            newStarted.SetResult();
            await releaseNew.Task;
            yield return Match(query, "new.md", "new result", 1);
            yield return new SearchSummary(query.RequestId, 1, 0, 0, false);
        }
    }

    [Fact]
    public async Task SearchAsync_groups_normalized_paths_sorts_groups_and_preserves_match_order()
    {
        // Break caught: grouping raw path spellings duplicates files, while global sorting can reorder streamed matches within a file.
        var root = Path.GetFullPath("search-root");
        var canonicalA = Path.Combine(root, "a.md");
        var alternateA = Path.Combine(root, "folder", "..", "a.md");
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = CreateViewModel(search, root);
        viewModel.SearchMode = SearchMode.Body;

        await viewModel.SearchAsync("needle", CancellationToken.None);

        Assert.Equal(["a.md", "b.md"], viewModel.SearchGroups.Select(group => group.RelativePath));
        var aGroup = viewModel.SearchGroups[0];
        Assert.Equal(Path.GetFullPath(canonicalA), aGroup.FullPath);
        Assert.Equal(["first", "second"], aGroup.Matches.Select(match => match.Preview));

        async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            await Task.Yield();
            yield return Match(query, "b.md", "third", 3);
            yield return new SearchMatch(query.RequestId, alternateA, 1, "first", 0, 6);
            yield return new SearchMatch(query.RequestId, canonicalA, 2, "second", 0, 6);
            yield return new SearchSummary(query.RequestId, 2, 0, 0, false);
        }
    }

    [Fact]
    public async Task SearchAsync_accepts_only_the_first_terminal_summary()
    {
        // Break caught: duplicate terminal events can overwrite the authoritative counts or accept matches after completion.
        var root = Path.GetFullPath("search-root");
        var firstSummary = new SearchSummary(Guid.Empty, 3, 1, 2, false);
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = CreateViewModel(search, root);

        await viewModel.SearchAsync("needle", CancellationToken.None);

        var summary = Assert.IsType<SearchSummary>(viewModel.SearchSummary);
        Assert.Equal(3, summary.FilesScanned);
        Assert.Equal(1, summary.SkippedLargeFiles);
        Assert.Equal(2, summary.UnreadableFiles);
        Assert.Empty(viewModel.SearchGroups);
        Assert.False(viewModel.IsSearching);

        async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            await Task.Yield();
            yield return firstSummary with { RequestId = query.RequestId };
            yield return Match(query, "too-late.md", "too late", 4);
            yield return new SearchSummary(query.RequestId, 99, 99, 99, true);
        }
    }

    [Fact]
    public async Task First_terminal_summary_stops_enumeration_immediately()
    {
        // Break caught: continuing after a terminal summary can hang forever on a broken producer.
        var continuedAfterSummary = NewSignal();
        var releaseBrokenProducer = NewSignal();
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = CreateViewModel(search, root);

        var activeSearch = viewModel.SearchAsync("needle", CancellationToken.None);
        try
        {
            var firstCompletion = await Task.WhenAny(activeSearch, continuedAfterSummary.Task)
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Same(activeSearch, firstCompletion);
            await activeSearch;
            Assert.Equal(1, Assert.IsType<SearchSummary>(viewModel.SearchSummary).FilesScanned);
        }
        finally
        {
            releaseBrokenProducer.TrySetResult();
            await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        }

        async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            yield return new SearchSummary(query.RequestId, 1, 0, 0, false);
            continuedAfterSummary.SetResult();
            await releaseBrokenProducer.Task;
            throw new SearchQueryException("must not be observed");
        }
    }

    [Fact]
    public async Task Summary_notification_reentrancy_cannot_end_the_new_search()
    {
        // Break caught: an old compound callback can keep mutating after PropertyChanged starts a newer request.
        var newStarted = NewSignal();
        var releaseNew = NewSignal();
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, _) => query.Text == "old"
            ? OldEvents(query)
            : NewEvents(query));
        using var viewModel = CreateViewModel(search, root);
        Task? newSearch = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SidebarViewModel.SearchSummary)
                && viewModel.SearchSummary?.FilesScanned == 1)
            {
                newSearch = viewModel.SearchAsync("new", CancellationToken.None);
            }
        };

        await viewModel.SearchAsync("old", CancellationToken.None);
        await newStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("new", viewModel.SearchText);
        Assert.True(viewModel.IsSearching);
        Assert.Null(viewModel.SearchSummary);

        releaseNew.SetResult();
        Assert.NotNull(newSearch);
        await newSearch.WaitAsync(TimeSpan.FromSeconds(1));

        static async IAsyncEnumerable<SearchEvent> OldEvents(SearchQuery query)
        {
            await Task.Yield();
            yield return new SearchSummary(query.RequestId, 1, 0, 0, false);
        }

        async IAsyncEnumerable<SearchEvent> NewEvents(SearchQuery query)
        {
            newStarted.SetResult();
            await releaseNew.Task;
            yield return new SearchSummary(query.RequestId, 2, 0, 0, false);
        }
    }

    [Fact]
    public async Task SearchText_notification_reentrancy_cannot_dispose_an_unregistered_operation()
    {
        // Break caught: publishing a CTS before registering cancellation lets a reentrant new search dispose it too early.
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = CreateViewModel(search, root);
        Task? newSearch = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SidebarViewModel.SearchText)
                && viewModel.SearchText == "old")
            {
                newSearch = viewModel.SearchAsync("new", CancellationToken.None);
            }
        };

        await viewModel.SearchAsync("old", CancellationToken.None);
        Assert.NotNull(newSearch);
        await newSearch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("new", viewModel.SearchText);
        Assert.Equal(2, Assert.IsType<SearchSummary>(viewModel.SearchSummary).FilesScanned);

        static async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            await Task.Yield();
            yield return new SearchSummary(
                query.RequestId,
                query.Text == "new" ? 2 : 1,
                0,
                0,
                false);
        }
    }

    [Fact]
    public async Task Completion_does_not_wait_for_a_dispatch_blocked_cancellation_callback()
    {
        // Break caught: disposing a registration can deadlock when its active callback is waiting for the UI dispatcher.
        var searchStarted = NewSignal();
        var releaseSearch = NewSignal();
        var cancellationCallbackEntered = NewSignal();
        var finalDispatchEntered = NewSignal();
        var releaseCancellationCallback = NewSignal();
        var startBlocking = 0;
        var blockedOnce = 0;
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, _) => Events());
        void Dispatch(Action action)
        {
            if (Volatile.Read(ref startBlocking) == 1
                && Interlocked.CompareExchange(ref blockedOnce, 1, 0) == 0)
            {
                cancellationCallbackEntered.SetResult();
                releaseCancellationCallback.Task.GetAwaiter().GetResult();
            }
            else if (Volatile.Read(ref blockedOnce) == 1)
            {
                finalDispatchEntered.TrySetResult();
            }

            action();
        }

        using var viewModel = CreateViewModel(search, root, Dispatch);
        using var cancellation = new CancellationTokenSource();
        var activeSearch = viewModel.SearchAsync("needle", cancellation.Token);
        await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Volatile.Write(ref startBlocking, 1);
        var cancelTask = Task.Run(cancellation.Cancel);
        await cancellationCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseSearch.SetResult();
        await finalDispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseCancellationCallback.TrySetResult();
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(1));
            await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        }

        async IAsyncEnumerable<SearchEvent> Events()
        {
            searchStarted.SetResult();
            await releaseSearch.Task;
            yield break;
        }
    }

    [Fact]
    public async Task SearchAsync_builds_the_approved_query_and_dispatches_all_async_observable_mutations()
    {
        // Break caught: dropping root/mode/limits from the query or mutating collections from the stream thread breaks sidebar behavior.
        SearchQuery? captured = null;
        var dispatcher = new RecordingDispatcher();
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = new SidebarViewModel(
            new FolderTreeService(),
            search,
            MarkdownExtensions(),
            MarkdownExtensions(),
            MarkdownExtensions(),
            dispatcher.Dispatch)
        {
            RootPath = root,
            SearchMode = SearchMode.Body,
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SidebarViewModel.SearchText)
                or nameof(SidebarViewModel.SearchSummary)
                or nameof(SidebarViewModel.IsSearching))
            {
                Assert.True(dispatcher.IsDispatching);
            }
        };
        viewModel.SearchGroups.CollectionChanged += (_, _) => Assert.True(dispatcher.IsDispatching);

        await viewModel.SearchAsync("needle", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(root, captured.Root);
        Assert.Equal("needle", captured.Text);
        Assert.Equal(SearchMode.Body, captured.Mode);
        Assert.False(captured.MatchCase);
        Assert.False(captured.WholeWord);
        Assert.False(captured.UseRegex);
        Assert.Equal(10 * 1024 * 1024, captured.MaxBodyBytes);
        Assert.True(captured.Extensions.SetEquals([".md", ".markdown"]));
        Assert.Equal("needle", viewModel.SearchText);

        async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            captured = query;
            await Task.Yield();
            yield return Match(query, "guide.md", "needle", 1);
            yield return new SearchSummary(query.RequestId, 1, 0, 0, false);
        }
    }

    [Fact]
    public async Task SearchAsync_exposes_query_errors_without_a_terminal_summary()
    {
        // Break caught: treating a query-start failure as a per-file summary hides the validation error from the user.
        var search = new DelegateSearchService((query, cancellationToken) => FailingEvents(cancellationToken));
        using var viewModel = CreateViewModel(search, Path.GetFullPath("search-root"));

        await viewModel.SearchAsync("[", CancellationToken.None);

        Assert.Equal("Invalid search expression.", viewModel.SearchError);
        Assert.Null(viewModel.SearchSummary);
        Assert.Empty(viewModel.SearchGroups);
        Assert.False(viewModel.IsSearching);

        static async IAsyncEnumerable<SearchEvent> FailingEvents(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new SearchQueryException("Invalid search expression.");
            }

            yield break;
        }
    }

    [Fact]
    public async Task Observer_argument_fault_is_not_mislabeled_as_a_query_error()
    {
        // Break caught: a broad catch around dispatched mutations converts observer bugs into user validation state.
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = CreateViewModel(search, root);
        viewModel.SearchGroups.CollectionChanged += (_, _) =>
            throw new ArgumentException("observer fault");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            viewModel.SearchAsync("needle", CancellationToken.None));

        Assert.Equal("observer fault", exception.Message);
        Assert.Null(viewModel.SearchError);

        static async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            await Task.Yield();
            yield return Match(query, "guide.md", "needle", 1);
        }
    }

    [Fact]
    public async Task Service_argument_fault_after_streaming_starts_is_not_a_query_error()
    {
        // Break caught: only startup validation is user state; a later service fault must remain an exception.
        var root = Path.GetFullPath("search-root");
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = CreateViewModel(search, root);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            viewModel.SearchAsync("needle", CancellationToken.None));

        Assert.Equal("late service fault", exception.Message);
        Assert.Null(viewModel.SearchError);
        Assert.Single(viewModel.SearchGroups);

        static async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            await Task.Yield();
            yield return Match(query, "guide.md", "needle", 1);
            if (query.Text.Length > 0)
            {
                throw new ArgumentException("late service fault");
            }
        }
    }

    [Fact]
    public async Task Dispose_cancels_the_active_request_and_blocks_its_late_update()
    {
        // Break caught: closing the owner can leave search work alive and allow it to mutate disposed UI state.
        var started = NewSignal();
        var release = NewSignal();
        CancellationToken searchToken = default;
        var search = new DelegateSearchService((query, cancellationToken) => Events(query, cancellationToken));
        var viewModel = CreateViewModel(search, Path.GetFullPath("search-root"));

        var activeSearch = viewModel.SearchAsync("needle", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.Dispose();

        Assert.True(searchToken.IsCancellationRequested);
        release.SetResult();
        await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(viewModel.SearchGroups);
        Assert.Null(viewModel.SearchSummary);

        async IAsyncEnumerable<SearchEvent> Events(
            SearchQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            searchToken = cancellationToken;
            started.SetResult();
            await release.Task;
            yield return Match(query, "late.md", "late", 1);
            yield return new SearchSummary(query.RequestId, 1, 0, 0, true);
        }
    }

    [Fact]
    public async Task Caller_cancellation_ends_searching_and_rejects_a_non_cooperative_late_match()
    {
        // Break caught: linked-token cancellation alone cannot stop a service that yields once more after cancellation.
        var started = NewSignal();
        var release = NewSignal();
        var search = new DelegateSearchService((query, cancellationToken) => Events(query));
        using var viewModel = CreateViewModel(search, Path.GetFullPath("search-root"));
        using var cancellation = new CancellationTokenSource();

        var activeSearch = viewModel.SearchAsync("needle", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        Assert.False(viewModel.IsSearching);
        release.SetResult();
        await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(viewModel.SearchGroups);
        Assert.True(Assert.IsType<SearchSummary>(viewModel.SearchSummary).WasCancelled);

        async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            started.SetResult();
            await release.Task;
            yield return Match(query, "late.md", "late", 1);
            yield return new SearchSummary(query.RequestId, 1, 0, 0, true);
        }
    }

    [Fact]
    public async Task CancelSearch_cancels_the_active_request_and_rejects_its_late_match()
    {
        // Break caught: a cancel command that only hides progress can leave work alive and still publish a late match.
        var started = NewSignal();
        var release = NewSignal();
        CancellationToken searchToken = default;
        var search = new DelegateSearchService((query, cancellationToken) => Events(query, cancellationToken));
        using var viewModel = CreateViewModel(search, Path.GetFullPath("search-root"));

        var activeSearch = viewModel.SearchAsync("needle", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.CancelSearch();

        Assert.True(searchToken.IsCancellationRequested);
        Assert.False(viewModel.IsSearching);
        release.SetResult();
        await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(viewModel.SearchGroups);
        Assert.True(Assert.IsType<SearchSummary>(viewModel.SearchSummary).WasCancelled);

        async IAsyncEnumerable<SearchEvent> Events(
            SearchQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            searchToken = cancellationToken;
            started.SetResult();
            await release.Task;
            yield return Match(query, "late.md", "late", 1);
            yield return new SearchSummary(query.RequestId, 1, 0, 0, true);
        }
    }

    [Fact]
    public async Task CancelSearch_rejects_a_non_cooperative_success_summary()
    {
        // Break caught: a late non-cancel terminal event can make a cancelled request look successfully completed.
        var started = NewSignal();
        var releaseSummary = NewSignal();
        var continuedAfterSummary = NewSignal();
        var releaseBrokenProducer = NewSignal();
        var search = new DelegateSearchService((query, _) => Events(query));
        using var viewModel = CreateViewModel(search, Path.GetFullPath("search-root"));

        var activeSearch = viewModel.SearchAsync("needle", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.CancelSearch();
        releaseSummary.SetResult();
        try
        {
            var firstCompletion = await Task.WhenAny(activeSearch, continuedAfterSummary.Task)
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Same(activeSearch, firstCompletion);
            await activeSearch;
            Assert.Null(viewModel.SearchSummary);
            Assert.Null(viewModel.SearchError);
            Assert.False(viewModel.IsSearching);
        }
        finally
        {
            releaseBrokenProducer.TrySetResult();
            await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        }

        async IAsyncEnumerable<SearchEvent> Events(SearchQuery query)
        {
            started.SetResult();
            await releaseSummary.Task;
            yield return new SearchSummary(query.RequestId, 99, 0, 0, false);
            continuedAfterSummary.SetResult();
            await releaseBrokenProducer.Task;
        }
    }

    [Fact]
    public async Task CancelSearch_rejects_a_non_cooperative_late_query_error()
    {
        // Break caught: a validation exception raised after cancellation can replace the cancelled visible state with an error.
        var started = NewSignal();
        var release = NewSignal();
        var search = new DelegateSearchService((query, _) => Events());
        using var viewModel = CreateViewModel(search, Path.GetFullPath("search-root"));

        var activeSearch = viewModel.SearchAsync("needle", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.CancelSearch();
        release.SetResult();
        await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(viewModel.SearchSummary);
        Assert.Null(viewModel.SearchError);
        Assert.False(viewModel.IsSearching);

        async IAsyncEnumerable<SearchEvent> Events()
        {
            started.SetResult();
            await release.Task;
            if (release.Task.IsCompleted)
            {
                throw new SearchQueryException("late invalid query");
            }

            yield break;
        }
    }

    [Fact]
    public async Task Follow_root_transition_atomically_invalidates_tree_and_non_cooperative_search_results()
    {
        // Break caught: FollowCurrentDocument can expose old-root nodes/results while a cancelled producer keeps yielding.
        var oldRoot = Path.Combine(Path.GetTempPath(), nameof(SearchViewModelTests), Guid.NewGuid().ToString("N"), "old");
        var newRoot = Path.Combine(Path.GetDirectoryName(oldRoot)!, "new");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "old.md"), "old");
        await File.WriteAllTextAsync(Path.Combine(newRoot, "new.md"), "new");
        var publishedFirst = NewSignal();
        var release = NewSignal();
        CancellationToken producerToken = default;
        SearchMatch? oldMatch = null;
        var search = new DelegateSearchService((query, cancellationToken) => Events(query, cancellationToken));
        using var viewModel = CreateViewModel(search, oldRoot);
        viewModel.RootMode = RootFollowMode.FollowCurrentDocument;

        await viewModel.RefreshTreeAsync(CancellationToken.None);
        var oldTreeFile = Assert.Single(Assert.IsType<FolderNode>(viewModel.Tree).Children);
        var activeSearch = viewModel.SearchAsync("old", CancellationToken.None);
        await publishedFirst.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(viewModel.CanActivateSearchMatch(Assert.IsType<SearchMatch>(oldMatch)));
        var atomicStateObserved = false;
        viewModel.PropertyChanged += (_, _) => AssertAtomicState();
        viewModel.SearchGroups.CollectionChanged += (_, _) => AssertAtomicState();

        viewModel.RootPath = newRoot;

        Assert.True(atomicStateObserved);
        Assert.True(producerToken.IsCancellationRequested);
        Assert.Null(viewModel.Tree);
        Assert.Empty(viewModel.SearchGroups);
        Assert.Null(viewModel.SearchSummary);
        Assert.Null(viewModel.SearchError);
        Assert.False(viewModel.IsSearching);
        Assert.True(viewModel.IsRefreshingTree);
        Assert.False(viewModel.CanActivateTree);
        Assert.False(viewModel.CanActivateSearchResults);
        Assert.False(viewModel.CanActivateFolderNode(oldTreeFile));
        Assert.False(viewModel.CanActivateSearchMatch(Assert.IsType<SearchMatch>(oldMatch)));

        release.SetResult();
        await activeSearch.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(viewModel.SearchGroups);
        Assert.Null(viewModel.SearchSummary);

        await viewModel.RefreshTreeAsync(CancellationToken.None);
        Assert.False(viewModel.IsRefreshingTree);
        Assert.True(viewModel.CanActivateTree);
        Assert.Equal("new.md", Assert.Single(Assert.IsType<FolderNode>(viewModel.Tree).Children).Name);

        Directory.Delete(Path.GetDirectoryName(oldRoot)!, recursive: true);

        async IAsyncEnumerable<SearchEvent> Events(
            SearchQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            producerToken = cancellationToken;
            oldMatch = Match(query, "old.md", "old", 1);
            yield return oldMatch;
            publishedFirst.SetResult();
            await release.Task;
            yield return Match(query, "late.md", "late", 2);
            yield return new SearchSummary(query.RequestId, 2, 0, 0, false);
        }

        void AssertAtomicState()
        {
            if (atomicStateObserved)
            {
                return;
            }

            atomicStateObserved = true;
            Assert.Equal(newRoot, viewModel.RootPath);
            Assert.Null(viewModel.Tree);
            Assert.Empty(viewModel.SearchGroups);
            Assert.False(viewModel.IsSearching);
            Assert.True(viewModel.IsRefreshingTree);
            Assert.False(viewModel.CanActivateTree);
            Assert.False(viewModel.CanActivateSearchResults);
        }
    }

    [Fact]
    public async Task Reentrant_search_clear_root_change_leaves_only_the_newest_root_refresh_state()
    {
        // Break caught: clearing stale results can reenter RootPath and let the outer transition overwrite the newer root.
        var first = Path.GetFullPath("first-root");
        var newest = Path.GetFullPath("newest-root");
        using var viewModel = CreateViewModel(
            new DelegateSearchService((query, _) => OneMatch(query)),
            Path.GetFullPath("seed-root"));
        await viewModel.SearchAsync("seed", CancellationToken.None);
        Assert.Single(viewModel.SearchGroups);
        var reentered = false;
        viewModel.SearchGroups.CollectionChanged += (_, args) =>
        {
            if (!reentered && args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                reentered = true;
                viewModel.RootPath = newest;
            }
        };

        viewModel.RootPath = first;

        Assert.True(reentered);
        Assert.Equal(newest, viewModel.RootPath);
        Assert.True(viewModel.IsRefreshingTree);
        Assert.False(viewModel.CanActivateTree);
        Assert.False(viewModel.CanActivateSearchResults);

        static async IAsyncEnumerable<SearchEvent> OneMatch(SearchQuery query)
        {
            await Task.Yield();
            yield return Match(query, "seed.md", "seed", 1);
            yield return new SearchSummary(query.RequestId, 1, 0, 0, false);
        }
    }

    private static SidebarViewModel CreateViewModel(IDocumentSearchService search, string root) =>
        CreateViewModel(search, root, action => action());

    private static SidebarViewModel CreateViewModel(
        IDocumentSearchService search,
        string root,
        Action<Action> dispatcher)
    {
        return new SidebarViewModel(
            new FolderTreeService(),
            search,
            MarkdownExtensions(),
            MarkdownExtensions(),
            MarkdownExtensions(),
            dispatcher)
        {
            RootPath = root,
        };
    }

    private static IReadOnlySet<string> MarkdownExtensions() =>
        new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase);

    private static SearchMatch Match(SearchQuery query, string relativePath, string preview, int line)
    {
        return new SearchMatch(
            query.RequestId,
            Path.Combine(query.Root, relativePath),
            line,
            preview,
            0,
            preview.Length);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateSearchService(
        Func<SearchQuery, CancellationToken, IAsyncEnumerable<SearchEvent>> search)
        : IDocumentSearchService
    {
        public IAsyncEnumerable<SearchEvent> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken) => search(query, cancellationToken);
    }

    private sealed class RecordingDispatcher
    {
        public bool IsDispatching { get; private set; }

        public void Dispatch(Action action)
        {
            IsDispatching = true;
            try
            {
                action();
            }
            finally
            {
                IsDispatching = false;
            }
        }
    }
}
