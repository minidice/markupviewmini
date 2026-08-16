using System.Text;
using MarkUpViewMini.App.Services;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Infrastructure.Files;
using MarkUpViewMini.Infrastructure.Folders;
using MarkUpViewMini.Infrastructure.Search;
using MarkUpViewMini.Infrastructure.State;
using MarkUpViewMini.Infrastructure.Recovery;

namespace MarkUpViewMini.App.Composition;

internal sealed class WindowComposition : IDisposable
{
    private bool disposed;

    private WindowComposition(
        DocumentFileService documentFileService,
        DocumentSaveService documentSaveService,
        FileChangeService fileChangeService,
        FolderTreeService folderTreeService,
        DocumentSearchService documentSearchService,
        SidebarViewModel sidebar,
        LinkRoutingService linkRoutingService,
        ExternalOpenService externalOpenService,
        ShellViewModel shell)
    {
        DocumentFileService = documentFileService;
        DocumentSaveService = documentSaveService;
        FileChangeService = fileChangeService;
        FolderTreeService = folderTreeService;
        DocumentSearchService = documentSearchService;
        Sidebar = sidebar;
        LinkRoutingService = linkRoutingService;
        ExternalOpenService = externalOpenService;
        Shell = shell;
    }

    internal DocumentFileService DocumentFileService { get; }

    internal DocumentSaveService DocumentSaveService { get; }

    internal FileChangeService FileChangeService { get; }

    internal FolderTreeService FolderTreeService { get; }

    internal DocumentSearchService DocumentSearchService { get; }

    internal SidebarViewModel Sidebar { get; }

    internal LinkRoutingService LinkRoutingService { get; }

    internal ExternalOpenService ExternalOpenService { get; }

    internal ShellViewModel Shell { get; }

    internal static WindowComposition Create(
        DocumentFormatRegistry formatRegistry,
        Func<DocumentTabViewModel, CancellationToken, Task> activateDocument,
        Action deactivateDocument,
        Func<int, CancellationToken, Task> goToLine,
        Func<string, CancellationToken, Task> goToAnchor,
        Action<Action> dispatcher,
        Func<Guid, long, CancellationToken, Task>? saveCompleted = null,
        SettingsService? settings = null,
        Guid? windowId = null,
        RecoveryService? recovery = null,
        DocumentSaveArbiter? saveArbiter = null)
    {
        ArgumentNullException.ThrowIfNull(formatRegistry);
        var documentFileService = new DocumentFileService();
        var documentSaveService = new DocumentSaveService(
            formatRegistry,
            saveArbiter ?? new DocumentSaveArbiter());
        var fileChangeService = new FileChangeService(documentFileService);
        var folderTreeService = new FolderTreeService();
        var documentSearchService = new DocumentSearchService(documentFileService);
        var sidebar = new SidebarViewModel(
            folderTreeService,
            documentSearchService,
            formatRegistry.GetExtensions(DocumentCapabilities.Read),
            formatRegistry.GetExtensions(DocumentCapabilities.FileNameSearch),
            formatRegistry.GetExtensions(DocumentCapabilities.BodySearch),
            dispatcher);
        var linkRoutingService = new LinkRoutingService(formatRegistry);
        var externalOpenService = new ExternalOpenService();
        var shell = new ShellViewModel(
            formatRegistry,
            (path, encoding, cancellationToken) => LoadDocumentAsync(
                documentFileService,
                path,
                encoding,
                cancellationToken),
            activateDocument,
            deactivateDocument,
            sidebar,
            linkRoutingService,
            externalOpenService.Open,
            goToLine,
            goToAnchor,
            documentSaveService.SaveAsync,
            saveCompleted,
            dispatcher,
            fileChangeService.WatchAsync,
            fileChangeService.RecordSavedVersion,
            settings is null
                ? null
                : path =>
                {
                    settings.RecordSuccessfulOpen(path);
                    return settings.Current.RecentDocuments;
                },
            settings is null
                ? null
                : hints => settings.UpdateEditorPreferences(
                    hints.SplitRatio,
                    new FindOptionsV1(
                        hints.FindMatchCase,
                        hints.FindWholeWord,
                        hints.FindUseRegex)),
            windowId,
            recovery is null ? null : buffer => recovery.Schedule(buffer),
            recovery is null ? null : (tabId, token) => recovery.RemoveAsync(tabId, token));

        return new WindowComposition(
            documentFileService,
            documentSaveService,
            fileChangeService,
            folderTreeService,
            documentSearchService,
            sidebar,
            linkRoutingService,
            externalOpenService,
            shell);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Shell.Dispose();
        Sidebar.Dispose();
    }

    private static Task<LoadedDocument> LoadDocumentAsync(
        DocumentFileService service,
        string path,
        Encoding? encoding,
        CancellationToken cancellationToken) =>
        encoding is null
            ? service.LoadAsync(path, cancellationToken)
            : service.LoadAsync(path, encoding, cancellationToken);
}
