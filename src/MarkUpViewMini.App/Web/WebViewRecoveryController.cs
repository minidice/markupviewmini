using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Diagnostics;

namespace MarkUpViewMini.App.Web;

internal sealed record WebViewRecoveryTabSnapshot(
    Guid TabId,
    string Path,
    string Text,
    long Revision,
    bool IsDirty,
    DocumentMode Mode,
    DocumentUiHints UiHints,
    string PreferredNewLine);

internal interface IWebViewRecoveryOperations
{
    IReadOnlyList<WebViewRecoveryTabSnapshot> CaptureTabs();

    Guid? CaptureActiveTabId();

    Guid CaptureBootstrapTabId();

    Task ReplaceBrokenSurfaceAsync(long generation, CancellationToken cancellationToken);

    Task InitializeAndWaitForReadyAsync(
        long generation,
        Guid bootstrapTabId,
        CancellationToken cancellationToken);

    Task RehydrateTabAsync(
        long generation,
        WebViewRecoveryTabSnapshot snapshot,
        CancellationToken cancellationToken);

    Task ActivateTabAsync(
        long generation,
        WebViewRecoveryTabSnapshot snapshot,
        CancellationToken cancellationToken);

    void DeactivateRecoveredSurface(long generation);

    void ShowRecoveryFailure(long generation);

    void ClearRecoveryFailure(long generation);
}

internal sealed class WebViewRecoveryController : IDisposable
{
    private const int MaxCopiedDiagnosticsLength = 256 * 1024;
    private readonly object gate = new();
    private readonly IWebViewRecoveryOperations operations;
    private readonly ISafeLogger logger;
    private readonly Func<string> readSafeDiagnostics;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Task? activeRecovery;
    private CancellationTokenSource? activeRecoveryCancellation;
    private long generation;
    private bool canRetry;
    private bool disposed;

    public WebViewRecoveryController(
        IWebViewRecoveryOperations operations,
        ISafeLogger logger,
        Func<string> readSafeDiagnostics)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.readSafeDiagnostics = readSafeDiagnostics ??
            throw new ArgumentNullException(nameof(readSafeDiagnostics));
    }

    public bool CanRetry
    {
        get
        {
            lock (gate)
            {
                return !disposed && canRetry && activeRecovery is null;
            }
        }
    }

    public Task HandleProcessFailureAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return activeRecovery ?? StartRecoveryLocked();
        }
    }

    public Task RetryAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeRecovery is not null)
            {
                return activeRecovery;
            }

            if (!canRetry)
            {
                return Task.FromException(new InvalidOperationException(
                    "The document surface is not waiting for a retry."));
            }

            return StartRecoveryLocked();
        }
    }

    public void SupersedeCurrentRecovery()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed || activeRecovery is null)
            {
                return;
            }

            generation++;
            canRetry = false;
            activeRecovery = null;
            cancellation = activeRecoveryCancellation;
            activeRecoveryCancellation = null;
        }

        cancellation?.Cancel();
    }

    public string CopyDiagnostics()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        try
        {
            var text = readSafeDiagnostics() ?? string.Empty;
            return text.Length <= MaxCopiedDiagnosticsLength
                ? text
                : text[^MaxCopiedDiagnosticsLength..];
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            canRetry = false;
            activeRecovery = null;
            cancellation = activeRecoveryCancellation;
            activeRecoveryCancellation = null;
        }

        cancellation?.Cancel();
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
    }

    private Task StartRecoveryLocked()
    {
        canRetry = false;
        var recoveryGeneration = ++generation;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token);
        activeRecovery = completion.Task;
        activeRecoveryCancellation = cancellation;
        _ = RunRecoveryAsync(
            recoveryGeneration,
            completion,
            cancellation);
        return completion.Task;
    }

    private async Task RunRecoveryAsync(
        long recoveryGeneration,
        TaskCompletionSource completion,
        CancellationTokenSource recoveryCancellation)
    {
        var cancellationToken = recoveryCancellation.Token;
        string? activePath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bootstrapTabId = operations.CaptureBootstrapTabId();
            if (bootstrapTabId == Guid.Empty)
            {
                throw new InvalidOperationException("Recovery requires a bootstrap owner.");
            }

            logger.Write("WebView", "ProcessFailed", activePath, error: null);

            await operations.ReplaceBrokenSurfaceAsync(
                    recoveryGeneration,
                    cancellationToken)
                .WaitAsync(cancellationToken);
            EnsureCurrent(recoveryGeneration, cancellationToken);

            await operations.InitializeAndWaitForReadyAsync(
                    recoveryGeneration,
                    bootstrapTabId,
                    cancellationToken)
                .WaitAsync(cancellationToken);
            EnsureCurrent(recoveryGeneration, cancellationToken);

            var tabs = ValidateTabs(operations.CaptureTabs());
            var active = FindActive(tabs, operations.CaptureActiveTabId());
            activePath = active?.Path;

            foreach (var tab in tabs)
            {
                if (tab.TabId == active?.TabId)
                {
                    continue;
                }

                await operations.RehydrateTabAsync(
                        recoveryGeneration,
                        tab,
                        cancellationToken)
                    .WaitAsync(cancellationToken);
                EnsureCurrent(recoveryGeneration, cancellationToken);
            }

            if (active is null)
            {
                operations.DeactivateRecoveredSurface(recoveryGeneration);
            }
            else
            {
                await operations.ActivateTabAsync(
                        recoveryGeneration,
                        active,
                        cancellationToken)
                    .WaitAsync(cancellationToken);
                EnsureCurrent(recoveryGeneration, cancellationToken);
            }

            operations.ClearRecoveryFailure(recoveryGeneration);
            logger.Write("WebView", "RecoverySucceeded", activePath, error: null);
            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception error)
        {
            if (IsCurrent(recoveryGeneration))
            {
                lock (gate)
                {
                    if (!disposed && generation == recoveryGeneration)
                    {
                        canRetry = true;
                    }
                }

                logger.Write("WebView", "RecoveryFailed", activePath, error);
                operations.ShowRecoveryFailure(recoveryGeneration);
            }

            completion.TrySetException(error);
        }
        finally
        {
            lock (gate)
            {
                if (generation == recoveryGeneration &&
                    ReferenceEquals(activeRecovery, completion.Task))
                {
                    activeRecovery = null;
                    activeRecoveryCancellation = null;
                }
            }

            recoveryCancellation.Dispose();
        }
    }

    private static IReadOnlyList<WebViewRecoveryTabSnapshot> ValidateTabs(
        IReadOnlyList<WebViewRecoveryTabSnapshot> tabs)
    {
        var seen = new HashSet<Guid>();
        foreach (var tab in tabs)
        {
            if (tab.TabId == Guid.Empty ||
                tab.Revision < 0 ||
                tab.UiHints is null ||
                !seen.Add(tab.TabId))
            {
                throw new InvalidOperationException("Recovery tab snapshots are invalid.");
            }

        }

        return tabs;
    }

    private static WebViewRecoveryTabSnapshot? FindActive(
        IReadOnlyList<WebViewRecoveryTabSnapshot> tabs,
        Guid? activeTabId) =>
        activeTabId is null || activeTabId == Guid.Empty
            ? null
            : tabs.FirstOrDefault(tab => tab.TabId == activeTabId);

    private bool IsCurrent(long expectedGeneration)
    {
        lock (gate)
        {
            return !disposed && generation == expectedGeneration;
        }
    }

    private void EnsureCurrent(long expectedGeneration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(expectedGeneration))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
