using MarkUpViewMini.App.Composition;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.App.Tests.Composition;

public sealed class WindowCompositionTests
{
    public WindowCompositionTests()
    {
        App.RegisterEncodingProviders();
    }

    [Fact]
    public void Composition_owns_one_coherent_service_graph_with_approved_defaults()
    {
        // Break caught: constructing services ad hoc can split loaders, format policy, sidebar state, or lifetime within one window.
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        using var composition = WindowComposition.Create(
            registry,
            (_, _) => Task.CompletedTask,
            () => { },
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            action => action());

        Assert.Same(
            composition.DocumentFileService,
            composition.DocumentSearchService.DocumentFileService);
        Assert.Same(composition.Sidebar, composition.Shell.Sidebar);
        Assert.Equal(SearchMode.FileName, composition.Sidebar.SearchMode);
        Assert.Equal(RootFollowMode.KeepRoot, composition.Sidebar.RootMode);
    }

    [Fact]
    public async Task Disposing_the_window_composition_disposes_sidebar_search_ownership()
    {
        // Break caught: closing a window can dispose Shell yet leave Sidebar accepting background search work.
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var composition = WindowComposition.Create(
            registry,
            (_, _) => Task.CompletedTask,
            () => { },
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            action => action());
        composition.Sidebar.RootPath = Path.GetFullPath("search-root");

        composition.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            composition.Sidebar.SearchAsync("needle", CancellationToken.None));
    }

    [Fact]
    public void Composition_uses_the_application_scoped_save_arbiter()
    {
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        var arbiter = new DocumentSaveArbiter();
        using var composition = WindowComposition.Create(
            registry,
            (_, _) => Task.CompletedTask,
            () => { },
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            action => action(),
            saveArbiter: arbiter);

        Assert.Same(arbiter, composition.DocumentSaveService.SaveArbiter);
    }
}
