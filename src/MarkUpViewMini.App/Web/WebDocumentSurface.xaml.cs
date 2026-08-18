using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MarkUpViewMini.App.Mermaid;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Mermaid;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Diagnostics;
using MarkUpViewMini.Infrastructure.Paths;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MarkUpViewMini.App.Web;

public partial class WebDocumentSurface : UserControl, IDisposable, IWebViewRecoveryOperations
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(10);
    private const string DocumentAssetFilter = "https://document-assets.local/*";

    private readonly WebSurfaceActivationCoordinator coordinator;
    private readonly WebNavigationAttemptTracker navigationAttempts = new();
    private readonly WebViewHandlerLifetime handlerLifetime = new();
    private readonly WebViewInitializationLifetime initializationLifetime = new();
    private readonly DocumentChangeBatchAssembler changeBatches = new();
    private readonly DocumentResyncTracker resyncTracker = new();
    private readonly WebViewControlLifetime<WebView2> browserLifetime;
    private readonly Func<IMermaidSurfaceTransport> getMermaidTransport;
    private readonly IMermaidEditDialogFactory mermaidDialogFactory;
    private readonly MermaidFocusRestoration mermaidFocusRestoration;
    private readonly Func<DocumentTabViewModel?> getMermaidTab;
    private readonly Func<Window?> getMermaidOwner;
    private IAppDataPaths? appDataPaths;
    private SafeFileLogger? safeLogger;
    private WebViewRecoveryController? recoveryController;
    private Func<IReadOnlyList<DocumentTabViewModel>>? captureTabs;
    private Func<DocumentTabViewModel?>? captureActiveTab;
    private WebActivationStamp? recoveryActivation;
    private long recoveryGeneration;
    private Guid windowId;
    private TaskCompletionSource<bool>? pendingReady;
    private Guid pendingReadyTabId;
    private DocumentTabViewModel? lastRequestedTab;
    private IMermaidEditDialog? activeMermaidDialog;
    private long mermaidDialogGeneration;
    private string? activeDocumentPath;
    private bool disposed;

    public event Action<DocumentOutlineMessage>? OutlineReceived;

    public event Action<LinkOpenMessage>? LinkOpenRequested;

    public event Action<LinkContextMenuMessage>? LinkContextMenuRequested;

    public event Action<DocumentChangedMessage>? DocumentChanged;

    public event Action<DocumentModeChangedMessage>? ModeChanged;

    public event Action<DocumentUiHintsChangedMessage>? UiHintsChanged;

    internal event Action? CurrentResponseChanged
    {
        add => coordinator.CurrentResponseChanged += value;
        remove => coordinator.CurrentResponseChanged -= value;
    }

    internal WebResponseContext? CurrentResponse => coordinator.CurrentResponse;

    public WebDocumentSurface()
        : this(
            new WebSurfaceActivationCoordinator(),
            null,
            new MermaidEditDialogFactory(),
            new MermaidFocusRestoration(),
            null,
            null)
    {
    }

    internal WebDocumentSurface(
        WebSurfaceActivationCoordinator coordinator,
        Func<IMermaidSurfaceTransport>? getMermaidTransport,
        IMermaidEditDialogFactory mermaidDialogFactory,
        MermaidFocusRestoration mermaidFocusRestoration,
        Func<DocumentTabViewModel?>? getMermaidTab,
        Func<Window?>? getMermaidOwner)
    {
        InitializeComponent();
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.mermaidDialogFactory = mermaidDialogFactory ??
            throw new ArgumentNullException(nameof(mermaidDialogFactory));
        this.mermaidFocusRestoration = mermaidFocusRestoration ??
            throw new ArgumentNullException(nameof(mermaidFocusRestoration));
        this.coordinator.CurrentResponseChanged += CancelPendingMermaidFocus;
        browserLifetime = new WebViewControlLifetime<WebView2>(
            () => new WebView2 { Visibility = Visibility.Collapsed },
            browser => BrowserHost.Children.Add(browser),
            browser => BrowserHost.Children.Remove(browser),
            browser => browser.Dispose());
        this.getMermaidTransport = getMermaidTransport ??
            (() => new WebView2MermaidSurfaceTransport(Browser));
        this.getMermaidTab = getMermaidTab ?? (() => lastRequestedTab);
        this.getMermaidOwner = getMermaidOwner ?? (() => Window.GetWindow(this));
        Browser.Visibility = Visibility.Collapsed;
    }

    private WebView2 Browser => browserLifetime.Current;

    public void Configure(
        IAppDataPaths paths,
        Guid ownerWindowId,
        Func<IReadOnlyList<DocumentTabViewModel>> recoveryTabs,
        Func<DocumentTabViewModel?> recoveryActiveTab)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(paths);
        if (ownerWindowId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty window ID is required.", nameof(ownerWindowId));
        }

        if (appDataPaths is not null &&
            (!ReferenceEquals(appDataPaths, paths) ||
             windowId != ownerWindowId ||
             !ReferenceEquals(captureTabs, recoveryTabs) ||
             !ReferenceEquals(captureActiveTab, recoveryActiveTab)))
        {
            throw new InvalidOperationException("The document surface is already configured for a window.");
        }

        appDataPaths = paths;
        windowId = ownerWindowId;
        captureTabs = recoveryTabs ?? throw new ArgumentNullException(nameof(recoveryTabs));
        captureActiveTab = recoveryActiveTab ?? throw new ArgumentNullException(nameof(recoveryActiveTab));
        safeLogger ??= new SafeFileLogger(paths);
        recoveryController ??= new WebViewRecoveryController(this, safeLogger, safeLogger.ReadSafeText);
    }

    public Task ActivateAsync(DocumentTabViewModel tab, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(tab);
        recoveryController?.SupersedeCurrentRecovery();
        mermaidFocusRestoration.Cancel();
        lastRequestedTab = tab;
        changeBatches.CancelAll();
        ClearPendingResync();
        var activation = coordinator.BeginActivation(tab.Id);
        return ActivateCoreAsync(tab, activation, cancellationToken);
    }

    public void Deactivate()
    {
        if (disposed)
        {
            return;
        }

        recoveryController?.SupersedeCurrentRecovery();
        mermaidFocusRestoration.Cancel();
        coordinator.Deactivate();
        changeBatches.CancelAll();
        ClearPendingResync();
        lastRequestedTab = null;
        ResetPendingReady();
        ClearDocumentAssetMapping();
        SurfaceError.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
    }

    public Task GoToLineAsync(int line) =>
        PostCurrentMessageAsync(response => WebViewPolicy.CreateGoToLineMessage(response, windowId, line));

    public Task GoToAnchorAsync(string anchor) =>
        PostCurrentMessageAsync(response => WebViewPolicy.CreateGoToAnchorMessage(response, windowId, anchor));

    public Task OpenFindAsync() => PostFindMessageAsync("find.open");

    public Task OpenFindAsync(Guid tabId, long revision) =>
        PostOwnedFindMessageAsync(tabId, revision, "find.open");

    public Task FindNextAsync() => PostFindMessageAsync("find.next");

    public Task FindNextAsync(Guid tabId, long revision) =>
        PostOwnedFindMessageAsync(tabId, revision, "find.next");

    public Task FindPreviousAsync() => PostFindMessageAsync("find.previous");

    public Task FindPreviousAsync(Guid tabId, long revision) =>
        PostOwnedFindMessageAsync(tabId, revision, "find.previous");

    public Task CloseFindAsync() => PostFindMessageAsync("find.close");

    public Task CloseFindAsync(Guid tabId, long revision) =>
        PostOwnedFindMessageAsync(tabId, revision, "find.close");

    public Task UndoAsync(Guid tabId, long revision) =>
        PostOwnedMessageAsync(
            tabId,
            revision,
            response => WebViewPolicy.CreateEditorCommandMessage(response, windowId, "editor.undo"));

    public Task RedoAsync(Guid tabId, long revision) =>
        PostOwnedMessageAsync(
            tabId,
            revision,
            response => WebViewPolicy.CreateEditorCommandMessage(response, windowId, "editor.redo"));

    public Task SetModeAsync(Guid tabId, long revision, DocumentMode mode) =>
        PostOwnedMessageAsync(
            tabId,
            revision,
            response => WebViewPolicy.CreateSetModeMessage(response, windowId, mode));

    public Task SetModeAsync(DocumentMode mode) =>
        PostCurrentMessageAsync(response => WebViewPolicy.CreateSetModeMessage(response, windowId, mode));

    public Task SetEditorPreferencesAsync(DocumentUiHints hints) =>
        PostCurrentMessageAsync(response =>
            WebViewPolicy.CreateSetEditorPreferencesMessage(response, windowId, hints));

    public Task SaveCompletedAsync(Guid tabId, long savedRevision)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (coordinator.CurrentResponse is not { } response ||
            response.TabId != tabId ||
            response.Revision != savedRevision)
        {
            return Task.CompletedTask;
        }

        Browser.CoreWebView2.PostWebMessageAsJson(
            WebViewPolicy.CreateSaveCompletedMessage(response, windowId));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        recoveryController?.Dispose();
        coordinator.CurrentResponseChanged -= CancelPendingMermaidFocus;
        mermaidFocusRestoration.Dispose();
        initializationLifetime.Dispose();
        try
        {
            Deactivate();
        }
        finally
        {
            disposed = true;
            changeBatches.Dispose();
            try
            {
                browserLifetime.Dispose(() => handlerLifetime.TryUnregister());
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }

    private async Task ActivateCoreAsync(
        DocumentTabViewModel tab,
        WebActivationStamp activation,
        CancellationToken cancellationToken)
    {
        try
        {
            Browser.Visibility = Visibility.Visible;
            coordinator.MarkInitializing(activation);
            await EnsureInitializedAsync(cancellationToken);
            if (!coordinator.IsCurrent(activation))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await EnsureSurfaceReadyAsync(activation, cancellationToken) ||
                !coordinator.IsCurrent(activation))
            {
                return;
            }

            MapDocumentAssets(tab.Path);
            PostActivation(tab, activation);
            SurfaceError.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            if (coordinator.IsCurrent(activation))
            {
                coordinator.CancelActivation(activation);
                ResetPendingReady();
                SurfaceError.Visibility = Visibility.Collapsed;
            }

            throw;
        }
        catch (TimeoutException exception) when (coordinator.IsCurrent(activation))
        {
            MarkActivationFailed(activation, WebSurfaceFailure.Timeout);
            throw new InvalidOperationException("The local document surface did not become ready in time.", exception);
        }
        catch when (!coordinator.IsCurrent(activation))
        {
        }
        catch
        {
            MarkActivationFailed(activation, WebSurfaceFailure.InitializationFailed);
            throw;
        }
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (appDataPaths is null)
        {
            throw new InvalidOperationException("Configure must be called before activating a document.");
        }

        return browserLifetime.EnsureInitializedAsync(
            initializationLifetime,
            (browser, token) => InitializeCoreAsync(appDataPaths, browser, token),
            () => handlerLifetime.TryUnregister(),
            InitializationTimeout,
            cancellationToken);
    }

    private async Task InitializeCoreAsync(
        IAppDataPaths paths,
        WebView2 browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.WebView2Directory);
        var assetsDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "web", "document-surface"));
        if (!File.Exists(Path.Combine(assetsDirectory, "index.html")))
        {
            throw new FileNotFoundException("The packaged document surface is unavailable.");
        }

        if (browser.CoreWebView2 is null)
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: paths.WebView2Directory)
                .WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(browser, Browser))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await browser.EnsureCoreWebView2Async(environment).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(browser, Browser))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var coreWebView = browser.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 initialization did not create a core instance.");
        coreWebView.SetVirtualHostNameToFolderMapping(
            WebViewPolicy.AppHostName,
            assetsDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        cancellationToken.ThrowIfCancellationRequested();
        SubscribeHandlersOnce(coreWebView);
    }

    private async Task<bool> EnsureSurfaceReadyAsync(
        WebActivationStamp activation,
        CancellationToken cancellationToken)
    {
        if (coordinator.State == WebSurfaceLifecycleState.Ready)
        {
            return true;
        }

        if (pendingReady is null)
        {
            navigationAttempts.Clear();
            Browser.CoreWebView2.Stop();
            var bootstrapUri = WebViewPolicy.BuildBootstrapUri(windowId, activation.TabId);
            navigationAttempts.Begin(activation.Generation, bootstrapUri);
            pendingReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingReadyTabId = activation.TabId;
            coordinator.MarkAwaitingReady(activation, pendingReadyTabId);
            Browser.CoreWebView2.Navigate(bootstrapUri.AbsoluteUri);
        }

        return await pendingReady.Task.WaitAsync(ReadyTimeout, cancellationToken);
    }

    private void SubscribeHandlersOnce(CoreWebView2 core)
    {
        handlerLifetime.TryRegister(
        [
            new(
                () => core.AddWebResourceRequestedFilter(
                    DocumentAssetFilter,
                    CoreWebView2WebResourceContext.Image,
                    CoreWebView2WebResourceRequestSourceKinds.Document),
                () => core.RemoveWebResourceRequestedFilter(
                    DocumentAssetFilter,
                    CoreWebView2WebResourceContext.Image,
                    CoreWebView2WebResourceRequestSourceKinds.Document)),
            new(() => core.WebMessageReceived += Core_WebMessageReceived, () => core.WebMessageReceived -= Core_WebMessageReceived),
            new(() => core.NavigationStarting += Core_NavigationStarting, () => core.NavigationStarting -= Core_NavigationStarting),
            new(() => core.NavigationCompleted += Core_NavigationCompleted, () => core.NavigationCompleted -= Core_NavigationCompleted),
            new(() => core.WebResourceRequested += Core_WebResourceRequested, () => core.WebResourceRequested -= Core_WebResourceRequested),
            new(() => core.NewWindowRequested += Core_NewWindowRequested, () => core.NewWindowRequested -= Core_NewWindowRequested),
            new(() => core.DownloadStarting += Core_DownloadStarting, () => core.DownloadStarting -= Core_DownloadStarting),
            new(() => core.PermissionRequested += Core_PermissionRequested, () => core.PermissionRequested -= Core_PermissionRequested),
            new(() => core.ProcessFailed += Core_ProcessFailed, () => core.ProcessFailed -= Core_ProcessFailed),
        ]);
    }

    private async void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

        WebMessageEnvelope message;
        try
        {
            message = WebMessageParser.Parse(e.WebMessageAsJson);
        }
        catch (WebMessageValidationException)
        {
            return;
        }

        if (pendingReady is { } ready &&
            WebViewPolicy.IsMatchingReady(message, windowId, pendingReadyTabId) &&
            coordinator.TryMarkReady(pendingReadyTabId))
        {
            navigationAttempts.Clear();
            pendingReady = null;
            ready.TrySetResult(true);
            return;
        }

        if (coordinator.CurrentResponse is { } resyncCurrent &&
            IsPendingResync(message, resyncCurrent))
        {
            HandleResyncRequest(resyncCurrent);
            return;
        }

        if (coordinator.CurrentResponse is not { } current ||
            (!WebViewPolicy.IsCurrentDocumentMessage(message, current, windowId) &&
             !WebViewPolicy.IsCurrentActivationResponse(
                 message,
                 current.RequestId,
                 windowId,
                 current.TabId,
                 current.Revision)))
        {
            if (coordinator.CurrentResponse is { } staleCurrent &&
                IsCorrelatedEditType(message.Type) &&
                message.TabId == staleCurrent.TabId)
            {
                changeBatches.Cancel(message.TabId);
                if (message.WindowId == windowId)
                {
                    RejectDocumentChange(staleCurrent);
                }
            }

            return;
        }

        if (await HandleMermaidMessageAsync(message))
        {
            return;
        }

        if (string.Equals(message.Type, "document.changed", StringComparison.Ordinal))
        {
            try
            {
                HandleDocumentChanged(current, WebMessageParser.ParseDocumentChanged(message));
            }
            catch (WebMessageValidationException)
            {
                RejectDocumentChange(current);
            }

            return;
        }

        if (string.Equals(message.Type, "document.changeBatchStart", StringComparison.Ordinal))
        {
            try
            {
                changeBatches.Start(WebMessageParser.ParseDocumentChangeBatchStart(message));
            }
            catch (WebMessageValidationException)
            {
                changeBatches.Cancel(message.TabId);
                RejectDocumentChange(current);
            }

            return;
        }

        if (string.Equals(message.Type, "document.changeBatchChunk", StringComparison.Ordinal))
        {
            try
            {
                if (!changeBatches.Append(WebMessageParser.ParseDocumentChangeBatchChunk(message)))
                {
                    RejectDocumentChange(current);
                }
            }
            catch (WebMessageValidationException)
            {
                changeBatches.Cancel(message.TabId);
                RejectDocumentChange(current);
            }

            return;
        }

        if (string.Equals(message.Type, "document.changeBatchCommit", StringComparison.Ordinal))
        {
            try
            {
                var completed = changeBatches.Commit(
                    WebMessageParser.ParseDocumentChangeBatchCommit(message));
                if (completed is null)
                {
                    RejectDocumentChange(current);
                }
                else
                {
                    HandleDocumentChanged(current, completed);
                }
            }
            catch (WebMessageValidationException)
            {
                changeBatches.Cancel(message.TabId);
                RejectDocumentChange(current);
            }

            return;
        }

        if (string.Equals(message.Type, "document.modeChanged", StringComparison.Ordinal))
        {
            try
            {
                ModeChanged?.Invoke(WebMessageParser.ParseDocumentModeChanged(message));
            }
            catch (WebMessageValidationException)
            {
            }

            return;
        }

        if (string.Equals(message.Type, "document.uiHintsChanged", StringComparison.Ordinal))
        {
            try
            {
                UiHintsChanged?.Invoke(WebMessageParser.ParseDocumentUiHintsChanged(message));
            }
            catch (WebMessageValidationException)
            {
            }

            return;
        }

        if (string.Equals(message.Type, "document.outline", StringComparison.Ordinal))
        {
            try
            {
                OutlineReceived?.Invoke(WebMessageParser.ParseDocumentOutline(message));
            }
            catch (WebMessageValidationException)
            {
            }

            return;
        }

        if (string.Equals(message.Type, "link.open", StringComparison.Ordinal))
        {
            try
            {
                LinkOpenRequested?.Invoke(WebMessageParser.ParseLinkOpen(message));
            }
            catch (WebMessageValidationException)
            {
            }

            return;
        }

        if (string.Equals(message.Type, "link.contextMenu", StringComparison.Ordinal))
        {
            try
            {
                LinkContextMenuRequested?.Invoke(WebMessageParser.ParseLinkContextMenu(message));
            }
            catch (WebMessageValidationException)
            {
            }

            return;
        }

        if (string.Equals(message.Type, "surface.error", StringComparison.Ordinal))
        {
            if (coordinator.TryMarkRenderFailed(current))
            {
                ResetPendingReady();
                ClearDocumentAssetMapping();
                SurfaceError.Visibility = Visibility.Visible;
            }

            return;
        }

        if (string.Equals(message.Type, "document.rendered", StringComparison.Ordinal))
        {
            SignalPerformanceRenderIfRequested();
        }

        SurfaceError.Visibility = Visibility.Collapsed;
    }

    private static void SignalPerformanceRenderIfRequested()
    {
        var pipeName = Environment.GetEnvironmentVariable("MARKUPVIEWMINI_PERF_RENDER_PIPE");
        if (string.IsNullOrWhiteSpace(pipeName) ||
            !pipeName.StartsWith("MarkUpViewMini.Performance.", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var rendered = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            rendered.Connect(1_000);
            rendered.WriteByte(1);
            rendered.Flush();
        }
        catch (TimeoutException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsCurrentCore(sender))
        {
            e.Cancel = true;
            return;
        }

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            !WebViewPolicy.IsAllowedTopLevelNavigation(uri))
        {
            e.Cancel = true;
            return;
        }

        navigationAttempts.TryRecordStarting(
            coordinator.CurrentGeneration,
            uri,
            e.NavigationId);
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!IsCurrentCore(sender) ||
            pendingReady is null ||
            !navigationAttempts.IsCurrentCompletion(
                coordinator.CurrentGeneration,
                e.NavigationId) ||
            e.IsSuccess)
        {
            return;
        }

        MarkSurfaceFailed(WebSurfaceFailure.NavigationFailed);
    }

    private async void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (disposed || !IsCurrentCore(sender))
        {
            return;
        }

        switch (WebViewProcessFailurePolicy.Decide(e.ProcessFailedKind))
        {
            case WebViewProcessRecoveryAction.Ignore:
                return;
            case WebViewProcessRecoveryAction.RecreateControl:
            case WebViewProcessRecoveryAction.Renavigate:
            default:
                try
                {
                    await (recoveryController?.HandleProcessFailureAsync() ?? Task.CompletedTask);
                }
                catch
                {
                    // The controller leaves the nonmodal recovery actions visible.
                }

                return;
        }
    }

    private void Core_WebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

        if (activeDocumentPath is not null &&
            WebViewPolicy.TryResolveDocumentAssetRequest(
                activeDocumentPath,
                e.Request.Uri,
                out _))
        {
            return;
        }

        e.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
            Stream.Null,
            403,
            "Forbidden",
            "Content-Type: text/plain\r\nCache-Control: no-store");
    }

    private static void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) =>
        e.Handled = true;

    private static void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) =>
        e.Cancel = true;

    private static void Core_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) =>
        e.State = CoreWebView2PermissionState.Deny;

    private void MapDocumentAssets(string documentPath)
    {
        var directory = WebViewPolicy.GetDocumentAssetsDirectory(documentPath);
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            WebViewPolicy.DocumentAssetsHostName,
            directory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        activeDocumentPath = Path.GetFullPath(documentPath);
    }

    private void ClearDocumentAssetMapping(bool clearBrowserMapping = true)
    {
        activeDocumentPath = null;
        if (clearBrowserMapping)
        {
            Browser.CoreWebView2?.ClearVirtualHostNameToFolderMapping(
                WebViewPolicy.DocumentAssetsHostName);
        }
    }

    /// <summary>Tells the surface which language to label its own controls in.</summary>
    /// <remarks>
    /// Carries no document correlation on purpose: the language is not a property of any
    /// document, and requiring a matching tab would drop the change whenever none is active.
    /// Safe to call before the surface is up - it simply does nothing until then, and the
    /// caller sends it again once initialisation finishes.
    /// </remarks>
    internal void PostLanguage(string languageCode)
    {
        if (disposed || Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            version = 1,
            type = "document.setLanguage",
            payload = new { language = languageCode ?? string.Empty },
        }));
    }

    private void PostActivation(DocumentTabViewModel tab, WebActivationStamp activation)
    {
        var requestId = Guid.NewGuid();
        _ = WebSurfaceActivationTransaction.TryPost(
            coordinator,
            activation,
            requestId,
            tab.Revision,
            () => PostActivationMessage(tab, requestId));
    }

    private void PostActivationMessage(DocumentTabViewModel tab, Guid requestId)
    {
        var json = WebViewPolicy.CreateDocumentActivationMessage(tab, requestId, windowId);
        Browser.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void HandleDocumentChanged(WebResponseContext current, DocumentChangedMessage message)
    {
        DocumentChanged?.Invoke(message);
        if (lastRequestedTab is { } tab &&
            tab.Id == current.TabId &&
            tab.Revision == current.Revision + 1 &&
            coordinator.TryUpdateCurrentResponse(current, current.RequestId, tab.Revision))
        {
            changeBatches.Cancel(tab.Id);
            ClearPendingResync();
            Browser.CoreWebView2.PostWebMessageAsJson(
                WebViewPolicy.CreateDocumentChangeAcceptedMessage(
                    current,
                    windowId,
                    tab.Revision));
            return;
        }

        RejectDocumentChange(current);
    }

    internal async Task<bool> HandleMermaidMessageAsync(string json)
    {
        WebMessageEnvelope message;
        try
        {
            message = WebMessageParser.Parse(json);
        }
        catch (WebMessageValidationException)
        {
            return false;
        }

        return await HandleMermaidMessageAsync(message);
    }

    private async Task<bool> HandleMermaidMessageAsync(WebMessageEnvelope message)
    {
        if (message.Type is not "mermaid.editRequested" and not "mermaid.focusCompleted")
        {
            return false;
        }

        if (coordinator.CurrentResponse is not { } current ||
            !WebViewPolicy.IsCurrentDocumentMessage(message, current, windowId))
        {
            return true;
        }

        if (string.Equals(message.Type, "mermaid.editRequested", StringComparison.Ordinal))
        {
            await HandleMermaidEditRequestedAsync(current, message);
            return true;
        }

        try
        {
            var transport = getMermaidTransport();
            mermaidFocusRestoration.TryAcknowledge(
                WebMessageParser.ParseMermaidFocusCompleted(message),
                coordinator.CurrentResponse,
                windowId,
                transport.Control,
                transport.Focus);
        }
        catch (WebMessageValidationException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    private async Task HandleMermaidEditRequestedAsync(
        WebResponseContext current,
        WebMessageEnvelope message)
    {
        if (activeMermaidDialog is not null ||
            appDataPaths is null ||
            getMermaidTab() is not { Buffer: not null } tab ||
            tab.Id != current.TabId ||
            getMermaidOwner() is not { } owner)
        {
            return;
        }

        IMermaidSurfaceTransport transport;
        try
        {
            transport = getMermaidTransport();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var generation = checked(++mermaidDialogGeneration);
        if (!MermaidDocumentEditSession.TryCreate(
            message,
            generation,
            transport.Control,
            out var session))
        {
            return;
        }

        // Opening the dialog creates a second WebView2 control that shares this control's
        // CoreWebView2Environment, and ShowDialog() pumps a nested message loop to initialize
        // it. Doing that synchronously from inside this WebView2's own WebMessageReceived COM
        // callback is a known WebView2 re-entrancy hazard (EnsureCoreWebView2Async can hang or,
        // under a native debugger, crash with STATUS_BREAKPOINT). Yielding first lets this
        // callback return to the message loop normally before the second control is created.
        await System.Windows.Threading.Dispatcher.Yield();

        var dialog = mermaidDialogFactory.Create(
            appDataPaths,
            transport.EditorEnvironment,
            (_, replacement) => TryApplyMermaidEdit(
                session,
                tab,
                replacement));
        activeMermaidDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync(
                session.DialogRequest,
                owner,
                initializationLifetime.Token);
            TryPostMermaidReopenRequest(result, session, generation);
        }
        catch (OperationCanceledException) when (initializationLifetime.Token.IsCancellationRequested)
        {
        }
        catch
        {
            // The document surface remains authoritative when the optional editor fails.
        }
        finally
        {
            if (ReferenceEquals(activeMermaidDialog, dialog))
            {
                activeMermaidDialog = null;
            }

            QueueMermaidActionFocus(session, transport);
        }
    }

    private void QueueMermaidActionFocus(
        MermaidDocumentEditSession session,
        IMermaidSurfaceTransport originatingTransport)
    {
        if (disposed ||
            getMermaidTab() is not { Buffer: not null } tab ||
            !session.TryCreateFocusRequest(
                mermaidDialogGeneration,
                coordinator.CurrentResponse,
                windowId,
                tab.Id,
                tab.Revision,
                originatingTransport.Control,
                out var request))
        {
            return;
        }

        try
        {
            _ = mermaidFocusRestoration.Begin(request, originatingTransport.PostMessage);
        }
        catch (ObjectDisposedException)
        {
            // A concurrent process recovery owns focus for the replacement surface.
        }
    }

    private void CancelPendingMermaidFocus() => mermaidFocusRestoration.Cancel();

    private void TryPostMermaidReopenRequest(
        MermaidDialogResult result,
        MermaidDocumentEditSession session,
        long dialogGeneration)
    {
        if (disposed ||
            coordinator.CurrentResponse is not { } current ||
            getMermaidTab() is not { Buffer: not null } tab ||
            !session.TryCreateReopenMessage(
                result,
                dialogGeneration,
                mermaidDialogGeneration,
                current,
                windowId,
                tab.Id,
                tab.Revision,
                out var json))
        {
            return;
        }

        getMermaidTransport().PostMessage(json);
    }

    private MermaidApplyResult TryApplyMermaidEdit(
        MermaidDocumentEditSession session,
        DocumentTabViewModel tab,
        string replacement)
    {
        if (disposed ||
            !ReferenceEquals(tab, getMermaidTab()) ||
            tab.Buffer is not { } buffer)
        {
            return MermaidApplyResult.StaleRevision;
        }

        return session.TryApply(
            mermaidDialogGeneration,
            coordinator.CurrentResponse,
            windowId,
            buffer,
            message => DocumentChanged?.Invoke(message),
            coordinator.TryUpdateCurrentResponse,
            () => getMermaidTransport().PostMessage(
                WebViewPolicy.CreateDocumentActivationMessage(
                    tab,
                    session.OwnerRequestId,
                    windowId)),
            replacement);
    }

    private void RejectDocumentChange(WebResponseContext current)
    {
        var currentRevision = lastRequestedTab is { } tab && tab.Id == current.TabId
            ? tab.Revision
            : current.Revision;
        var pending = resyncTracker.GetOrBegin(current, currentRevision);
        Browser.CoreWebView2.PostWebMessageAsJson(
            WebViewPolicy.CreateDocumentChangeRejectedMessage(
                current,
                windowId,
                currentRevision,
                pending.RequestId));
    }

    private bool IsPendingResync(WebMessageEnvelope message, WebResponseContext current) =>
        string.Equals(message.Type, "document.resync", StringComparison.Ordinal) &&
        message.WindowId == windowId &&
        message.TabId == current.TabId &&
        resyncTracker.IsCurrent(current, message.RequestId, message.DocumentRevision) &&
        message.Payload.ValueKind == JsonValueKind.Object &&
        !message.Payload.EnumerateObject().Any();

    private void HandleResyncRequest(WebResponseContext current)
    {
        if (lastRequestedTab is not { } tab ||
            tab.Id != current.TabId ||
            !resyncTracker.TryTake(current, tab.Revision, out var pending) ||
            !coordinator.TryUpdateCurrentResponse(
                current,
                pending.RequestId,
                pending.Revision))
        {
            ClearPendingResync();
            return;
        }

        PostActivationMessage(tab, pending.RequestId);
    }

    private static bool IsCorrelatedEditType(string type) =>
        type is "document.changed" or
            "document.changeBatchStart" or
            "document.changeBatchChunk" or
            "document.changeBatchCommit";

    private void ClearPendingResync()
    {
        resyncTracker.Clear();
    }

    private Task PostFindMessageAsync(string type) =>
        PostCurrentMessageAsync(response => WebViewPolicy.CreateFindMessage(response, windowId, type));

    private Task PostOwnedFindMessageAsync(Guid tabId, long revision, string type) =>
        PostOwnedMessageAsync(
            tabId,
            revision,
            response => WebViewPolicy.CreateFindMessage(response, windowId, type));

    private Task PostCurrentMessageAsync(Func<WebResponseContext, string> createMessage)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(createMessage);
        var response = coordinator.CurrentResponse ??
            throw new InvalidOperationException("There is no active document response context.");
        Browser.CoreWebView2.PostWebMessageAsJson(createMessage(response));
        return Task.CompletedTask;
    }

    private void MarkActivationFailed(WebActivationStamp activation, WebSurfaceFailure failure)
    {
        coordinator.MarkFailed(activation, failure);
        ResetPendingReady();
        SurfaceError.Visibility = Visibility.Visible;
    }

    private void MarkSurfaceFailed(
        WebSurfaceFailure failure,
        bool interactWithBrowser = true)
    {
        if (coordinator.RequestedTabId is null)
        {
            ResetPendingReady(interactWithBrowser);
            ClearDocumentAssetMapping(interactWithBrowser);
            SurfaceError.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Collapsed;
            return;
        }

        coordinator.MarkFailed(failure);
        ResetPendingReady(interactWithBrowser);
        ClearDocumentAssetMapping(interactWithBrowser);
        SurfaceError.Visibility = Visibility.Visible;
    }

    private void ResetPendingReady(bool stopBrowser = true)
    {
        navigationAttempts.Clear();
        if (stopBrowser)
        {
            Browser.CoreWebView2?.Stop();
        }

        var ready = pendingReady;
        pendingReady = null;
        pendingReadyTabId = Guid.Empty;
        ready?.TrySetResult(false);
    }

    private async void RetryInitialization_Click(object sender, RoutedEventArgs e)
    {
        if (recoveryController?.CanRetry == true)
        {
            try
            {
                await recoveryController.RetryAsync();
            }
            catch
            {
                // The nonblocking surface error remains visible for another retry.
            }

            return;
        }

        if (lastRequestedTab is null || !coordinator.CanRetry)
        {
            return;
        }

        var tab = lastRequestedTab;
        var retry = coordinator.BeginRetry();
        try
        {
            await ActivateCoreAsync(tab, retry, initializationLifetime.Token);
        }
        catch
        {
            // The nonblocking surface error remains visible for another retry.
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (recoveryController is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(recoveryController.CopyDiagnostics());
        }
        catch
        {
            // Clipboard contention must not replace the recovery error.
        }
    }

    IReadOnlyList<WebViewRecoveryTabSnapshot> IWebViewRecoveryOperations.CaptureTabs()
    {
        var tabs = captureTabs?.Invoke() ?? [];
        return tabs
            .Where(tab => tab.Buffer is not null)
            .Select(tab =>
            {
                var buffer = tab.Buffer!.CaptureSnapshot();
                return new WebViewRecoveryTabSnapshot(
                    buffer.TabId,
                    buffer.Path,
                    buffer.Text,
                    buffer.Revision,
                    buffer.IsDirty,
                    tab.Mode,
                    tab.UiHints,
                    buffer.PreferredNewLine);
            })
            .ToArray();
    }

    Guid? IWebViewRecoveryOperations.CaptureActiveTabId() => captureActiveTab?.Invoke()?.Id;

    Guid IWebViewRecoveryOperations.CaptureBootstrapTabId() => windowId;

    Task IWebViewRecoveryOperations.ReplaceBrokenSurfaceAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        recoveryGeneration = generation;
        recoveryActivation = null;
        mermaidFocusRestoration.Cancel();
        changeBatches.CancelAll();
        ClearPendingResync();
        MarkSurfaceFailed(WebSurfaceFailure.ProcessFailed, interactWithBrowser: false);
        initializationLifetime.Reset();
        var broken = Browser;
        try
        {
            browserLifetime.Recreate(() => handlerLifetime.TryUnregister());
        }
        catch when (!ReferenceEquals(Browser, broken))
        {
            // Handler cleanup failed after the replacement was already installed.
        }

        Browser.Visibility = Visibility.Collapsed;
        return Task.CompletedTask;
    }

    private Task PostOwnedMessageAsync(
        Guid tabId,
        long revision,
        Func<WebResponseContext, string> createMessage)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(createMessage);
        if (coordinator.CurrentResponse is not { } response ||
            response.TabId != tabId ||
            response.Revision != revision)
        {
            return Task.CompletedTask;
        }

        Browser.CoreWebView2.PostWebMessageAsJson(createMessage(response));
        return Task.CompletedTask;
    }

    async Task IWebViewRecoveryOperations.InitializeAndWaitForReadyAsync(
        long generation,
        Guid bootstrapTabId,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryGeneration(generation, cancellationToken);
        var activation = coordinator.BeginActivation(bootstrapTabId);
        recoveryActivation = activation;
        Browser.Visibility = Visibility.Visible;
        coordinator.MarkInitializing(activation);
        await EnsureInitializedAsync(cancellationToken);
        EnsureRecoveryGeneration(generation, cancellationToken);
        if (!await EnsureSurfaceReadyAsync(activation, cancellationToken))
        {
            throw new InvalidOperationException("The replacement document surface did not become ready.");
        }

        EnsureRecoveryGeneration(generation, cancellationToken);
    }

    Task IWebViewRecoveryOperations.RehydrateTabAsync(
        long generation,
        WebViewRecoveryTabSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryGeneration(generation, cancellationToken);
        Browser.CoreWebView2.PostWebMessageAsJson(
            WebViewPolicy.CreateDocumentRecoveryMessage(snapshot, Guid.NewGuid(), windowId));
        return Task.CompletedTask;
    }

    Task IWebViewRecoveryOperations.ActivateTabAsync(
        long generation,
        WebViewRecoveryTabSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryGeneration(generation, cancellationToken);
        var currentTab = captureActiveTab?.Invoke();
        if (currentTab is null ||
            currentTab.Id != snapshot.TabId ||
            currentTab.Buffer?.CaptureSnapshot() is not { } currentBuffer ||
            !Matches(snapshot, currentBuffer, currentTab))
        {
            throw new InvalidOperationException("The active document changed during WebView recovery.");
        }

        var activation = recoveryActivation ??
            throw new InvalidOperationException("The replacement surface is not initialized.");
        MapDocumentAssets(snapshot.Path);
        var requestId = Guid.NewGuid();
        if (!WebSurfaceActivationTransaction.TryPost(
                coordinator,
                activation,
                requestId,
                snapshot.Revision,
                () => Browser.CoreWebView2.PostWebMessageAsJson(
                    WebViewPolicy.CreateDocumentRecoveryMessage(snapshot, requestId, windowId))))
        {
            throw new InvalidOperationException("The replacement surface activation is stale.");
        }

        lastRequestedTab = currentTab;
        return Task.CompletedTask;
    }

    void IWebViewRecoveryOperations.DeactivateRecoveredSurface(long generation)
    {
        EnsureRecoveryGeneration(generation, CancellationToken.None);
        coordinator.Deactivate();
        lastRequestedTab = null;
        ClearDocumentAssetMapping();
        Browser.Visibility = Visibility.Collapsed;
    }

    void IWebViewRecoveryOperations.ShowRecoveryFailure(long generation)
    {
        if (!disposed && recoveryGeneration == generation)
        {
            SurfaceError.Visibility = Visibility.Visible;
        }
    }

    void IWebViewRecoveryOperations.ClearRecoveryFailure(long generation)
    {
        if (!disposed && recoveryGeneration == generation)
        {
            SurfaceError.Visibility = Visibility.Collapsed;
            Browser.Visibility = coordinator.HasActiveDocument
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private bool IsCurrentCore(object? sender)
    {
        if (disposed)
        {
            return false;
        }

        try
        {
            return sender is CoreWebView2 core && ReferenceEquals(core, Browser.CoreWebView2);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void EnsureRecoveryGeneration(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || recoveryGeneration != generation)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static bool Matches(
        WebViewRecoveryTabSnapshot expected,
        Core.Documents.DocumentBufferSnapshot current,
        DocumentTabViewModel tab) =>
        current.TabId == expected.TabId &&
        current.Revision == expected.Revision &&
        current.IsDirty == expected.IsDirty &&
        string.Equals(current.Path, expected.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.Text, expected.Text, StringComparison.Ordinal) &&
        string.Equals(current.PreferredNewLine, expected.PreferredNewLine, StringComparison.Ordinal) &&
        tab.Mode == expected.Mode &&
        Equals(tab.UiHints, expected.UiHints);
}
