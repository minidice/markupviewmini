using System.ComponentModel;
using System.IO;
using System.Windows;
using MarkUpViewMini.Core.Activation;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.App.Composition;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Infrastructure.Activation;
using MarkUpViewMini.Infrastructure.Files;
using MarkUpViewMini.Infrastructure.Paths;
using MarkUpViewMini.Infrastructure.Recovery;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App;

internal sealed class ActivationWindowRegistry<TWindow>
    where TWindow : class
{
    private readonly Dictionary<TWindow, WindowState> windows = new(ReferenceEqualityComparer.Instance);
    private long activationOrder;

    public void Register(TWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        windows.TryAdd(window, new WindowState(DateTimeOffset.MinValue, 0, IsClosing: false));
    }

    public void RecordActivated(TWindow window, DateTimeOffset activatedAt)
    {
        if (windows.TryGetValue(window, out var state) && !state.IsClosing)
        {
            windows[window] = state with
            {
                ActivatedAt = activatedAt,
                ActivationOrder = ++activationOrder,
            };
        }
    }

    public void RecordClosing(TWindow window)
    {
        if (windows.TryGetValue(window, out var state))
        {
            windows[window] = state with { IsClosing = true };
        }
    }

    public void Remove(TWindow window) => windows.Remove(window);

    public TWindow GetOrCreate(Func<TWindow> createWindow)
    {
        ArgumentNullException.ThrowIfNull(createWindow);
        return windows
            .Where(item => !item.Value.IsClosing)
            .OrderByDescending(item => item.Value.ActivationOrder)
            .Select(item => item.Key)
            .FirstOrDefault() ?? createWindow();
    }

    private sealed record WindowState(
        DateTimeOffset ActivatedAt,
        long ActivationOrder,
        bool IsClosing);
}

public partial class App : Application
{
    static App()
    {
        RegisterEncodingProviders();
    }

    public static void RegisterEncodingProviders() =>
        DocumentFileService.RegisterCodePages();

    private readonly DocumentFormatRegistry formatRegistry = new([new MarkdownDocumentProvider()]);
    private readonly DocumentSaveArbiter saveArbiter = new();
    private readonly IAppDataPaths appDataPaths = SelectAppDataPaths();
    private SettingsService? settings;
    private SettingsShutdownCoordinator? settingsShutdown;
    private SessionService? session;
    private RecoveryService? recovery;
    private readonly List<MainWindow> sessionWindows = [];
    private readonly ActivationWindowRegistry<MainWindow> activationWindows = new();
    private readonly TaskCompletionSource activationRoutingReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppSessionMutationTracker sessionMutations = new();
    private SingleInstanceCoordinator? singleInstance;
    private bool exiting;
    private bool shutdownPreparationInProgress;
    private long windowSetGeneration;

    private static IAppDataPaths SelectAppDataPaths()
    {
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath) ??
            Path.GetFullPath(AppContext.BaseDirectory);
        var kind = File.Exists(Path.Combine(executableDirectory, "portable.marker"))
            ? AppDistributionKind.Portable
            : AppDistributionKind.Installed;
        return AppDataPathSelector.Select(
            kind,
            executableDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        singleInstance = new SingleInstanceCoordinator(
            "MarkUpViewMini.App",
            RouteActivationAsync);
        SingleInstanceResult instanceResult;
        try
        {
            instanceResult = await singleInstance.StartOrForwardAsync(
                new ActivationRequest(1, ActivationKind.FileOpen, e.Args, Environment.ProcessId),
                CancellationToken.None);
        }
        catch (Exception)
        {
            await singleInstance.DisposeAsync();
            singleInstance = null;
            Shutdown();
            return;
        }

        if (instanceResult == SingleInstanceResult.Forwarded)
        {
            await singleInstance.DisposeAsync();
            singleInstance = null;
            Shutdown();
            return;
        }

        settings = new SettingsService(appDataPaths);
        session = new SessionService(appDataPaths);
        recovery = new RecoveryService(appDataPaths);
        settingsShutdown = new SettingsShutdownCoordinator(settings);
        sessionMutations.BeginStartup();
        var recoveryFiles = new DocumentFileService();
        var recoveryResolver = new RecoveryDecisionResolver(
            recovery.LoadAvailableAsync,
            new NativeRecoveryDecisionDialog(),
            async (path, token) => (await recoveryFiles.LoadAsync(path, token)).Text,
            recovery.RemoveAsync);
        var startup = new SessionStartupCoordinator(
            async token => _ = await settings.LoadAsync(token),
            recoveryResolver,
            session.LoadAsync,
            CreateStartupWindowCandidate,
            count => (MainWindow as MarkUpViewMini.App.MainWindow)?.ShowSessionSummary(count));

        var windows = await startup.StartAsync(e.Args, Environment.CurrentDirectory, CancellationToken.None);
        if (windows.Count == 0)
        {
            sessionMutations.CompleteStartup();
            activationRoutingReady.TrySetCanceled();
            Shutdown();
            return;
        }

        session.ScheduleSave(CaptureCurrentSession(), SessionSaveReason.AutomaticRestore);
        sessionMutations.CompleteStartup();
        activationRoutingReady.TrySetResult();
    }

    internal MainWindow CreateWindow(Guid? sessionWindowId = null)
    {
        var window = CreateStartupWindowCandidate(sessionWindowId);
        RegisterWindow(window, isUserMutation: true);
        return window;
    }

    private MainWindow CreateStartupWindowCandidate(Guid? sessionWindowId)
    {
        var previous = MainWindow;
        var candidate = new MainWindow(
            formatRegistry,
            appDataPaths,
            settings ??= new SettingsService(appDataPaths),
            sessionWindowId,
            recovery,
            saveArbiter);
        StartupMainWindowOwnership.PreservePrevious(this, candidate, previous);
        return candidate;
    }

    internal void CommitStartupWindow(MainWindow window)
    {
        RegisterWindow(window, isUserMutation: false);
        if (MainWindow is null)
        {
            StartupMainWindowOwnership.Commit(this, window);
        }

        window.Show();
    }

    internal void AbandonStartupWindow(MainWindow window)
    {
        var previous = MainWindow;
        if (ReferenceEquals(previous, window))
        {
            previous = sessionWindows.FirstOrDefault(candidate => !ReferenceEquals(candidate, window));
        }

        StartupMainWindowOwnership.Abandon(this, window, previous);
    }

    private void RegisterWindow(MainWindow window, bool isUserMutation)
    {
        if (sessionWindows.Contains(window))
        {
            return;
        }

        sessionWindows.Add(window);
        activationWindows.Register(window);
        window.Activated += Window_Activated;
        window.Closing += Window_Closing;
        windowSetGeneration++;
        MainWindow ??= window;
        ScheduleSessionCapture(isUserMutation);
    }

    internal void ScheduleSessionCapture(bool isUserMutation = true)
    {
        if (exiting || session is null)
        {
            return;
        }

        session.ScheduleSave(
            CaptureCurrentSession(),
            isUserMutation
                ? sessionMutations.RecordMutation()
                : sessionMutations.CaptureWithoutMutation());
    }

    internal void FlushSessionBeforeWindowDisposal(MainWindow closingWindow)
    {
        if (exiting || session is null)
        {
            return;
        }

        try
        {
            var current = CaptureCurrentSession();
            session.ScheduleSave(
                SessionCloseCapture.Create(
                    current.Windows,
                    closingWindow.CaptureSession().WindowId),
                current.Windows.Count <= 1
                    ? sessionMutations.CaptureLastWindowClose()
                    : sessionMutations.RecordMutation());
            session.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
    }

    internal async void ShutdownCoherently()
    {
        if (exiting || shutdownPreparationInProgress)
        {
            return;
        }

        shutdownPreparationInProgress = true;
        try
        {
            var windows = sessionWindows.ToArray();
            var capturedWindowSetGeneration = windowSetGeneration;
            var ownership = windows.Select(window => window.CaptureShutdownOwnership()).ToArray();
            var requests = new List<DirtyCloseRequest>();
            foreach (var window in windows)
            {
                if (!window.TryCreateApplicationShutdownRequest(out var request) || request is null)
                {
                    AbortApplicationShutdown();
                    return;
                }

                requests.Add(request);
            }

            bool ValidateWindowOwnership() =>
                windowSetGeneration == capturedWindowSetGeneration &&
                sessionWindows.Count == windows.Length &&
                sessionWindows.SequenceEqual(windows) &&
                ownership.All(item => item.Window.IsCurrentShutdownOwnership(item));

            if (!await DirtyCloseCoordinator.TryResolveAsync(
                    requests,
                    ValidateWindowOwnership,
                    CancellationToken.None) ||
                !ValidateWindowOwnership())
            {
                AbortApplicationShutdown();
                return;
            }

            var resolvedTabOwnership = windows
                .Select(window => window.CaptureShutdownTabOwnership())
                .ToArray();

            try
            {
                ScheduleSessionCapture(isUserMutation: false);
                if (session is not null)
                {
                    await session.FlushAsync(CancellationToken.None);
                }
            }
            catch (Exception)
            {
            }

            if (!ValidateWindowOwnership() ||
                resolvedTabOwnership.Any(item => !item.Window.IsCurrentShutdownTabOwnership(item)))
            {
                AbortApplicationShutdown();
                return;
            }

            foreach (var window in windows)
            {
                window.ApproveApplicationShutdown();
            }

            exiting = true;
            Shutdown();
        }
        finally
        {
            shutdownPreparationInProgress = false;
        }
    }

    internal void RemoveClosedWindow(MainWindow window)
    {
        window.Activated -= Window_Activated;
        window.Closing -= Window_Closing;
        activationWindows.Remove(window);
        if (sessionWindows.Remove(window))
        {
            windowSetGeneration++;
        }
    }

    private void AbortApplicationShutdown() =>
        ApplicationShutdownAbortCoordinator.AbortCurrentWindows(
            sessionWindows,
            static window => window.AbortApplicationShutdown());

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            activationWindows.RecordActivated(window, DateTimeOffset.UtcNow);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!e.Cancel && sender is MainWindow window)
        {
            activationWindows.RecordClosing(window);
        }
    }

    private async Task RouteActivationAsync(
        ActivationRequest request,
        CancellationToken cancellationToken)
    {
        await activationRoutingReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(async () =>
        {
            var created = false;
            var window = activationWindows.GetOrCreate(() =>
            {
                created = true;
                return CreateWindow();
            });
            if (created)
            {
                window.Show();
            }

            if (window.DataContext is ShellViewModel shell)
            {
                await shell.OpenActivationPathsAsync(request.Paths, cancellationToken);
            }

            if (window.WindowState == WindowState.Minimized)
            {
                SystemCommands.RestoreWindow(window);
            }

            window.Activate();
        }).Task.Unwrap().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private SessionV1 CaptureCurrentSession() => new()
    {
        Windows = sessionWindows
            .Where(window => !window.IsLoaded || window.IsVisible)
            .Select(window => window.CaptureSession())
            .ToArray(),
    };

    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true;
        activationRoutingReady.TrySetCanceled();
        try
        {
            singleInstance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        try
        {
            session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        try
        {
            recovery?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }

        settingsShutdown?.Complete();
        base.OnExit(e);
    }
}
