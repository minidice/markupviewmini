using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Folders;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class SettingsIntegrationTests
{
    public SettingsIntegrationTests() => App.RegisterEncodingProviders();

    [Fact]
    public async Task Shell_records_only_a_successful_registered_document_open()
    {
        // Break caught: unsupported or failed loads pollute MRU, or success is recorded before generation ownership is proven.
        var recorded = new List<string>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) => Path.GetFileName(path) == "missing.md"
                ? Task.FromException<LoadedDocument>(new FileNotFoundException())
                : Task.FromResult(Loaded(path)),
            (_, _) => Task.CompletedTask,
            recordSuccessfulOpen: path =>
            {
                recorded.Add(path);
                return [new RecentDocumentEntry(path)];
            });

        await shell.OpenAsync(Target("missing.md"), OpenGesture.Normal, CancellationToken.None);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            shell.OpenAsync(Target("unsupported.txt"), OpenGesture.Normal, CancellationToken.None));
        await shell.OpenAsync(Target("opened.md"), OpenGesture.Normal, CancellationToken.None);

        Assert.Equal([Path.GetFullPath("opened.md")], recorded);
        Assert.Equal(recorded, shell.RecentDocuments.Select(entry => entry.Path));
    }

    [Fact]
    public async Task Recent_activation_uses_the_registry_and_normal_shell_open_generation()
    {
        // Break caught: the File menu bypasses format policy or directly loads outside Shell ownership.
        var loaded = new List<string>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) =>
            {
                loaded.Add(path);
                return Task.FromResult(Loaded(path));
            },
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<NotSupportedException>(() => shell.OpenRecentAsync(
            new RecentDocumentEntry(Path.GetFullPath("unsupported.txt")),
            CancellationToken.None));
        await shell.OpenRecentAsync(
            new RecentDocumentEntry(Path.GetFullPath("recent.md")),
            CancellationToken.None);

        Assert.Equal([Path.GetFullPath("recent.md")], loaded);
        Assert.Equal(Path.GetFullPath("recent.md"), shell.ActiveTab?.Path);
        Assert.Equal(1, shell.ActiveTab?.Revision);
    }

    [Fact]
    public async Task Successful_background_completion_is_recorded_after_the_user_switches_tabs()
    {
        // Break caught: MRU recording is incorrectly conditional on the loaded tab still being active.
        var firstLoad = new TaskCompletionSource<LoadedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new List<string>();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var shell = new ShellViewModel(
            registry,
            (path, _, _) => Path.GetFileName(path) == "slow.md"
                ? firstLoad.Task
                : Task.FromResult(Loaded(path)),
            (_, _) => Task.CompletedTask,
            recordSuccessfulOpen: path =>
            {
                recorded.Add(path);
                return recorded.Select(item => new RecentDocumentEntry(item)).ToArray();
            });

        var slowOpen = shell.OpenAsync(Target("slow.md"), OpenGesture.Normal, CancellationToken.None);
        await shell.OpenAsync(Target("current.md"), OpenGesture.ExplicitNewTab, CancellationToken.None);
        firstLoad.SetResult(Loaded("slow.md"));
        await slowOpen;

        Assert.Equal(
            [Path.GetFullPath("current.md"), Path.GetFullPath("slow.md")],
            recorded);
    }

    [Fact]
    public void Loaded_sidebar_settings_apply_on_dispatcher_and_nested_newer_application_wins()
    {
        // Break caught: a stale outer load overwrites SearchMode after a PropertyChanged observer applies a newer snapshot.
        var dispatcher = new RecordingDispatcher();
        using var sidebar = new SidebarViewModel(
            new FolderTreeService(),
            new EmptySearchService(),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            dispatcher.Dispatch);
        var nested = false;
        sidebar.PropertyChanged += (_, args) =>
        {
            Assert.True(dispatcher.IsDispatching);
            if (!nested && args.PropertyName == nameof(SidebarViewModel.RootMode))
            {
                nested = true;
                sidebar.ApplySettings(SettingsV1.CreateDefault() with
                {
                    RootMode = RootFollowMode.KeepRoot,
                    SidebarSearchMode = SearchMode.FileName,
                });
            }
        };

        sidebar.ApplySettings(SettingsV1.CreateDefault() with
        {
            RootMode = RootFollowMode.FollowCurrentDocument,
            SidebarSearchMode = SearchMode.Body,
            SidebarSearchOptions = new(true, true, true),
        });

        Assert.True(nested);
        Assert.Equal(RootFollowMode.KeepRoot, sidebar.RootMode);
        Assert.Equal(SearchMode.FileName, sidebar.SearchMode);
        Assert.False(sidebar.MatchCase);
        Assert.False(sidebar.WholeWord);
        Assert.False(sidebar.UseRegex);
    }

    private static DocumentTarget Target(string path) =>
        new(Path.GetFullPath(path), null, null);

    private static LoadedDocument Loaded(string path) => new(
        $"loaded:{Path.GetFileName(path)}",
        new EncodingDescriptor("utf-8", false),
        NewLineKind.Lf,
        "\n",
        new DiskFileVersion(1, DateTime.UnixEpoch, new string('a', 64)));

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
}
