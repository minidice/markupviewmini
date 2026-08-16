using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Infrastructure.Windows;
using Microsoft.Win32;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class WindowsIntegrationSettingsViewModelTests
{
    [Fact]
    public async Task Refresh_publishes_registration_status_inside_the_dispatcher()
    {
        // Break caught: a registry continuation raises bound state on a worker thread.
        var service = new StubFileAssociationService
        {
            GetStatus = () => Task.FromResult(new FileAssociationStatus(true)),
        };
        var dispatcher = new RecordingDispatcher();
        using var viewModel = new WindowsIntegrationSettingsViewModel(
            service,
            new StubShortcutService(),
            @"C:\app\MarkUpViewMini.App.exe",
            dispatcher.Dispatch);
        viewModel.PropertyChanged += (_, _) => Assert.True(dispatcher.IsDispatching);

        await Task.Run(viewModel.RefreshAsync);

        Assert.True(viewModel.IsRegistered);
        Assert.Equal("파일 형식이 등록되어 있습니다.", viewModel.StatusText);
        Assert.Contains("Windows에서 사용자가 선택", viewModel.GuidanceText);
    }

    [Fact]
    public async Task Register_command_rejects_reentry_until_the_active_operation_finishes()
    {
        // Break caught: double-clicking registration races two registry plans and stale status updates.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new StubFileAssociationService
        {
            Register = async _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task;
            },
            GetStatus = () => Task.FromResult(new FileAssociationStatus(true)),
        };
        using var viewModel = new WindowsIntegrationSettingsViewModel(
            service,
            new StubShortcutService(),
            @"C:\app\MarkUpViewMini.App.exe",
            action => action());

        viewModel.RegisterCommand.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.RegisterCommand.CanExecute(null));

        viewModel.RegisterCommand.Execute(null);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.TrySetResult();
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.True(viewModel.IsRegistered);
        Assert.True(viewModel.RegisterCommand.CanExecute(null));
    }

    [Fact]
    public async Task Registration_failure_is_exposed_as_nonmodal_state()
    {
        // Break caught: a registry exception escapes an async command and terminates the UI thread.
        var service = new StubFileAssociationService
        {
            Register = _ => Task.FromException(new UnauthorizedAccessException("denied")),
        };
        using var viewModel = new WindowsIntegrationSettingsViewModel(
            service,
            new StubShortcutService(),
            @"C:\app\MarkUpViewMini.App.exe",
            action => action());

        var exception = await Record.ExceptionAsync(viewModel.RegisterAsync);

        Assert.Null(exception);
        Assert.True(viewModel.HasError);
        Assert.Equal("파일 형식을 등록할 수 없습니다.", viewModel.ErrorMessage);
    }

    [Fact]
    public void Default_apps_launch_failure_is_exposed_without_a_dialog_or_throw()
    {
        // Break caught: process-launch failure is swallowed or escapes the command handler.
        var service = new StubFileAssociationService
        {
            OpenSettings = () => throw new InvalidOperationException("no handler"),
        };
        using var viewModel = new WindowsIntegrationSettingsViewModel(
            service,
            new StubShortcutService(),
            @"C:\app\MarkUpViewMini.App.exe",
            action => action());

        var exception = Record.Exception(viewModel.OpenWindowsDefaultAppsSettings);

        Assert.Null(exception);
        Assert.True(viewModel.HasError);
        Assert.Equal("Windows 기본 앱 설정을 열 수 없습니다.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Dispose_disables_commands_and_ignores_a_late_status_result()
    {
        // Break caught: closing a window lets an in-flight registry read mutate disposed WPF bindings.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<FileAssociationStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubFileAssociationService
        {
            GetStatus = () =>
            {
                entered.TrySetResult();
                return release.Task;
            },
        };
        var viewModel = new WindowsIntegrationSettingsViewModel(
            service,
            new StubShortcutService(),
            @"C:\app\MarkUpViewMini.App.exe",
            action => action());
        var refresh = viewModel.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Dispose();
        release.TrySetResult(new FileAssociationStatus(true));
        await refresh;

        Assert.False(viewModel.IsRegistered);
        Assert.Equal("파일 형식 등록 상태를 확인하는 중입니다…", viewModel.StatusText);
        Assert.False(viewModel.RegisterCommand.CanExecute(null));
        Assert.False(viewModel.UnregisterCommand.CanExecute(null));
        Assert.False(viewModel.OpenDefaultAppsSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dispose_does_not_release_the_process_operation_gate_while_registry_io_is_active()
    {
        // Break caught: closing one window releases a shared gate and lets another window read partial state.
        var gate = new FileAssociationOperationGate();
        var blockingRegistry = new BlockingRegistryStore();
        var firstService = new FileAssociationService(
            blockingRegistry,
            new NoOpProcessLauncher(),
            @"C:\app\MarkUpViewMini.App.exe",
            new ThreadPoolBackgroundExecutor(),
            new NoOpNotifier(),
            gate);
        var secondService = new FileAssociationService(
            new EmptyRegistryStore(),
            new NoOpProcessLauncher(),
            @"C:\app\MarkUpViewMini.App.exe",
            new ThreadPoolBackgroundExecutor(),
            new NoOpNotifier(),
            gate);
        var viewModel = new WindowsIntegrationSettingsViewModel(
            firstService,
            new StubShortcutService(),
            @"C:\app\MarkUpViewMini.App.exe",
            action => action());
        var registration = viewModel.RegisterAsync();
        await blockingRegistry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStatus = secondService.GetStatusAsync();

        viewModel.Dispose();
        try
        {
            Assert.False(secondStatus.IsCompleted);
        }
        finally
        {
            blockingRegistry.Release.Set();
        }

        await registration;
        Assert.False((await secondStatus).IsRegistered);
    }

    [Fact]
    public async Task Start_menu_shortcut_command_publishes_exact_status_inside_the_dispatcher()
    {
        // Break caught: shortcut I/O blocks WPF or its continuation raises bound state off-dispatcher.
        var shortcutService = new StubShortcutService
        {
            GetStatus = () => Task.FromResult(new ShortcutStatus(true, false)),
        };
        var dispatcher = new RecordingDispatcher();
        using var viewModel = new WindowsIntegrationSettingsViewModel(
            new StubFileAssociationService(),
            shortcutService,
            @"C:\app\MarkUpViewMini.App.exe",
            dispatcher.Dispatch);
        viewModel.PropertyChanged += (_, _) => Assert.True(dispatcher.IsDispatching);

        await Task.Run(viewModel.CreateStartMenuShortcutAsync);

        Assert.Equal(1, shortcutService.StartMenuCreations);
        Assert.True(viewModel.HasStartMenuShortcut);
        Assert.False(viewModel.HasDesktopShortcut);
        Assert.Equal("시작 메뉴 바로 가기가 있습니다.", viewModel.ShortcutStatusText);
    }

    [Fact]
    public async Task Shortcut_failure_is_exposed_as_nonmodal_state()
    {
        // Break caught: a Shell Link COM error escapes an async command and terminates the UI thread.
        var shortcutService = new StubShortcutService
        {
            CreateDesktop = () => Task.FromException(new UnauthorizedAccessException("denied")),
        };
        using var viewModel = new WindowsIntegrationSettingsViewModel(
            new StubFileAssociationService(),
            shortcutService,
            @"C:\app\MarkUpViewMini.App.exe",
            action => action());

        var exception = await Record.ExceptionAsync(viewModel.CreateDesktopShortcutAsync);

        Assert.Null(exception);
        Assert.True(viewModel.HasError);
        Assert.Equal("바탕 화면 바로 가기를 만들 수 없습니다.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Shortcut_operation_rejects_another_window_command_until_it_finishes()
    {
        // Break caught: one window starts file registration while its shortcut mutation is still active.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortcutService = new StubShortcutService
        {
            CreateStartMenu = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            },
        };
        using var viewModel = new WindowsIntegrationSettingsViewModel(
            new StubFileAssociationService(),
            shortcutService,
            @"C:\app\MarkUpViewMini.App.exe",
            action => action());

        viewModel.CreateStartMenuShortcutCommand.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CreateDesktopShortcutCommand.CanExecute(null));
        Assert.False(viewModel.RegisterCommand.CanExecute(null));

        release.TrySetResult();
        await WaitUntilAsync(() => !viewModel.IsBusy);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class StubFileAssociationService : IFileAssociationService
    {
        public Func<string, Task> Register { get; init; } = _ => Task.CompletedTask;

        public Func<Task> Unregister { get; init; } = () => Task.CompletedTask;

        public Func<Task<FileAssociationStatus>> GetStatus { get; init; } =
            () => Task.FromResult(new FileAssociationStatus(false));

        public Action OpenSettings { get; init; } = () => { };

        public Task RegisterAsync(string executablePath) => Register(executablePath);

        public Task UnregisterAsync() => Unregister();

        public Task<FileAssociationStatus> GetStatusAsync() => GetStatus();

        public void OpenWindowsDefaultAppsSettings() => OpenSettings();
    }

    private sealed class StubShortcutService : IShortcutService
    {
        public Func<Task> CreateStartMenu { get; init; } = () => Task.CompletedTask;

        public Func<Task> CreateDesktop { get; init; } = () => Task.CompletedTask;

        public Func<Task> Remove { get; init; } = () => Task.CompletedTask;

        public Func<Task<ShortcutStatus>> GetStatus { get; init; } =
            () => Task.FromResult(new ShortcutStatus(false, false));

        public int StartMenuCreations { get; private set; }

        public Task CreateStartMenuShortcutAsync()
        {
            StartMenuCreations++;
            return CreateStartMenu();
        }

        public Task CreateDesktopShortcutAsync() => CreateDesktop();

        public Task RemoveOwnedShortcutsAsync() => Remove();

        public Task<ShortcutStatus> GetShortcutStatusAsync() => GetStatus();
    }

    private sealed class RecordingDispatcher
    {
        public bool IsDispatching { get; private set; }

        public void Dispatch(Action action)
        {
            Assert.False(IsDispatching);
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

    private sealed class BlockingRegistryStore : EmptyRegistryStore
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public override RegistryKeySnapshot? ReadKey(string keyPath)
        {
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
            return null;
        }
    }

    private class EmptyRegistryStore : IRegistryStore
    {
        public virtual RegistryKeySnapshot? ReadKey(string keyPath) => null;

        public void SetString(string keyPath, string? valueName, string value)
        {
        }

        public void DeleteValue(string keyPath, string valueName)
        {
        }

        public void DeleteKeyIfEmpty(string keyPath)
        {
        }
    }

    private sealed class NoOpNotifier : IAssociationChangeNotifier
    {
        public void NotifyChanged()
        {
        }
    }

    private sealed class NoOpProcessLauncher : IProcessLauncher
    {
        public void Start(System.Diagnostics.ProcessStartInfo startInfo)
        {
        }
    }
}
