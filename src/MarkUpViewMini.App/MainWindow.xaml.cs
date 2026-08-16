using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MarkUpViewMini.App.About;
using MarkUpViewMini.App.Composition;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Folders;
using MarkUpViewMini.Infrastructure.Files;
using MarkUpViewMini.Infrastructure.Paths;
using MarkUpViewMini.Infrastructure.Recovery;
using MarkUpViewMini.Infrastructure.State;
using MarkUpViewMini.Infrastructure.Windows;
using Microsoft.Win32;

namespace MarkUpViewMini.App;

internal readonly record struct MainWindowShutdownOwnership(
    MainWindow Window,
    Guid WindowId,
    long LifetimeGeneration);

internal readonly record struct MainWindowTabShutdownOwnership(
    MainWindow Window,
    ShellShutdownOwnership ShellOwnership);

public partial class MainWindow : Window, ISessionWindow
{
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly DocumentFormatRegistry formatRegistry;
    private readonly WindowComposition composition;
    private readonly ShellViewModel shell;
    private readonly SettingsService settings;
    private readonly bool ownsSettings;
    private readonly SessionWindowStateController sessionState;
    private readonly IAboutDialogService aboutDialogService;
    private bool applyingSettings;
    private long latestSettingsGeneration;
    private bool closed;
    private bool abandoningStartup;
    private bool closeApproved;
    private bool closeResolutionInProgress;
    private long lifetimeGeneration = 1;

    internal MainWindow(
        DocumentFormatRegistry formatRegistry,
        IAppDataPaths appDataPaths,
        SettingsService? settings = null,
        Guid? sessionWindowId = null,
        RecoveryService? recovery = null,
        DocumentSaveArbiter? saveArbiter = null,
        WindowsIntegrationSettingsViewModel? windowsIntegration = null,
        IAboutDialogService? aboutDialogService = null)
    {
        InitializeComponent();
        this.formatRegistry = formatRegistry;
        ownsSettings = settings is null;
        this.settings = settings ?? new SettingsService(appDataPaths);
        this.aboutDialogService = aboutDialogService ?? new AboutDialogService(
            new AboutMetadataProvider(),
            new AboutLinkLauncher());
        composition = WindowComposition.Create(
            formatRegistry,
            ActivateDocumentSurfaceAsync,
            DocumentSurface.Deactivate,
            (line, _) => DocumentSurface.GoToLineAsync(line),
            (anchor, _) => DocumentSurface.GoToAnchorAsync(anchor),
            action => Dispatcher.Invoke(action),
            (tabId, revision, _) => DocumentSurface.SaveCompletedAsync(tabId, revision),
            this.settings,
            sessionWindowId,
            recovery,
            saveArbiter);
        shell = composition.Shell;
        var currentExecutablePath = Environment.ProcessPath ??
            Path.Combine(AppContext.BaseDirectory, "MarkUpViewMini.App.exe");
        WindowsIntegration = windowsIntegration ?? new WindowsIntegrationSettingsViewModel(
            new FileAssociationService(
                new CurrentUserRegistryStore(),
                new ShellProcessLauncher(),
                currentExecutablePath),
            new ShellLinkShortcutService(
                currentExecutablePath,
                currentExecutablePath,
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            currentExecutablePath,
            action =>
            {
                if (Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    Dispatcher.Invoke(action);
                }
            });
        WindowsIntegrationMenu.DataContext = WindowsIntegration;
        sessionState = new SessionWindowStateController(
            shell,
            File.Exists,
            CaptureWindowLayout,
            ApplyWindowLayout);
        ApplySettings(this.settings.Current);
        this.settings.Changed += Settings_Changed;
        DocumentSurface.Configure(
            appDataPaths,
            shell.WindowId,
            () => shell.Tabs.ToArray(),
            () => shell.ActiveTab);
        DocumentSurface.OutlineReceived += shell.HandleOutline;
        DocumentSurface.LinkOpenRequested += DocumentSurface_LinkOpenRequested;
        DocumentSurface.LinkContextMenuRequested += DocumentSurface_LinkContextMenuRequested;
        DocumentSurface.DocumentChanged += shell.HandleDocumentChanged;
        DocumentSurface.ModeChanged += shell.HandleModeChanged;
        DocumentSurface.UiHintsChanged += shell.HandleDocumentUiHintsChanged;
        DocumentSurface.CurrentResponseChanged += DocumentSurface_CurrentResponseChanged;
        composition.Sidebar.PropertyChanged += Sidebar_PropertyChanged;
        shell.PropertyChanged += Shell_PropertyChanged;
        shell.Tabs.CollectionChanged += Tabs_CollectionChanged;
        LocationChanged += WindowLayout_Changed;
        SizeChanged += WindowLayout_Changed;
        StateChanged += WindowLayout_Changed;
        DataContext = shell;
        _ = WindowsIntegration.RefreshAsync();
    }

    internal SidebarViewModel Sidebar => composition.Sidebar;

    public WindowsIntegrationSettingsViewModel WindowsIntegration { get; }

    internal async Task OpenCommandLineTargetsAsync(
        IReadOnlyList<string> arguments,
        string? baseDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RunUiActionAsync(async lifetimeToken =>
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken,
                cancellationToken);
            await shell.OpenCommandLineTargetsAsync(
                arguments,
                baseDirectory,
                operation.Token);
        });
    }

    async Task<int> ISessionWindow.RestoreAsync(
        SessionWindowV1 state,
        CancellationToken cancellationToken) =>
        await sessionState.RestoreAsync(state, cancellationToken);

    void ISessionWindow.Commit()
    {
        if (Application.Current is App app)
        {
            app.CommitStartupWindow(this);
        }
        else
        {
            Show();
        }
    }

    void ISessionWindow.Abandon()
    {
        abandoningStartup = true;
        if (Application.Current is App app)
        {
            app.AbandonStartupWindow(this);
        }
        else
        {
            Close();
        }
    }

    Task ISessionWindow.OpenCommandLineTargetsAsync(
        IReadOnlyList<string> arguments,
        string? baseDirectory,
        CancellationToken cancellationToken) =>
        OpenCommandLineTargetsAsync(arguments, baseDirectory, cancellationToken);

    Task ISessionWindow.RestoreRecoveredAsync(
        IReadOnlyList<DocumentBuffer> buffers,
        CancellationToken cancellationToken) =>
        shell.RestoreRecoveredBuffersAsync(buffers, cancellationToken);

    internal SessionWindowV1 CaptureSession() => sessionState.Capture(shell.WindowId);

    internal void ShowSessionSummary(int skipped) =>
        StatusText.SetCurrentValue(
            TextBlock.TextProperty,
            $"{skipped} session item(s) could not be restored.");

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!abandoningStartup && !closeApproved && shell.Tabs.Any(tab => tab.IsDirty))
        {
            e.Cancel = true;
            if (!closeResolutionInProgress)
            {
                closeResolutionInProgress = true;
                _ = ResolveDirtyCloseAndCloseAsync();
            }

            base.OnClosing(e);
            return;
        }

        if (!abandoningStartup && !e.Cancel && Application.Current is App app)
        {
            app.FlushSessionBeforeWindowDisposal(this);
        }

        base.OnClosing(e);
    }

    internal bool TryCreateApplicationShutdownRequest(out DirtyCloseRequest? request)
    {
        if (closeApproved || abandoningStartup || closed)
        {
            request = null;
            return true;
        }

        if (closeResolutionInProgress)
        {
            request = null;
            return false;
        }

        request = new DirtyCloseRequest(
            shell,
            shell.Tabs.ToArray(),
            tab => NativeDirtyCloseDialog.Show(this, tab));
        return true;
    }

    internal MainWindowShutdownOwnership CaptureShutdownOwnership() =>
        new(this, shell.WindowId, Volatile.Read(ref lifetimeGeneration));

    internal bool IsCurrentShutdownOwnership(MainWindowShutdownOwnership ownership) =>
        ReferenceEquals(ownership.Window, this) &&
        ownership.WindowId == shell.WindowId &&
        ownership.LifetimeGeneration == Volatile.Read(ref lifetimeGeneration) &&
        !closed;

    internal MainWindowTabShutdownOwnership CaptureShutdownTabOwnership() =>
        new(this, shell.CaptureShutdownOwnership());

    internal bool IsCurrentShutdownTabOwnership(MainWindowTabShutdownOwnership ownership) =>
        ReferenceEquals(ownership.Window, this) &&
        !closed &&
        shell.IsCurrentShutdownOwnership(ownership.ShellOwnership);

    internal void AbortApplicationShutdown()
    {
        if (closed || lifetimeCancellation.IsCancellationRequested ||
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(AbortApplicationShutdown);
            return;
        }

        if (!closed && !lifetimeCancellation.IsCancellationRequested)
        {
            shell.AbortApplicationShutdown();
        }
    }

    internal void ApproveApplicationShutdown() => closeApproved = true;

    private async Task ResolveDirtyCloseAndCloseAsync()
    {
        try
        {
            if (await shell.TryResolveDirtyTabsForCloseAsync(
                    shell.Tabs,
                    tab => NativeDirtyCloseDialog.Show(this, tab),
                    lifetimeCancellation.Token))
            {
                closeApproved = true;
                Close();
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            closeResolutionInProgress = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        Interlocked.Increment(ref lifetimeGeneration);
        Interlocked.Exchange(ref latestSettingsGeneration, long.MaxValue);
        settings.Changed -= Settings_Changed;
        lifetimeCancellation.Cancel();
        DocumentSurface.OutlineReceived -= shell.HandleOutline;
        DocumentSurface.LinkOpenRequested -= DocumentSurface_LinkOpenRequested;
        DocumentSurface.LinkContextMenuRequested -= DocumentSurface_LinkContextMenuRequested;
        DocumentSurface.DocumentChanged -= shell.HandleDocumentChanged;
        DocumentSurface.ModeChanged -= shell.HandleModeChanged;
        DocumentSurface.UiHintsChanged -= shell.HandleDocumentUiHintsChanged;
        DocumentSurface.CurrentResponseChanged -= DocumentSurface_CurrentResponseChanged;
        composition.Sidebar.PropertyChanged -= Sidebar_PropertyChanged;
        shell.PropertyChanged -= Shell_PropertyChanged;
        shell.Tabs.CollectionChanged -= Tabs_CollectionChanged;
        foreach (var tab in shell.Tabs)
        {
            tab.PropertyChanged -= SessionTab_PropertyChanged;
        }

        LocationChanged -= WindowLayout_Changed;
        SizeChanged -= WindowLayout_Changed;
        StateChanged -= WindowLayout_Changed;
        WindowsIntegration.Dispose();
        composition.Dispose();
        lifetimeCancellation.Dispose();
        DocumentSurface.Dispose();
        if (ownsSettings)
        {
            this.settings.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (Application.Current is App app)
        {
            app.RemoveClosedWindow(this);
        }

        base.OnClosed(e);
    }

    private void ApplySettings(SettingsV1 loaded)
    {
        Dispatcher.Invoke(() =>
        {
            applyingSettings = true;
            try
            {
                composition.Sidebar.ApplySettings(loaded);
                SidebarColumn.Width = new GridLength(loaded.SidebarWidth);
                shell.RecentDocuments.Clear();
                foreach (var entry in loaded.RecentDocuments)
                {
                    shell.RecentDocuments.Add(entry);
                }
            }
            finally
            {
                applyingSettings = false;
            }
        });
    }

    private Task ActivateDocumentSurfaceAsync(
        DocumentTabViewModel tab,
        CancellationToken cancellationToken)
    {
        var current = settings.Current;
        tab.ApplyEditorPreferences(current.EditorSplitRatio, current.FindOptions);
        return DocumentSurface.ActivateAsync(tab, cancellationToken);
    }

    private void Settings_Changed(object? sender, SettingsChangedEventArgs change)
    {
        while (true)
        {
            var observed = Volatile.Read(ref latestSettingsGeneration);
            if (change.Generation <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                ref latestSettingsGeneration,
                change.Generation,
                observed) == observed)
            {
                break;
            }
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!closed && change.Generation == Volatile.Read(ref latestSettingsGeneration))
            {
                ApplySettings(change.Snapshot);
                if (shell.ActiveTab is { } tab)
                {
                    tab.ApplyEditorPreferences(
                        change.Snapshot.EditorSplitRatio,
                        change.Snapshot.FindOptions);
                    _ = RunUiActionAsync(_ => DocumentSurface.SetEditorPreferencesAsync(tab.UiHints));
                }
            }
        });
    }

    private async void DocumentSurface_LinkOpenRequested(LinkOpenMessage message) =>
        await RunUiActionAsync(token => shell.HandleLinkOpenAsync(message, token));

    private void DocumentSurface_LinkContextMenuRequested(LinkContextMenuMessage message)
    {
        if (!shell.TryGetLinkContextMenuState(message, out var state) || state is null)
        {
            return;
        }

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        AddLinkMenuItem(menu, "기본 동작으로 열기", LinkOpenDisposition.Default, state.CanOpenDefault);
        AddLinkMenuItem(menu, "MarkUpViewMini에서 열기", LinkOpenDisposition.Internal, state.CanOpenInternal);
        AddLinkMenuItem(menu, "Windows 기본 앱에서 열기", LinkOpenDisposition.WindowsDefault, state.CanOpenWithWindows);
        AddLinkMenuItem(menu, "새 탭에서 열기", LinkOpenDisposition.NewTab, state.CanOpenNewTab);
        menu.Closed += (_, _) => DocumentSurface.ContextMenu = null;
        DocumentSurface.ContextMenu = menu;
        menu.IsOpen = true;

        void AddLinkMenuItem(
            ItemsControl owner,
            string header,
            LinkOpenDisposition disposition,
            bool enabled)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await RunUiActionAsync(token =>
                shell.HandleLinkContextMenuSelectionAsync(message, disposition, token));
            owner.Items.Add(item);
        }
    }

    private async void Sidebar_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifySessionChanged();
        if (e.PropertyName == nameof(SidebarViewModel.RootPath) &&
            !string.IsNullOrWhiteSpace(composition.Sidebar.RootPath))
        {
            await RefreshSidebarTreeAsync();
        }

        if (e.PropertyName is nameof(SidebarViewModel.RootMode)
            or nameof(SidebarViewModel.SearchMode)
            or nameof(SidebarViewModel.MatchCase)
            or nameof(SidebarViewModel.WholeWord)
            or nameof(SidebarViewModel.UseRegex))
        {
            if (applyingSettings)
            {
                return;
            }

            settings.UpdateSidebarPreferences(
                composition.Sidebar.RootMode,
                composition.Sidebar.SearchMode,
                new SearchOptionsV1(
                    composition.Sidebar.MatchCase,
                    composition.Sidebar.WholeWord,
                    composition.Sidebar.UseRegex));
        }
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e) =>
        settings.UpdateSidebarWidth(SidebarColumn.ActualWidth > 0
            ? SidebarColumn.ActualWidth
            : SidebarColumn.Width.Value);

    private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        CommandManager.InvalidateRequerySuggested();
        NotifySessionChanged();
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (DocumentTabViewModel tab in e.OldItems)
            {
                tab.PropertyChanged -= SessionTab_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (DocumentTabViewModel tab in e.NewItems)
            {
                tab.PropertyChanged += SessionTab_PropertyChanged;
            }
        }

        NotifySessionChanged();
    }

    private void SessionTab_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        NotifySessionChanged();

    private void WindowLayout_Changed(object? sender, EventArgs e) => NotifySessionChanged();

    private void NotifySessionChanged()
    {
        if (!closed && Application.Current is App app)
        {
            app.ScheduleSessionCapture();
        }
    }

    private SessionWindowLayoutV1 CaptureWindowLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new SessionWindowLayoutV1(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            WindowState == WindowState.Maximized);
    }

    private void ApplyWindowLayout(SessionWindowLayoutV1 layout)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = layout.Left;
        Top = layout.Top;
        Width = layout.Width;
        Height = layout.Height;
        WindowState = layout.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    private static void DocumentSurface_CurrentResponseChanged() =>
        CommandManager.InvalidateRequerySuggested();

    private void Save_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = shell is not null &&
            !closeResolutionInProgress &&
            WindowInputPolicy.CanExecuteSave(shell.ActiveTab);
        e.Handled = true;
    }

    private async void Save_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunSaveAsync(saveAs: false);

    private void SaveAs_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = shell is not null &&
            !closeResolutionInProgress &&
            WindowInputPolicy.CanExecuteSaveAs(shell.ActiveTab);
        e.Handled = true;
    }

    private async void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunSaveAsync(saveAs: true);

    private void EditorHistory_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = shell is not null && WindowInputPolicy.CanExecuteEditorHistory(
            shell.ActiveTab,
            DocumentSurface.CurrentResponse);
        e.Handled = true;
    }

    private async void Undo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (TryGetExactWebOwner(WindowInputPolicy.CanExecuteEditorHistory, out var tab))
        {
            await RunUiActionAsync(_ => DocumentSurface.UndoAsync(tab.Id, tab.Revision));
        }
    }

    private async void Redo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (TryGetExactWebOwner(WindowInputPolicy.CanExecuteEditorHistory, out var tab))
        {
            await RunUiActionAsync(_ => DocumentSurface.RedoAsync(tab.Id, tab.Revision));
        }
    }

    private void ModeToggle_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = shell is not null && WindowInputPolicy.CanExecuteModeToggle(
            shell.ActiveTab,
            DocumentSurface.CurrentResponse);
        e.Handled = true;
    }

    private async void ToggleMode_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!TryGetExactWebOwner(WindowInputPolicy.CanExecuteModeToggle, out var tab))
        {
            return;
        }

        var mode = tab.Mode == DocumentMode.Read ? DocumentMode.Edit : DocumentMode.Read;
        await RunUiActionAsync(_ => DocumentSurface.SetModeAsync(tab.Id, tab.Revision, mode));
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open document",
            Filter = CreateOpenDialogFilter(formatRegistry),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenInputAsync(dialog.FileName, baseDirectory: null, OpenGesture.Normal);
        }
    }

    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.CreateWindow().Show();
        }
    }

    private void VersionInformation_Click(object sender, RoutedEventArgs e) =>
        aboutDialogService.Show(AboutDialogKind.Version, this);

    private void ThirdPartyLicenses_Click(object sender, RoutedEventArgs e) =>
        aboutDialogService.Show(AboutDialogKind.ThirdPartyLicenses, this);

    private void ApplicationLicense_Click(object sender, RoutedEventArgs e) =>
        aboutDialogService.Show(AboutDialogKind.ApplicationLicense, this);

    private async void RecentDocument_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RecentDocumentEntry entry })
        {
            await RunUiActionAsync(token => shell.OpenRecentAsync(entry, token));
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ShutdownCoherently();
        }
        else
        {
            Close();
        }
    }

    private async void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabsList.SelectedItem is DocumentTabViewModel tab)
        {
            await RunUiActionAsync(token => shell.ActivateAsync(tab, token));
        }
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocumentTabViewModel tab })
        {
            try
            {
                _ = await shell.TryCloseTabAsync(
                    tab,
                    candidate => NativeDirtyCloseDialog.Show(this, candidate),
                    lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(shell.RetryAsync);

    private async void RetryWithEncoding_Click(object sender, RoutedEventArgs e)
    {
        var selected = shell.EncodingSelection.Selected;
        if (selected is not null)
        {
            await RunUiActionAsync(token => shell.RetryWithEncodingAsync(selected.Encoding, token));
        }
    }

    private void CloseErrorTab_Click(object sender, RoutedEventArgs e) => shell.CloseActiveTab();

    private void DismissNavigationError_Click(object sender, RoutedEventArgs e) =>
        shell.ClearNavigationError();

    private void DismissEditingError_Click(object sender, RoutedEventArgs e) =>
        shell.ClearEditingError();

    private async void ReloadExternal_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(token => shell.ResolveExternalChangeAsync(
            ExternalChangeDecision.ReloadExternal,
            token));

    private async void KeepMine_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(token => shell.ResolveExternalChangeAsync(
            ExternalChangeDecision.KeepMine,
            token));

    private async void CompareExternal_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(token => shell.ResolveExternalChangeAsync(
            ExternalChangeDecision.Compare,
            token));

    private async void FolderTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindNearestItem<FolderNode>(e.OriginalSource as DependencyObject) is not { IsDirectory: false } node ||
            !composition.Sidebar.CanActivateFolderNode(node))
        {
            return;
        }

        e.Handled = true;
        await ActivateFolderNodeAsync(node, Keyboard.Modifiers);
    }

    private async void FolderTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!WindowInputPolicy.IsItemActivationKey(e.Key) ||
            FolderTree.SelectedItem is not FolderNode { IsDirectory: false } node)
        {
            return;
        }

        if (!composition.Sidebar.CanActivateFolderNode(node))
        {
            return;
        }

        e.Handled = true;
        await ActivateFolderNodeAsync(node, Keyboard.Modifiers);
    }

    private async void OutlineList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindNearestItem<OutlineItemViewModel>(e.OriginalSource as DependencyObject) is not { } item)
        {
            return;
        }

        e.Handled = true;
        await RunUiActionAsync(token => shell.GoToOutlineAsync(item, token));
    }

    private async void OutlineList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!WindowInputPolicy.IsItemActivationKey(e.Key) ||
            OutlineList.SelectedItem is not OutlineItemViewModel item)
        {
            return;
        }

        e.Handled = true;
        await RunUiActionAsync(token => shell.GoToOutlineAsync(item, token));
    }

    private async void SearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchMatch match })
        {
            if (!composition.Sidebar.CanActivateSearchMatch(match))
            {
                return;
            }

            await RunUiActionAsync(token => shell.GoToSearchMatchAsync(
                match,
                WindowInputPolicy.GetLinkDisposition(Keyboard.Modifiers),
                token));
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private void CancelSearch_Click(object sender, RoutedEventArgs e) => composition.Sidebar.CancelSearch();

    private Task SearchAsync() => composition.Sidebar.CanSearch
        ? RunUiActionAsync(token => composition.Sidebar.SearchAsync(composition.Sidebar.SearchText, token))
        : Task.CompletedTask;

    private void Back_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = shell is not null && shell.CanGoBack;
        e.Handled = true;
    }

    private async void Back_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunUiActionAsync(shell.GoBackAsync);

    private void Forward_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = shell is not null && shell.CanGoForward;
        e.Handled = true;
    }

    private async void Forward_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunUiActionAsync(shell.GoForwardAsync);

    private void Find_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = shell is not null &&
            WindowInputPolicy.CanExecuteFind(shell.ActiveTab, DocumentSurface.CurrentResponse);
        e.Handled = true;
    }

    private async void OpenFind_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunOwnedFindActionAsync((tabId, revision) => DocumentSurface.OpenFindAsync(tabId, revision));

    private async void FindNext_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunOwnedFindActionAsync((tabId, revision) => DocumentSurface.FindNextAsync(tabId, revision));

    private async void FindPrevious_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunOwnedFindActionAsync((tabId, revision) => DocumentSurface.FindPreviousAsync(tabId, revision));

    private async void CloseFind_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await RunOwnedFindActionAsync((tabId, revision) => DocumentSurface.CloseFindAsync(tabId, revision));

    private Task RunOwnedFindActionAsync(Func<Guid, long, Task> action) =>
        TryGetExactWebOwner(WindowInputPolicy.CanExecuteFind, out var tab)
            ? RunUiActionAsync(_ => action(tab.Id, tab.Revision))
            : Task.CompletedTask;

    private bool TryGetExactWebOwner(
        Func<DocumentTabViewModel?, WebResponseContext?, bool> policy,
        out DocumentTabViewModel tab)
    {
        tab = shell.ActiveTab!;
        return tab is not null && policy(tab, DocumentSurface.CurrentResponse);
    }

    private async Task RunSaveAsync(bool saveAs)
    {
        var canExecute = saveAs
            ? WindowInputPolicy.CanExecuteSaveAs(shell.ActiveTab)
            : WindowInputPolicy.CanExecuteSave(shell.ActiveTab);
        if (!canExecute || shell.ActiveTab is not { } tab)
        {
            return;
        }

        SaveDecision decision;
        if (saveAs)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Markdown As",
                FileName = System.IO.Path.GetFileName(tab.Path),
                InitialDirectory = System.IO.Path.GetDirectoryName(tab.Path),
                Filter = CreateSaveDialogFilter(formatRegistry),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true || tab.Encoding is not { } currentEncoding)
            {
                return;
            }

            var encoding = NativeSaveEncodingDialog.Choose(this, currentEncoding);
            if (encoding is null)
            {
                return;
            }

            decision = new SaveDecision.SaveAs(dialog.FileName, encoding);
        }
        else
        {
            decision = shell.CreateCurrentSaveDecision(tab);
        }

        try
        {
            var result = await shell.SaveActiveAsync(decision, lifetimeCancellation.Token);
            if (result is SaveResult.Conflict)
            {
                shell.ShowEditingError("디스크의 파일이 변경되어 저장하지 않았습니다. 충돌을 먼저 해결하세요.");
            }
        }
        catch (EncoderFallbackException)
        {
            shell.ShowEditingError("현재 인코딩으로 저장할 수 없습니다. 다른 이름으로 저장을 사용하세요.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            shell.ShowEditingError("문서를 저장할 수 없습니다. 원본 파일은 변경되지 않았습니다.");
        }
    }

    private Task OpenInputAsync(string input, string? baseDirectory, OpenGesture gesture) =>
        RunUiActionAsync(token =>
            shell.OpenAsync(DocumentTargetParser.Parse(input, baseDirectory), gesture, token));

    private async Task RunUiActionAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(lifetimeCancellation.Token);
            StatusText.SetCurrentValue(TextBlock.TextProperty, shell.ActiveTab is null
                ? "Local Markdown reader"
                : shell.ActiveTab.Path);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (FormatException)
        {
            StatusText.SetCurrentValue(TextBlock.TextProperty, "The document target is invalid.");
        }
        catch (NotSupportedException)
        {
            StatusText.SetCurrentValue(TextBlock.TextProperty, "The document type is not registered for this action.");
        }
        catch
        {
            StatusText.SetCurrentValue(TextBlock.TextProperty, "The local document surface is unavailable; use its Retry action.");
        }
    }

    private async Task RefreshSidebarTreeAsync()
    {
        try
        {
            await composition.Sidebar.RefreshTreeAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.SetCurrentValue(TextBlock.TextProperty, "The navigation root could not be refreshed.");
        }
    }

    private Task ActivateFolderNodeAsync(FolderNode node, ModifierKeys modifiers)
    {
        var disposition = WindowInputPolicy.GetLinkDisposition(modifiers);
        return RunUiActionAsync(token => shell.OpenAsync(
            new DocumentTarget(node.FullPath, null, null),
            disposition == LinkOpenDisposition.NewTab ? OpenGesture.ExplicitNewTab : OpenGesture.Normal,
            token));
    }

    internal static T? FindNearestItem<T>(DependencyObject? source)
        where T : class
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is TreeViewItem or ListBoxItem &&
                current is FrameworkElement { DataContext: T item })
            {
                return item;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child) =>
        child is Visual
            ? VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    private static string CreateOpenDialogFilter(DocumentFormatRegistry registry)
    {
        var patterns = registry.GetExtensions(DocumentCapabilities.Read)
            .Select(extension => $"*{extension}")
            .ToArray();
        return patterns.Length == 0
            ? "All files (*.*)|*.*"
            : $"Registered documents ({string.Join(';', patterns)})|{string.Join(';', patterns)}";
    }

    private static string CreateSaveDialogFilter(DocumentFormatRegistry registry)
    {
        var patterns = registry.GetExtensions(DocumentCapabilities.Edit)
            .Select(extension => $"*{extension}")
            .ToArray();
        return patterns.Length == 0
            ? "All files (*.*)|*.*"
            : $"Editable documents ({string.Join(';', patterns)})|{string.Join(';', patterns)}";
    }
}
