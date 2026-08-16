using System.Windows.Input;
using MarkUpViewMini.Infrastructure.Windows;

namespace MarkUpViewMini.App.ViewModels;

public sealed class WindowsIntegrationSettingsViewModel : ObservableObject, IDisposable
{
    private const string CheckingStatus = "파일 형식 등록 상태를 확인하는 중입니다…";
    private const string CheckingShortcutStatus = "바로 가기 상태를 확인하는 중입니다…";

    private readonly IFileAssociationService service;
    private readonly IShortcutService shortcutService;
    private readonly string executablePath;
    private readonly Action<Action> dispatcher;
    private readonly DelegateCommand registerCommand;
    private readonly DelegateCommand unregisterCommand;
    private readonly DelegateCommand openDefaultAppsSettingsCommand;
    private readonly DelegateCommand createStartMenuShortcutCommand;
    private readonly DelegateCommand createDesktopShortcutCommand;
    private readonly DelegateCommand removeShortcutsCommand;
    private string statusText = CheckingStatus;
    private string shortcutStatusText = CheckingShortcutStatus;
    private string? errorMessage;
    private bool isRegistered;
    private bool hasStartMenuShortcut;
    private bool hasDesktopShortcut;
    private bool isBusy;
    private int activeOperation;
    private int disposed;
    private long lifetimeGeneration;

    public WindowsIntegrationSettingsViewModel(
        IFileAssociationService service,
        IShortcutService shortcutService,
        string executablePath,
        Action<Action> dispatcher)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.executablePath = executablePath;
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        registerCommand = new DelegateCommand(() => _ = RegisterAsync(), CanStartOperation);
        unregisterCommand = new DelegateCommand(() => _ = UnregisterAsync(), CanStartOperation);
        openDefaultAppsSettingsCommand = new DelegateCommand(
            OpenWindowsDefaultAppsSettings,
            CanStartOperation);
        createStartMenuShortcutCommand = new DelegateCommand(
            () => _ = CreateStartMenuShortcutAsync(),
            CanStartOperation);
        createDesktopShortcutCommand = new DelegateCommand(
            () => _ = CreateDesktopShortcutAsync(),
            CanStartOperation);
        removeShortcutsCommand = new DelegateCommand(
            () => _ = RemoveShortcutsAsync(),
            CanStartOperation);
    }

    public string GuidanceText { get; } =
        "등록하면 MarkUpViewMini가 앱 선택 목록에 추가됩니다. 실제 기본 앱은 Windows에서 사용자가 선택합니다.";

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetProperty(ref errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string ShortcutStatusText
    {
        get => shortcutStatusText;
        private set => SetProperty(ref shortcutStatusText, value);
    }

    public bool IsRegistered
    {
        get => isRegistered;
        private set => SetProperty(ref isRegistered, value);
    }

    public bool HasStartMenuShortcut
    {
        get => hasStartMenuShortcut;
        private set => SetProperty(ref hasStartMenuShortcut, value);
    }

    public bool HasDesktopShortcut
    {
        get => hasDesktopShortcut;
        private set => SetProperty(ref hasDesktopShortcut, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public ICommand RegisterCommand => registerCommand;

    public ICommand UnregisterCommand => unregisterCommand;

    public ICommand OpenDefaultAppsSettingsCommand => openDefaultAppsSettingsCommand;

    public ICommand CreateStartMenuShortcutCommand => createStartMenuShortcutCommand;

    public ICommand CreateDesktopShortcutCommand => createDesktopShortcutCommand;

    public ICommand RemoveShortcutsCommand => removeShortcutsCommand;

    public async Task RefreshAsync()
    {
        if (!TryBeginOperation(out var generation))
        {
            return;
        }

        try
        {
            var fileAssociationStatus = await service.GetStatusAsync().ConfigureAwait(false);
            var shortcutStatus = await shortcutService.GetShortcutStatusAsync().ConfigureAwait(false);
            Publish(generation, () =>
            {
                PublishFileAssociationStatus(fileAssociationStatus);
                PublishShortcutStatus(shortcutStatus);
                ErrorMessage = null;
            });
        }
        catch
        {
            Publish(generation, () => ErrorMessage = "Windows 통합 상태를 확인할 수 없습니다.");
        }
        finally
        {
            EndOperation(generation);
        }
    }

    public Task RegisterAsync() => RunOperationAsync(
        async () =>
        {
            await service.RegisterAsync(executablePath).ConfigureAwait(false);
            return await service.GetStatusAsync().ConfigureAwait(false);
        },
        "파일 형식을 등록할 수 없습니다.");

    public Task UnregisterAsync() => RunOperationAsync(
        async () =>
        {
            await service.UnregisterAsync().ConfigureAwait(false);
            return await service.GetStatusAsync().ConfigureAwait(false);
        },
        "파일 형식 등록을 해제할 수 없습니다.");

    public Task CreateStartMenuShortcutAsync() => RunShortcutOperationAsync(
        shortcutService.CreateStartMenuShortcutAsync,
        "시작 메뉴 바로 가기를 만들 수 없습니다.");

    public Task CreateDesktopShortcutAsync() => RunShortcutOperationAsync(
        shortcutService.CreateDesktopShortcutAsync,
        "바탕 화면 바로 가기를 만들 수 없습니다.");

    public Task RemoveShortcutsAsync() => RunShortcutOperationAsync(
        shortcutService.RemoveOwnedShortcutsAsync,
        "바로 가기를 제거할 수 없습니다.");

    public void OpenWindowsDefaultAppsSettings()
    {
        if (!TryBeginOperation(out var generation))
        {
            return;
        }

        try
        {
            service.OpenWindowsDefaultAppsSettings();
            Publish(generation, () => ErrorMessage = null);
        }
        catch
        {
            Publish(generation, () => ErrorMessage = "Windows 기본 앱 설정을 열 수 없습니다.");
        }
        finally
        {
            EndOperation(generation);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Interlocked.Increment(ref lifetimeGeneration);
        }
    }

    private async Task RunOperationAsync(
        Func<Task<FileAssociationStatus>> operation,
        string failureMessage)
    {
        if (!TryBeginOperation(out var generation))
        {
            return;
        }

        try
        {
            var status = await operation().ConfigureAwait(false);
            Publish(generation, () =>
            {
                PublishFileAssociationStatus(status);
                ErrorMessage = null;
            });
        }
        catch
        {
            Publish(generation, () => ErrorMessage = failureMessage);
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task RunShortcutOperationAsync(Func<Task> operation, string failureMessage)
    {
        if (!TryBeginOperation(out var generation))
        {
            return;
        }

        try
        {
            await operation().ConfigureAwait(false);
            var status = await shortcutService.GetShortcutStatusAsync().ConfigureAwait(false);
            Publish(generation, () =>
            {
                PublishShortcutStatus(status);
                ErrorMessage = null;
            });
        }
        catch
        {
            Publish(generation, () => ErrorMessage = failureMessage);
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private void PublishFileAssociationStatus(FileAssociationStatus status)
    {
        IsRegistered = status.IsRegistered;
        StatusText = status.IsRegistered
            ? "파일 형식이 등록되어 있습니다."
            : "파일 형식이 등록되어 있지 않습니다.";
    }

    private void PublishShortcutStatus(ShortcutStatus status)
    {
        HasStartMenuShortcut = status.HasStartMenuShortcut;
        HasDesktopShortcut = status.HasDesktopShortcut;
        ShortcutStatusText = status switch
        {
            { HasStartMenuShortcut: true, HasDesktopShortcut: true } =>
                "시작 메뉴와 바탕 화면 바로 가기가 있습니다.",
            { HasStartMenuShortcut: true } => "시작 메뉴 바로 가기가 있습니다.",
            { HasDesktopShortcut: true } => "바탕 화면 바로 가기가 있습니다.",
            _ => "바로 가기가 없습니다.",
        };
    }

    private bool TryBeginOperation(out long generation)
    {
        generation = Volatile.Read(ref lifetimeGeneration);
        if (Volatile.Read(ref disposed) != 0 ||
            Interlocked.CompareExchange(ref activeOperation, 1, 0) != 0)
        {
            return false;
        }

        Publish(generation, () =>
        {
            IsBusy = true;
            ErrorMessage = null;
            RaiseCanExecuteChanged();
        });
        return true;
    }

    private void EndOperation(long generation)
    {
        Interlocked.Exchange(ref activeOperation, 0);
        Publish(generation, () =>
        {
            IsBusy = false;
            RaiseCanExecuteChanged();
        });
    }

    private bool CanStartOperation() =>
        Volatile.Read(ref disposed) == 0 && Volatile.Read(ref activeOperation) == 0;

    private void RaiseCanExecuteChanged()
    {
        registerCommand.RaiseCanExecuteChanged();
        unregisterCommand.RaiseCanExecuteChanged();
        openDefaultAppsSettingsCommand.RaiseCanExecuteChanged();
        createStartMenuShortcutCommand.RaiseCanExecuteChanged();
        createDesktopShortcutCommand.RaiseCanExecuteChanged();
        removeShortcutsCommand.RaiseCanExecuteChanged();
    }

    private void Publish(long generation, Action action)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        try
        {
            dispatcher(() =>
            {
                if (IsCurrent(generation))
                {
                    action();
                }
            });
        }
        catch (InvalidOperationException) when (Volatile.Read(ref disposed) != 0)
        {
        }
        catch (TaskCanceledException) when (Volatile.Read(ref disposed) != 0)
        {
        }
    }

    private bool IsCurrent(long generation) =>
        Volatile.Read(ref disposed) == 0 &&
        Volatile.Read(ref lifetimeGeneration) == generation;

    private sealed class DelegateCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
