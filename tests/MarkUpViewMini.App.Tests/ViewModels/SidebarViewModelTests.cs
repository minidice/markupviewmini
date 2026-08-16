using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Folders;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class SidebarViewModelTests : IDisposable
{
    private readonly string root;

    public SidebarViewModelTests()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            nameof(SidebarViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void Defaults_keep_the_selected_root_and_search_file_names()
    {
        // Break caught: changing either Phase 2 default silently changes navigation/search behavior.
        using var viewModel = CreateViewModel(out _);

        Assert.Equal(RootFollowMode.KeepRoot, viewModel.RootMode);
        Assert.Equal(SearchMode.FileName, viewModel.SearchMode);
        Assert.Null(viewModel.RootPath);
    }

    [Fact]
    public void Observable_setters_raise_notifications_inside_the_dispatcher()
    {
        // Break caught: a bound setter invoked off the UI thread can raise PropertyChanged outside the dispatcher.
        using var viewModel = CreateViewModel(out var dispatcher);
        var expected = new HashSet<string>
        {
            nameof(SidebarViewModel.RootPath),
            nameof(SidebarViewModel.RootMode),
            nameof(SidebarViewModel.SearchMode),
            nameof(SidebarViewModel.SearchText),
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } propertyName && expected.Remove(propertyName))
            {
                Assert.True(dispatcher.IsDispatching);
            }
        };

        viewModel.RootPath = root;
        viewModel.RootMode = RootFollowMode.FollowCurrentDocument;
        viewModel.SearchMode = SearchMode.Body;
        viewModel.SearchText = "needle";

        Assert.Empty(expected);
    }

    [Fact]
    public async Task RefreshTreeAsync_remembers_the_root_mode_and_builds_only_registered_markdown()
    {
        // Break caught: rebuilding the tree can reset the in-memory root choice or include unsupported files.
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        await File.WriteAllTextAsync(Path.Combine(root, "guide.md"), "# Guide");
        await File.WriteAllTextAsync(Path.Combine(root, "notes", "deep.markdown"), "# Deep");
        await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "ignored");
        using var viewModel = CreateViewModel(out var dispatcher);
        viewModel.RootPath = root;
        viewModel.RootMode = RootFollowMode.FollowCurrentDocument;
        var treeChanges = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SidebarViewModel.Tree))
            {
                Assert.True(dispatcher.IsDispatching);
                treeChanges++;
            }
        };

        await viewModel.RefreshTreeAsync(CancellationToken.None);

        Assert.Equal(root, viewModel.RootPath);
        Assert.Equal(RootFollowMode.FollowCurrentDocument, viewModel.RootMode);
        Assert.Equal(1, treeChanges);
        var tree = Assert.IsType<FolderNode>(viewModel.Tree);
        Assert.Equal(["notes", "guide.md"], tree.Children.Select(child => child.Name));
        Assert.Equal("deep.markdown", Assert.Single(tree.Children[0].Children).Name);
    }

    [Fact]
    public async Task RefreshTreeAsync_exposes_a_root_enumeration_error_inside_the_dispatcher()
    {
        // Break caught: the root node can carry an error while a child-only tree binding leaves no observable root error for the UI.
        var service = new FolderTreeService(_ => throw new IOException("Root unavailable for test."));
        using var viewModel = CreateViewModel(service, out var dispatcher);
        var rootErrorNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SidebarViewModel.RootError) or nameof(SidebarViewModel.HasRootError))
            {
                Assert.True(dispatcher.IsDispatching);
                rootErrorNotifications++;
            }
        };
        viewModel.RootPath = root;

        await viewModel.RefreshTreeAsync(CancellationToken.None);

        Assert.Equal("Root unavailable for test.", viewModel.RootError);
        Assert.True(viewModel.HasRootError);
        Assert.Equal(2, rootErrorNotifications);
    }

    [Fact]
    public async Task Tree_and_each_search_mode_use_the_injected_registered_extensions()
    {
        // Break caught: a later registered document type can be omitted by hard-coded Markdown extensions in sidebar state.
        await File.WriteAllTextAsync(Path.Combine(root, "guide.note"), "needle");
        var search = new CapturingSearchService();
        using var viewModel = new SidebarViewModel(
            new FolderTreeService(),
            search,
            new HashSet<string>([".md", ".note"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md", ".note"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            action => action())
        {
            RootPath = root,
        };

        await viewModel.RefreshTreeAsync(CancellationToken.None);
        await viewModel.SearchAsync("guide", CancellationToken.None);
        viewModel.SearchMode = SearchMode.Body;
        await viewModel.SearchAsync("needle", CancellationToken.None);

        Assert.Contains(Assert.IsType<FolderNode>(viewModel.Tree).Children, node => node.Name == "guide.note");
        Assert.Collection(
            search.Queries,
            query => Assert.True(query.Extensions.SetEquals([".md", ".note"])),
            query => Assert.True(query.Extensions.SetEquals([".md"])));
    }

    [Fact]
    public async Task Stale_same_path_refresh_cannot_replace_the_newest_root_generation()
    {
        // Break caught: root A→B→A can let the first delayed A refresh overwrite the newest A tree by path equality alone.
        var staleFile = Path.Combine(root, "stale.md");
        var currentFile = Path.Combine(root, "current.md");
        await File.WriteAllTextAsync(staleFile, "stale");
        await File.WriteAllTextAsync(currentFile, "current");
        var firstStarted = new ManualResetEventSlim();
        var releaseFirst = new ManualResetEventSlim();
        var calls = 0;
        var service = new FolderTreeService(directory =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(2)));
                return [new FileInfo(staleFile)];
            }

            return [new FileInfo(currentFile)];
        });
        using var viewModel = new SidebarViewModel(
            service,
            new EmptySearchService(),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            action => action());
        viewModel.RootPath = root;
        var staleRefresh = Task.Run(() => viewModel.RefreshTreeAsync(CancellationToken.None));
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(1)));

        viewModel.RootPath = Path.Combine(root, "other");
        viewModel.RootPath = root;
        await viewModel.RefreshTreeAsync(CancellationToken.None);
        releaseFirst.Set();
        await staleRefresh.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(viewModel.IsRefreshingTree);
        Assert.Equal("current.md", Assert.Single(Assert.IsType<FolderNode>(viewModel.Tree).Children).Name);
    }

    [Theory]
    [InlineData(nameof(SidebarViewModel.Tree), true)]
    [InlineData(nameof(SidebarViewModel.RootError), true)]
    [InlineData(nameof(SidebarViewModel.HasRootError), true)]
    [InlineData(nameof(SidebarViewModel.Tree), false)]
    [InlineData(nameof(SidebarViewModel.RootError), false)]
    [InlineData(nameof(SidebarViewModel.HasRootError), false)]
    public async Task Nested_root_refresh_started_by_tree_notification_keeps_the_new_generation_refreshing(
        string triggerProperty,
        bool changeRoot)
    {
        // Break caught: the stale refresh callback clears IsRefreshingTree after a Tree-derived observer starts a newer root generation.
        var newestRoot = Path.Combine(root, "newest");
        Directory.CreateDirectory(newestRoot);
        using var newestRefreshStarted = new ManualResetEventSlim();
        using var releaseNewestRefresh = new ManualResetEventSlim();
        var enumerationCount = 0;
        var service = new FolderTreeService(_ =>
        {
            if (Interlocked.Increment(ref enumerationCount) == 1)
            {
                throw new IOException("First root unavailable for test.");
            }

            newestRefreshStarted.Set();
            Assert.True(releaseNewestRefresh.Wait(TimeSpan.FromSeconds(5)));
            return [];
        });
        using var viewModel = CreateViewModel(service, out var dispatcher);
        Task? newestRefresh = null;
        var replacementStarted = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            Assert.True(dispatcher.IsDispatching);
            if (!replacementStarted && args.PropertyName == triggerProperty)
            {
                replacementStarted = true;
                if (changeRoot)
                {
                    viewModel.RootPath = newestRoot;
                }

                newestRefresh = viewModel.RefreshTreeAsync(CancellationToken.None);
            }
        };
        viewModel.RootPath = root;

        await viewModel.RefreshTreeAsync(CancellationToken.None);
        Assert.True(newestRefreshStarted.Wait(TimeSpan.FromSeconds(1)));

        Assert.True(replacementStarted);
        Assert.Equal(changeRoot ? newestRoot : root, viewModel.RootPath);
        Assert.True(viewModel.IsRefreshingTree);

        releaseNewestRefresh.Set();
        Assert.NotNull(newestRefresh);
        await newestRefresh;
        Assert.False(viewModel.IsRefreshingTree);
    }

    [Fact]
    public void SetOutline_replaces_items_inside_the_dispatcher()
    {
        // Break caught: appending a new document outline leaves stale headings visible or mutates UI state off-dispatcher.
        using var viewModel = CreateViewModel(out var dispatcher);
        viewModel.Outline.CollectionChanged += (_, _) => Assert.True(dispatcher.IsDispatching);
        viewModel.SetOutline([new(1, "Old", "old", 1)]);

        viewModel.SetOutline(
        [
            new(2, "Install", "install", 8),
            new(2, "Install", "install-1", 14),
        ]);

        Assert.Equal(
            [
                new OutlineItemViewModel(2, "Install", "install", 8),
                new OutlineItemViewModel(2, "Install", "install-1", 14),
            ],
            viewModel.Outline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nested_SetOutline_replacement_wins_without_stale_outer_items(bool replaceOnAdd)
    {
        // Break caught: Clear/Add reentrancy can let an older replacement append after the newer outline.
        using var viewModel = CreateViewModel(out var dispatcher);
        viewModel.SetOutline([new(1, "Seed", "seed", 1)]);
        var nestedReplacementStarted = false;
        var newest = new[]
        {
            new OutlineItemViewModel(2, "Newest first", "newest-first", 10),
            new OutlineItemViewModel(3, "Newest child", "newest-child", 11),
        };
        viewModel.Outline.CollectionChanged += (_, args) =>
        {
            Assert.True(dispatcher.IsDispatching);
            if (nestedReplacementStarted
                || replaceOnAdd != (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add))
            {
                return;
            }

            nestedReplacementStarted = true;
            viewModel.SetOutline(newest);
        };

        viewModel.SetOutline(
        [
            new(1, "Stale first", "stale-first", 20),
            new(2, "Stale child", "stale-child", 21),
        ]);

        Assert.True(nestedReplacementStarted);
        Assert.Equal(newest, viewModel.Outline);
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }

    private static SidebarViewModel CreateViewModel(out RecordingDispatcher dispatcher)
    {
        return CreateViewModel(new FolderTreeService(), out dispatcher);
    }

    private static SidebarViewModel CreateViewModel(
        FolderTreeService folderTreeService,
        out RecordingDispatcher dispatcher)
    {
        dispatcher = new RecordingDispatcher();
        return new SidebarViewModel(
            folderTreeService,
            new EmptySearchService(),
            new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase),
            dispatcher.Dispatch);
    }

    private sealed class CapturingSearchService : IDocumentSearchService
    {
        public List<SearchQuery> Queries { get; } = [];

        public async IAsyncEnumerable<SearchEvent> SearchAsync(
            SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Queries.Add(query);
            await Task.Yield();
            yield return new SearchSummary(query.RequestId, 0, 0, 0, false);
        }
    }

    private sealed class RecordingDispatcher
    {
        private int depth;

        public bool IsDispatching => depth > 0;

        public void Dispatch(Action action)
        {
            depth++;
            try
            {
                action();
            }
            finally
            {
                depth--;
            }
        }
    }

    private sealed class EmptySearchService : IDocumentSearchService
    {
        public async IAsyncEnumerable<SearchEvent> SearchAsync(
            SearchQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new SearchSummary(query.RequestId, 0, 0, 0, false);
        }
    }
}
