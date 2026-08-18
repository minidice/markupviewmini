using System.Text.Json;
using MarkUpViewMini.App.Localization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Mermaid;
using MarkUpViewMini.Infrastructure.Paths;
using Microsoft.Web.WebView2.Core;

namespace MarkUpViewMini.App.Mermaid;

public partial class MermaidEditDialog : Window, IMermaidEditDialog
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri BootstrapUri = new("https://mermaid-editor.local/index.html");
    private readonly IAppDataPaths paths;
    private readonly CoreWebView2Environment environment;
    private readonly Func<MermaidBlockSnapshot, string, MermaidApplyResult> apply;
    private readonly WebViewHandlerLifetime handlerLifetime = new();
    private readonly TaskCompletionSource editorReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? lifetimeCancellation;
    private CancellationTokenRegistration cancellationRegistration;
    private MermaidEditRequest? request;
    private MermaidDialogBridge? bridge;
    private Task? initializationTask;
    private Exception? initializationException;
    private MermaidDialogResult? finalResult;
    private string? conflictCandidate;
    private bool showing;
    private bool cleanedUp;
    private int terminalHandled;

    public MermaidEditDialog(
        IAppDataPaths paths,
        CoreWebView2Environment environment,
        Func<MermaidBlockSnapshot, string, MermaidApplyResult> apply)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.apply = apply ?? throw new ArgumentNullException(nameof(apply));
        InitializeComponent();
    }

    public async Task<MermaidDialogResult> ShowAsync(
        MermaidEditRequest request,
        Window owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (showing)
        {
            throw new InvalidOperationException("A Mermaid dialog instance can only be shown once.");
        }

        showing = true;
        this.request = request;
        Owner = owner;
        bridge = new MermaidDialogBridge(request, PostMessage);
        lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationRegistration = cancellationToken.Register(() =>
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                {
                    Close();
                }
            });
        });

        try
        {
            ShowDialog();
            lifetimeCancellation.Cancel();
            if (initializationTask is not null)
            {
                try
                {
                    await initializationTask;
                }
                catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    initializationException ??= exception;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (initializationException is not null)
            {
                throw new InvalidOperationException(
                    "The local Mermaid editor could not be started.",
                    initializationException);
            }

            return finalResult ?? MermaidDialogResult.Canceled(request.Snapshot.Source);
        }
        finally
        {
            Cleanup();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        initializationTask = InitializeAsync(lifetimeCancellation?.Token ?? CancellationToken.None);
        try
        {
            await initializationTask;
        }
        catch (OperationCanceledException) when (lifetimeCancellation?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            initializationException = exception;
            bridge?.TryCancel();
            if (IsVisible)
            {
                Close();
            }
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.WebView2Directory);
        var assetsDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "web", "mermaid-editor"));
        if (!File.Exists(Path.Combine(assetsDirectory, "index.html")) ||
            !File.Exists(Path.Combine(assetsDirectory, "dist", "editor.js")) ||
            !File.Exists(Path.Combine(assetsDirectory, "dist", "editor.css")))
        {
            throw new FileNotFoundException("The packaged Mermaid editor is unavailable.");
        }

        await Browser.EnsureCoreWebView2Async(environment)
            .WaitAsync(InitializationTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var core = Browser.CoreWebView2 ??
            throw new InvalidOperationException("WebView2 initialization did not create a core instance.");
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            BootstrapUri.Host,
            assetsDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        SubscribeHandlers(core);
        cancellationToken.ThrowIfCancellationRequested();
        core.Navigate(BootstrapUri.AbsoluteUri);
        await editorReady.Task.WaitAsync(ReadyTimeout, cancellationToken);
        PostLocale();
    }

    /// <summary>Tells the editor which language to label itself in.</summary>
    /// <remarks>
    /// Sent as its own message rather than folded into mermaid.open: that payload is validated
    /// down to its exact key set on both sides, and the UI language is unrelated to the editing
    /// session it describes. The dialog is modal, so one message at start-up is enough - the
    /// language cannot change while it is open.
    /// </remarks>
    private void PostLocale() => PostMessage(JsonSerializer.Serialize(new
    {
        version = 1,
        type = "mermaid.locale",
        payload = new { language = AppLocalization.Source.CurrentCode },
    }));

    private void SubscribeHandlers(CoreWebView2 core)
    {
        handlerLifetime.TryRegister(
        [
            new(
                () => core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All),
                () => core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All)),
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
        if (!IsCurrentCore(sender) ||
            !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
            source != BootstrapUri ||
            bridge is null)
        {
            return;
        }

        try
        {
            if (!bridge.TryHandleMessage(e.WebMessageAsJson))
            {
                return;
            }

            ConfirmButton.IsEnabled = bridge.CurrentSourceSupported;
            if (!bridge.Completion.IsCompleted ||
                Interlocked.Exchange(ref terminalHandled, 1) != 0)
            {
                return;
            }

            await HandleTerminalAsync(await bridge.Completion);
        }
        catch (Exception exception)
        {
            initializationException = exception;
            if (IsVisible)
            {
                Close();
            }
        }
    }

    private Task HandleTerminalAsync(MermaidDialogResult result)
    {
        if (!result.IsConfirmed || request is null)
        {
            finalResult = MermaidDialogResult.Canceled(request?.Snapshot.Source ?? result.Source);
            Close();
            return Task.CompletedTask;
        }

        var applyResult = apply(request.Snapshot, result.Source);
        if (applyResult == MermaidApplyResult.Applied)
        {
            finalResult = result;
            Close();
            return Task.CompletedTask;
        }

        if (applyResult is MermaidApplyResult.StaleRevision or MermaidApplyResult.RangeChanged)
        {
            conflictCandidate = result.Source;
            Browser.IsEnabled = false;
            ConflictMessage.Text = Strings.Get("mermaid.dialog.conflictMessage");
            ConflictPanel.Visibility = Visibility.Visible;
            return Task.CompletedTask;
        }

        finalResult = MermaidDialogResult.Canceled(request.Snapshot.Source);
        initializationException = new InvalidOperationException(
            "The Mermaid editor returned an unsafe replacement range.");
        Close();
        return Task.CompletedTask;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled ||
            Browser.CoreWebView2 is null ||
            bridge?.Completion.IsCompleted == true)
        {
            return;
        }

        try
        {
            await Browser.ExecuteScriptAsync("document.querySelector('[data-confirm]')?.click();");
        }
        catch (Exception exception)
        {
            initializationException = exception;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        finalResult = MermaidDialogResult.Canceled(request?.Snapshot.Source ?? string.Empty);
        bridge?.TryCancel();
        Close();
    }

    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        finalResult = MermaidDialogResult.ReopenRequested(request?.Snapshot.Source ?? string.Empty);
        Close();
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (request is null || conflictCandidate is null)
        {
            return;
        }

        ComparisonText.Text = Strings.Format(
            "mermaid.dialog.comparison",
            request.Snapshot.Source,
            conflictCandidate);
        ComparisonText.Visibility = Visibility.Visible;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        finalResult ??= MermaidDialogResult.Canceled(request?.Snapshot.Source ?? string.Empty);
        bridge?.TryCancel();
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsCurrentCore(sender) ||
            !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            uri != BootstrapUri)
        {
            e.Cancel = true;
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (IsCurrentCore(sender) && !e.IsSuccess)
        {
            initializationException = new InvalidOperationException("The local Mermaid editor navigation failed.");
            if (IsVisible)
            {
                Close();
            }
        }
    }

    private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (IsCurrentCore(sender) &&
            Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) &&
            IsAllowedLocalResource(uri))
        {
            return;
        }

        if (sender is not CoreWebView2 core)
        {
            return;
        }

        e.Response = core.Environment.CreateWebResourceResponse(
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

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (IsCurrentCore(sender))
        {
            initializationException = new InvalidOperationException("The Mermaid editor WebView process failed.");
            if (IsVisible)
            {
                Close();
            }
        }
    }

    private void PostMessage(string json)
    {
        if (cleanedUp || Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.PostWebMessageAsJson(json);
        editorReady.TrySetResult();
    }

    private bool IsCurrentCore(object? sender)
    {
        if (cleanedUp)
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

    private static bool IsAllowedLocalResource(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, BootstrapUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        return uri.AbsolutePath is "/index.html" or "/dist/editor.js" or "/dist/editor.css";
    }

    private void Cleanup()
    {
        if (cleanedUp)
        {
            return;
        }

        cleanedUp = true;
        cancellationRegistration.Dispose();
        lifetimeCancellation?.Cancel();
        lifetimeCancellation?.Dispose();
        bridge?.Dispose();
        try
        {
            handlerLifetime.TryUnregister();
        }
        finally
        {
            try
            {
                Browser.CoreWebView2?.ClearVirtualHostNameToFolderMapping(BootstrapUri.Host);
                Browser.CoreWebView2?.Stop();
            }
            finally
            {
                Browser.Dispose();
            }
        }
    }
}
