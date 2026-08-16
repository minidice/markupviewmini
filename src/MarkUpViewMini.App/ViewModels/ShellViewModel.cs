using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text;
using MarkUpViewMini.App.Services;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Files;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.ViewModels;

internal enum DirtyCloseChoice
{
    Save,
    Discard,
    Cancel,
}

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly DocumentFormatRegistry formatRegistry;
    private readonly Func<string, Encoding?, CancellationToken, Task<LoadedDocument>> loadDocument;
    private readonly Func<DocumentTabViewModel, CancellationToken, Task> activateDocument;
    private readonly Action deactivateDocument;
    private readonly SidebarViewModel? sidebar;
    private readonly LinkRoutingService? linkRouting;
    private readonly Func<LinkRoute, ExternalOpenResult>? externalOpen;
    private readonly Func<int, CancellationToken, Task>? goToLine;
    private readonly Func<string, CancellationToken, Task>? goToAnchor;
    private readonly Func<DocumentBuffer, SaveDecision, CancellationToken, Task<SaveResult>>? saveDocument;
    private readonly Func<Guid, long, CancellationToken, Task>? saveCompleted;
    private readonly Action<Action> dispatcher;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<FileChangeNotice>>? watchExternalChanges;
    private readonly Action<string, DiskFileVersion>? recordSavedVersion;
    private readonly Func<string, IReadOnlyList<RecentDocumentEntry>>? recordSuccessfulOpen;
    private readonly Action<DocumentUiHints>? recordEditorPreferences;
    private readonly Action<DocumentBuffer>? scheduleRecovery;
    private readonly Func<Guid, CancellationToken, Task>? removeRecovery;
    private readonly ConcurrentDictionary<Guid, long> loadGenerations = [];
    private readonly ConcurrentDictionary<Guid, long> navigationGenerations = [];
    private readonly ConcurrentDictionary<Guid, long> saveGenerations = [];
    private readonly ConcurrentDictionary<Guid, DiskFileVersion> keepMineVersions = [];
    private readonly ConcurrentDictionary<Guid, PendingExternalNotice> pendingExternalNotices = [];
    private readonly ConcurrentDictionary<Guid, long> externalNoticeGenerations = [];
    private readonly Dictionary<Guid, CancellationTokenSource> tabOperations = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> externalWatchOperations = [];
    private DocumentTabViewModel? activeTab;
    private string? navigationErrorMessage;
    private string? editingErrorMessage;
    private bool editingErrorBlocksClose;
    private ExternalConflictContext? externalConflict;
    private bool disposed;
    private readonly Guid lifetimeId = Guid.NewGuid();
    private long externalNoticeSequence;
    private long recentDocumentsGeneration;

    public ShellViewModel(
        DocumentFormatRegistry formatRegistry,
        Func<string, Encoding?, CancellationToken, Task<LoadedDocument>> loadDocument,
        Func<DocumentTabViewModel, CancellationToken, Task> activateDocument,
        Action? deactivateDocument = null,
        SidebarViewModel? sidebar = null,
        LinkRoutingService? linkRouting = null,
        Func<LinkRoute, ExternalOpenResult>? externalOpen = null,
        Func<int, CancellationToken, Task>? goToLine = null,
        Func<string, CancellationToken, Task>? goToAnchor = null,
        Func<DocumentBuffer, SaveDecision, CancellationToken, Task<SaveResult>>? saveDocument = null,
        Func<Guid, long, CancellationToken, Task>? saveCompleted = null,
        Action<Action>? dispatcher = null,
        Func<string, CancellationToken, IAsyncEnumerable<FileChangeNotice>>? watchExternalChanges = null,
        Action<string, DiskFileVersion>? recordSavedVersion = null,
        Func<string, IReadOnlyList<RecentDocumentEntry>>? recordSuccessfulOpen = null,
        Action<DocumentUiHints>? recordEditorPreferences = null,
        Guid? windowId = null,
        Action<DocumentBuffer>? scheduleRecovery = null,
        Func<Guid, CancellationToken, Task>? removeRecovery = null)
    {
        this.formatRegistry = formatRegistry ?? throw new ArgumentNullException(nameof(formatRegistry));
        this.loadDocument = loadDocument ?? throw new ArgumentNullException(nameof(loadDocument));
        this.activateDocument = activateDocument ?? throw new ArgumentNullException(nameof(activateDocument));
        this.deactivateDocument = deactivateDocument ?? (() => { });
        this.sidebar = sidebar;
        this.linkRouting = linkRouting;
        this.externalOpen = externalOpen;
        this.goToLine = goToLine;
        this.goToAnchor = goToAnchor;
        this.saveDocument = saveDocument;
        this.saveCompleted = saveCompleted;
        this.dispatcher = dispatcher ?? (action => action());
        this.watchExternalChanges = watchExternalChanges;
        this.recordSavedVersion = recordSavedVersion;
        this.recordSuccessfulOpen = recordSuccessfulOpen;
        this.recordEditorPreferences = recordEditorPreferences;
        this.scheduleRecovery = scheduleRecovery;
        this.removeRecovery = removeRecovery;
        if (windowId == Guid.Empty)
        {
            throw new ArgumentException("A session window ID cannot be empty.", nameof(windowId));
        }

        WindowId = windowId ?? Guid.NewGuid();
        EncodingSelection = new EncodingSelectionViewModel();
        ConflictBar = new ConflictBarViewModel();
    }

    public Guid WindowId { get; }

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    public ObservableCollection<RecentDocumentEntry> RecentDocuments { get; } = [];

    public EncodingSelectionViewModel EncodingSelection { get; }

    public ConflictBarViewModel ConflictBar { get; }

    public DiskFileVersion? KeepMineObservedVersion =>
        ActiveTab is { } tab && keepMineVersions.TryGetValue(tab.Id, out var version)
            ? version
            : null;

    internal int PendingExternalCount => pendingExternalNotices.Count;

    public SidebarViewModel? Sidebar => sidebar;

    public DocumentTabViewModel? ActiveTab
    {
        get => activeTab;
        set
        {
            if (ReferenceEquals(activeTab, value))
            {
                return;
            }

            if (activeTab is not null)
            {
                ClearOutline();
                activeTab.PropertyChanged -= ActiveTab_PropertyChanged;
            }

            ClearExternalConflict();

            if (!SetProperty(ref activeTab, value))
            {
                return;
            }

            if (activeTab is not null)
            {
                activeTab.PropertyChanged += ActiveTab_PropertyChanged;
            }

            OnPropertyChanged(nameof(KeepMineObservedVersion));
            NotifyActiveErrorChanged();
            NotifyHistoryChanged();
        }
    }

    public bool HasActiveError => ActiveTab?.Error is not null;

    public string? ActiveErrorMessage => ActiveTab?.Error?.Message;

    public bool CanRetryActiveError => ActiveTab?.Error?.CanRetry == true;

    public bool CanChooseEncodingForActiveError => ActiveTab?.Error?.CanChooseEncoding == true;

    public bool CanCloseActiveError => ActiveTab?.Error?.CanClose == true;

    public bool CanGoBack => ActiveTab?.NavigationHistory.CanMoveBack == true;

    public bool CanGoForward => ActiveTab?.NavigationHistory.CanMoveForward == true;

    public bool HasNavigationError => !string.IsNullOrWhiteSpace(NavigationErrorMessage);

    public bool HasEditingError => !string.IsNullOrWhiteSpace(EditingErrorMessage);

    public string? EditingErrorMessage
    {
        get => editingErrorMessage;
        private set
        {
            if (SetProperty(ref editingErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasEditingError));
            }
        }
    }

    public string? NavigationErrorMessage
    {
        get => navigationErrorMessage;
        private set
        {
            if (SetProperty(ref navigationErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasNavigationError));
            }
        }
    }

    public Task OpenAsync(
        DocumentTarget target,
        OpenGesture gesture,
        CancellationToken cancellationToken) =>
        NavigateAsync(target, gesture, recordHistory: true, cancellationToken);

    internal async Task OpenCommandLineTargetsAsync(
        IReadOnlyList<string> arguments,
        string? baseDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (var index = 0; index < arguments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gesture = Tabs.Count == 0 && index == 0
                ? OpenGesture.Normal
                : OpenGesture.ExplicitNewTab;
            await OpenAsync(
                DocumentTargetParser.Parse(arguments[index], baseDirectory),
                gesture,
                cancellationToken);
        }
    }

    internal async Task OpenActivationPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await OpenAsync(
                new DocumentTarget(path, null, null),
                OpenGesture.ExplicitNewTab,
                cancellationToken);
        }
    }

    internal async Task<DocumentTabViewModel?> RestoreSessionTabAsync(
        SessionTabV1 state,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        _ = formatRegistry.Resolve(state.Path);
        if (Tabs.Any(tab => tab.Id == state.TabId))
        {
            throw new InvalidOperationException("The session contains a duplicate tab ID.");
        }

        var target = new DocumentTarget(state.Path, null, null);
        var hadActiveSurface = ActiveTab is { Error: null, Revision: > 0 };
        var tab = new DocumentTabViewModel(target, state.TabId);
        Tabs.Add(tab);
        var navigationGeneration = BeginNavigation(tab);
        ActiveTab = tab;
        if (hadActiveSurface)
        {
            deactivateDocument();
        }

        if (await LoadAndActivateAsync(tab, selectedEncoding: null, cancellationToken) != NavigationLoadResult.Succeeded ||
            !IsCurrentNavigation(tab, navigationGeneration))
        {
            CloseTab(tab);
            return null;
        }

        tab.SetMode(state.Mode);
        tab.ApplyUiHints(tab.UiHints with
        {
            SelectionAnchor = state.Hints.SelectionAnchor,
            SelectionHead = state.Hints.SelectionHead,
            ScrollTop = state.Hints.ScrollTop,
            SplitRatio = state.Hints.SplitRatio,
        });
        tab.NavigationHistory.Restore(new NavigationHistorySnapshot(
            state.History.Select(entry => new NavigationEntry(
                entry.Path,
                entry.Line,
                entry.Anchor,
                entry.Mode,
                entry.ScrollOffset)).ToArray(),
            state.HistoryIndex));
        CompleteSuccessfulNavigation(tab, recordHistory: false);
        NotifyHistoryChanged();
        return tab;
    }

    internal async Task RestoreRecoveredBuffersAsync(
        IReadOnlyList<DocumentBuffer> buffers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        foreach (var buffer in buffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = formatRegistry.Resolve(buffer.Path);
            if (Tabs.FirstOrDefault(tab => tab.Id == buffer.TabId) is { } existing)
            {
                var snapshot = existing.CaptureNavigationSnapshot();
                var previousActiveTab = ActiveTab;
                try
                {
                    existing.ApplyRecovered(buffer, provider);
                    if (!existing.CanEdit)
                    {
                        ShowEditingError("읽기 전용 형식의 복구 내용을 보존했습니다. 다른 이름으로 저장을 사용하세요.");
                    }
                    scheduleRecovery?.Invoke(existing.Buffer!);
                    ActiveTab = existing;
                    await activateDocument(existing, cancellationToken);
                }
                catch (Exception activationFailure)
                {
                    await RollBackRecoveredActivationAsync(
                        activationFailure,
                        () => existing.RestoreNavigationSnapshot(snapshot, () => true),
                        previousActiveTab);
                    throw;
                }

                continue;
            }

            var previousActive = ActiveTab;
            var tab = new DocumentTabViewModel(
                new DocumentTarget(buffer.Path, null, null),
                buffer.TabId);
            tab.ApplyRecovered(buffer, provider);
            if (!tab.CanEdit)
            {
                ShowEditingError("읽기 전용 형식의 복구 내용을 보존했습니다. 다른 이름으로 저장을 사용하세요.");
            }
            scheduleRecovery?.Invoke(tab.Buffer!);
            Tabs.Add(tab);
            ActiveTab = tab;
            try
            {
                await activateDocument(tab, cancellationToken);
            }
            catch (Exception activationFailure)
            {
                await RollBackRecoveredActivationAsync(
                    activationFailure,
                    () => RemoveTab(tab, deactivateSurface: false),
                    previousActive);
                throw;
            }
        }
    }

    private async Task RollBackRecoveredActivationAsync(
        Exception activationFailure,
        Action restoreModel,
        DocumentTabViewModel? previousActive)
    {
        var rollbackFailures = new List<Exception>();
        ClearOutline();
        try
        {
            deactivateDocument();
        }
        catch (Exception failure)
        {
            rollbackFailures.Add(failure);
        }

        try
        {
            restoreModel();
            ActiveTab = previousActive;
        }
        catch (Exception failure)
        {
            rollbackFailures.Add(failure);
        }

        if (rollbackFailures.Count == 0 &&
            previousActive is { Error: null, Revision: > 0 } &&
            Tabs.Contains(previousActive))
        {
            try
            {
                await activateDocument(previousActive, CancellationToken.None);
            }
            catch (Exception failure)
            {
                rollbackFailures.Add(failure);
            }
        }

        if (rollbackFailures.Count > 0)
        {
            var rollbackFailure = rollbackFailures.Count == 1
                ? rollbackFailures[0]
                : new AggregateException(rollbackFailures);
            throw new RecoverySurfaceRollbackException(activationFailure, rollbackFailure);
        }
    }

    public Task OpenRecentAsync(
        RecentDocumentEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return OpenAsync(
            new DocumentTarget(entry.Path, null, null),
            OpenGesture.Normal,
            cancellationToken);
    }

    public async Task<SaveResult> SaveActiveAsync(
        SaveDecision decision,
        CancellationToken cancellationToken) =>
        await SaveTabAsync(RequireCurrentDocument(), decision, cancellationToken);

    internal async Task<SaveResult> SaveTabAsync(
        DocumentTabViewModel tab,
        SaveDecision decision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(decision);
        if (!Tabs.Contains(tab) || tab.Error is not null || tab.Revision <= 0)
        {
            throw new InvalidOperationException("The document tab is not a current loaded owner.");
        }

        ClearEditingError();
        IDocumentFormatProvider? saveAsProvider = null;
        if (decision is SaveDecision.SaveAs saveAs)
        {
            saveAsProvider = formatRegistry.Resolve(saveAs.TargetPath);
            if (!saveAsProvider.Descriptor.Capabilities.HasFlag(DocumentCapabilities.Edit))
            {
                throw new NotSupportedException("The selected document format is not editable.");
            }
        }
        else if (!tab.CanEdit)
        {
            throw new NotSupportedException("The source document format is not editable.");
        }

        var save = saveDocument ??
            throw new InvalidOperationException("Document saving is not configured.");
        var buffer = tab.Buffer ?? throw new InvalidOperationException("The document is not loaded.");
        var path = tab.Path;
        var loadGeneration = loadGenerations.GetValueOrDefault(tab.Id);
        var navigationGeneration = navigationGenerations.GetValueOrDefault(tab.Id);
        var saveGeneration = saveGenerations.GetValueOrDefault(tab.Id) + 1;
        saveGenerations[tab.Id] = saveGeneration;
        var result = await save(buffer, decision, cancellationToken);
        if (result is not SaveResult.Saved saved)
        {
            return result;
        }

        SaveCompletionOwner? owner = null;
        await DispatchAsync(() =>
        {
            if (!IsCurrentSave(
                    tab,
                    buffer,
                    path,
                    loadGeneration,
                    navigationGeneration,
                    saveGeneration))
            {
                return;
            }

            tab.CompleteSave(saved, decision, saveAsProvider);
            var savedPath = decision is SaveDecision.SaveAs saveAs
                ? Path.GetFullPath(saveAs.TargetPath)
                : path;
            owner = new SaveCompletionOwner(
                lifetimeId,
                tab,
                tab.Id,
                buffer,
                savedPath,
                saved.SavedRevision,
                tab.Revision,
                saved.Version,
                tab.FormatProvider,
                loadGeneration,
                navigationGeneration,
                saveGeneration,
                GetExternalNoticeGeneration(tab.Id),
                decision is SaveDecision.SaveAs);
            recordSavedVersion?.Invoke(savedPath, saved.Version);

            if (!IsCurrentSaveCompletion(
                    owner,
                    CancellationToken.None,
                    requireExternalGeneration: true))
            {
                return;
            }

            ClearPendingExternal(owner.TabId);
            owner = owner with { ExternalNoticeGeneration = null };
            ClearKeepMineVersion(owner.Tab);
            if (ReferenceEquals(ActiveTab, owner.Tab))
            {
                ClearExternalConflict();
            }

            if (owner.IsSaveAs)
            {
                StartExternalWatch(owner.Tab, owner.SavedPath, owner.LoadGeneration);
                if (!IsCurrentSaveCompletion(
                        owner,
                        CancellationToken.None,
                        requireExternalGeneration: false))
                {
                    return;
                }

                if (ReferenceEquals(ActiveTab, owner.Tab))
                {
                    FollowCurrentDocumentIfRequired(owner.SavedPath);
                }

                if (!IsCurrentSaveCompletion(
                        owner,
                        CancellationToken.None,
                        requireExternalGeneration: false))
                {
                    return;
                }

                RecordSuccessfulOpen(owner.SavedPath);
            }
        }).ConfigureAwait(false);
        if (owner is null)
        {
            return result;
        }

        var recoverySynchronized = await SynchronizeRecoveryAfterSaveAsync(
                owner.Tab,
                CancellationToken.None)
            .ConfigureAwait(false);

        Task? surfaceNotification = null;
        await DispatchAsync(() =>
        {
            if (!IsCurrentSaveCompletion(owner, cancellationToken, requireExternalGeneration: false))
            {
                return;
            }

            if (!recoverySynchronized)
            {
                ShowEditingError("문서는 저장했지만 복구본을 정리할 수 없습니다.");
            }

            if (
                !ReferenceEquals(ActiveTab, owner.Tab) ||
                owner.Tab.Revision != owner.SavedRevision ||
                saveCompleted is not { } notify)
            {
                return;
            }

            surfaceNotification = NotifySaveCompletedAsync(owner, notify, cancellationToken);
        }).ConfigureAwait(false);
        if (surfaceNotification is not null)
        {
            await surfaceNotification.ConfigureAwait(false);
        }

        return result;
    }

    internal SaveDecision CreateCurrentSaveDecision(DocumentTabViewModel tab) =>
        keepMineVersions.TryGetValue(tab.Id, out var observed)
            ? new SaveDecision.UseMyVersion(observed)
            : new SaveDecision.Normal();

    internal async Task<bool> TryResolveDirtyTabsForCloseAsync(
        IEnumerable<DocumentTabViewModel> tabs,
        Func<DocumentTabViewModel, DirtyCloseChoice> choose,
        CancellationToken cancellationToken) =>
        await DirtyCloseCoordinator.TryResolveAsync(
            [new DirtyCloseRequest(this, tabs, choose)],
            cancellationToken);

    internal DirtyClosePlan? TryCreateDirtyClosePlan(
        IEnumerable<DocumentTabViewModel> tabs,
        Func<DocumentTabViewModel, DirtyCloseChoice> choose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(choose);
        ObjectDisposedException.ThrowIf(disposed, this);
        ClearEditingError();

        var selectedTabs = tabs.Distinct().ToArray();
        if (selectedTabs.Any(tab => !Tabs.Contains(tab)))
        {
            ShowShutdownOwnershipChanged();
            return null;
        }

        var snapshots = selectedTabs
            .Select(CaptureDirtyCloseTabSnapshot)
            .ToArray();
        var entries = new List<DirtyClosePlanEntry>();
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.IsDirty)
            {
                continue;
            }

            var choice = choose(snapshot.Tab);
            if (choice == DirtyCloseChoice.Cancel)
            {
                return null;
            }

            entries.Add(new DirtyClosePlanEntry(snapshot, choice));
        }

        var requiresExactTabSet = selectedTabs.Length == Tabs.Count &&
            selectedTabs.SequenceEqual(Tabs);
        return new DirtyClosePlan(this, snapshots, entries, requiresExactTabSet);
    }

    internal bool ValidateDirtyClosePlan(DirtyClosePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(plan.Shell, this) ||
            plan.RequiresExactTabSet &&
            (Tabs.Count != plan.Tabs.Count || !Tabs.SequenceEqual(plan.Tabs.Select(snapshot => snapshot.Tab))) ||
            plan.Tabs.Any(snapshot => !IsCurrentDirtyCloseSnapshot(plan, snapshot)))
        {
            ShowShutdownOwnershipChanged();
            return false;
        }

        return true;
    }

    internal async Task<bool> ExecuteDirtyCloseSaveAsync(
        DirtyClosePlan plan,
        DirtyClosePlanEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(entry);
        if (!ReferenceEquals(plan.Shell, this) ||
            entry.Choice != DirtyCloseChoice.Save ||
            entry.SaveCompleted ||
            !ValidateDirtyClosePlan(plan))
        {
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SaveTabAsync(
                entry.Snapshot.Tab,
                CreateCurrentSaveDecision(entry.Snapshot.Tab),
                cancellationToken);
            if (result is SaveResult.Conflict)
            {
                ShowEditingError("디스크의 파일이 변경되어 저장하지 않았습니다. 충돌을 먼저 해결하세요.");
                return false;
            }

            if (editingErrorBlocksClose)
            {
                return false;
            }

            if (entry.Snapshot.Tab.IsDirty)
            {
                ShowEditingError("저장 중 새 편집이 발생하여 문서를 닫지 않았습니다.");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EncoderFallbackException)
        {
            ShowEditingError("현재 인코딩으로 저장할 수 없습니다. 다른 이름으로 저장을 사용하세요.");
            return false;
        }
        catch (NotSupportedException)
        {
            ShowEditingError("읽기 전용 형식은 원래 파일에 저장할 수 없습니다. 다른 이름으로 저장을 사용하세요.");
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowEditingError("문서를 안전하게 저장하거나 복구본을 정리할 수 없어 닫지 않았습니다.");
            return false;
        }
    }

    internal async Task<bool> ExecuteDirtyCloseDiscardAsync(
        DirtyClosePlanEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Choice != DirtyCloseChoice.Discard)
        {
            return false;
        }

        try
        {
            if (removeRecovery is not null)
            {
                await removeRecovery(entry.Snapshot.TabId, cancellationToken);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowEditingError("문서를 안전하게 저장하거나 복구본을 정리할 수 없어 닫지 않았습니다.");
            return false;
        }
    }

    internal void RescheduleDirtyRecovery(DirtyClosePlan plan)
    {
        if (!ReferenceEquals(plan.Shell, this) || scheduleRecovery is null)
        {
            return;
        }

        foreach (var snapshot in plan.Tabs)
        {
            var tab = snapshot.Tab;
            if (Tabs.Contains(tab) && tab.IsDirty && tab.Buffer is { } buffer)
            {
                scheduleRecovery(buffer);
            }
        }
    }

    internal void ShowShutdownOwnershipChanged() =>
        ShowEditingError("닫기 준비 중 창이나 문서가 변경되었습니다. 다시 시도하세요.");

    internal void AbortApplicationShutdown()
    {
        ShowShutdownOwnershipChanged();
        if (scheduleRecovery is null)
        {
            return;
        }

        foreach (var tab in Tabs)
        {
            if (tab.IsDirty && tab.Buffer is { } buffer)
            {
                scheduleRecovery(buffer);
            }
        }
    }

    internal ShellShutdownOwnership CaptureShutdownOwnership() =>
        new(Tabs.Select(CaptureDirtyCloseTabSnapshot).ToArray());

    internal bool IsCurrentShutdownOwnership(ShellShutdownOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        return Tabs.Count == ownership.Tabs.Count &&
            Tabs.SequenceEqual(ownership.Tabs.Select(snapshot => snapshot.Tab)) &&
            ownership.Tabs.All(snapshot => IsCurrentDirtyCloseSnapshot(snapshot, saveCompleted: false));
    }

    private DirtyCloseTabSnapshot CaptureDirtyCloseTabSnapshot(DocumentTabViewModel tab) =>
        new(
            tab,
            tab.Id,
            tab.Buffer,
            tab.Revision,
            tab.IsDirty,
            loadGenerations.GetValueOrDefault(tab.Id),
            navigationGenerations.GetValueOrDefault(tab.Id),
            saveGenerations.GetValueOrDefault(tab.Id));

    private bool IsCurrentDirtyCloseSnapshot(
        DirtyClosePlan plan,
        DirtyCloseTabSnapshot snapshot)
    {
        var entry = plan.Entries.FirstOrDefault(candidate => ReferenceEquals(candidate.Snapshot, snapshot));
        var saveCompleted = entry?.SaveCompleted == true;
        return IsCurrentDirtyCloseSnapshot(snapshot, saveCompleted);
    }

    private bool IsCurrentDirtyCloseSnapshot(
        DirtyCloseTabSnapshot snapshot,
        bool saveCompleted) =>
        Tabs.Contains(snapshot.Tab) &&
            snapshot.Tab.Id == snapshot.TabId &&
            ReferenceEquals(snapshot.Tab.Buffer, snapshot.Buffer) &&
            snapshot.Tab.Revision == snapshot.Revision &&
            snapshot.Tab.IsDirty == (saveCompleted ? false : snapshot.IsDirty) &&
            loadGenerations.GetValueOrDefault(snapshot.TabId) == snapshot.LoadGeneration &&
            navigationGenerations.GetValueOrDefault(snapshot.TabId) == snapshot.NavigationGeneration &&
            saveGenerations.GetValueOrDefault(snapshot.TabId) == snapshot.SaveGeneration + (saveCompleted ? 1 : 0);

    internal async Task<bool> TryCloseTabAsync(
        DocumentTabViewModel tab,
        Func<DocumentTabViewModel, DirtyCloseChoice> choose,
        CancellationToken cancellationToken)
    {
        if (!Tabs.Contains(tab))
        {
            return true;
        }

        if (!await TryResolveDirtyTabsForCloseAsync([tab], choose, cancellationToken))
        {
            return false;
        }

        RemoveTab(tab, deactivateSurface: true);
        return true;
    }

    internal void ShowEditingError(string message, bool blocksClose = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        editingErrorBlocksClose |= blocksClose;
        EditingErrorMessage = message;
    }

    internal void ClearEditingError()
    {
        editingErrorBlocksClose = false;
        EditingErrorMessage = null;
    }

    public async Task GoBackAsync(CancellationToken cancellationToken)
    {
        var tab = RequireActiveTab();
        if (!tab.NavigationHistory.TryMoveBack(out var entry))
        {
            return;
        }

        var navigationGeneration = BeginNavigation(tab);
        NotifyHistoryChanged();
        if (!IsCurrentNavigation(tab, navigationGeneration))
        {
            return;
        }

        NavigationLoadResult result;
        try
        {
            result = await RestoreHistoryEntryAsync(tab, entry, navigationGeneration, cancellationToken);
        }
        catch (OperationCanceledException) when (IsCurrentNavigation(tab, navigationGeneration))
        {
            _ = tab.NavigationHistory.TryMoveForward(out _);
            NotifyHistoryChanged();
            throw;
        }

        if (result == NavigationLoadResult.Succeeded &&
            IsCurrentNavigation(tab, navigationGeneration) &&
            ReferenceEquals(ActiveTab, tab))
        {
            FollowCurrentDocumentIfRequired(tab.Path);
        }

        if (result == NavigationLoadResult.Failed && IsCurrentNavigation(tab, navigationGeneration))
        {
            _ = tab.NavigationHistory.TryMoveForward(out _);
            NotifyHistoryChanged();
        }
    }

    public async Task GoForwardAsync(CancellationToken cancellationToken)
    {
        var tab = RequireActiveTab();
        if (!tab.NavigationHistory.TryMoveForward(out var entry))
        {
            return;
        }

        var navigationGeneration = BeginNavigation(tab);
        NotifyHistoryChanged();
        if (!IsCurrentNavigation(tab, navigationGeneration))
        {
            return;
        }

        NavigationLoadResult result;
        try
        {
            result = await RestoreHistoryEntryAsync(tab, entry, navigationGeneration, cancellationToken);
        }
        catch (OperationCanceledException) when (IsCurrentNavigation(tab, navigationGeneration))
        {
            _ = tab.NavigationHistory.TryMoveBack(out _);
            NotifyHistoryChanged();
            throw;
        }

        if (result == NavigationLoadResult.Succeeded &&
            IsCurrentNavigation(tab, navigationGeneration) &&
            ReferenceEquals(ActiveTab, tab))
        {
            FollowCurrentDocumentIfRequired(tab.Path);
        }

        if (result == NavigationLoadResult.Failed && IsCurrentNavigation(tab, navigationGeneration))
        {
            _ = tab.NavigationHistory.TryMoveBack(out _);
            NotifyHistoryChanged();
        }
    }

    public Task GoToSearchMatchAsync(
        SearchMatch match,
        LinkOpenDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(match);
        var gesture = disposition switch
        {
            LinkOpenDisposition.Default => OpenGesture.Normal,
            LinkOpenDisposition.NewTab => OpenGesture.ExplicitNewTab,
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };

        return OpenAsync(
            new DocumentTarget(Path.GetFullPath(match.Path), match.LineNumber, null),
            gesture,
            cancellationToken);
    }

    public async Task GoToOutlineAsync(
        OutlineItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.SourceLine <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(item));
        }

        var tab = RequireCurrentDocument();
        var revision = tab.Revision;
        var navigationGeneration = BeginNavigation(tab);
        var send = goToLine ?? throw new InvalidOperationException("Line navigation is not configured.");
        await send(item.SourceLine, cancellationToken);
        if (!IsCurrentOwner(tab.Id, revision) || !IsCurrentNavigation(tab, navigationGeneration))
        {
            return;
        }

        tab.ApplyNavigationTarget(item.SourceLine, item.Anchor);
        tab.NavigationHistory.Push(CurrentEntry(tab));
        NotifyHistoryChanged();
    }

    public async Task OpenLinkAsync(
        string target,
        LinkOpenDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var owner = RequireCurrentDocument();
        var routing = linkRouting ?? throw new InvalidOperationException("Link routing is not configured.");
        var route = routing.Route(target, owner.Path, disposition);
        switch (route.Kind)
        {
            case LinkRouteKind.InternalCurrentTab:
                if (string.Equals(route.Target, owner.Path, StringComparison.OrdinalIgnoreCase))
                {
                    if (route.Line is null && string.IsNullOrWhiteSpace(route.Anchor))
                    {
                        await OpenAsync(
                            new DocumentTarget(route.Target, null, null),
                            OpenGesture.Normal,
                            cancellationToken);
                    }
                    else
                    {
                        await NavigateWithinCurrentDocumentAsync(owner, route.Line, route.Anchor, cancellationToken);
                    }
                }
                else
                {
                    await OpenAsync(
                        new DocumentTarget(route.Target, route.Line, route.Anchor),
                        OpenGesture.Normal,
                        cancellationToken);
                }

                return;
            case LinkRouteKind.InternalNewTab:
                await OpenAsync(
                    new DocumentTarget(route.Target, route.Line, route.Anchor),
                    OpenGesture.ExplicitNewTab,
                    cancellationToken);
                return;
            case LinkRouteKind.DefaultBrowser:
            case LinkRouteKind.WindowsAssociatedApp:
                var open = externalOpen ?? throw new InvalidOperationException("External opening is not configured.");
                var result = open(route);
                NavigationErrorMessage = result.Succeeded
                    ? null
                    : result.Error ?? "Windows could not open the link target.";
                return;
            default:
                throw new InvalidOperationException("The link route is not supported.");
        }
    }

    public LinkContextMenuState GetLinkContextMenuState(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var owner = RequireCurrentDocument();
        var routing = linkRouting ?? throw new InvalidOperationException("Link routing is not configured.");

        return new LinkContextMenuState(
            CanRoute(LinkOpenDisposition.Default),
            CanRoute(LinkOpenDisposition.Internal, LinkRouteKind.InternalCurrentTab),
            CanRoute(LinkOpenDisposition.WindowsDefault),
            CanRoute(LinkOpenDisposition.NewTab, LinkRouteKind.InternalNewTab));

        bool CanRoute(LinkOpenDisposition disposition, LinkRouteKind? requiredKind = null)
        {
            try
            {
                var route = routing.Route(target, owner.Path, disposition);
                return requiredKind is null || route.Kind == requiredKind;
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException)
            {
                return false;
            }
        }
    }

    public bool TryGetLinkContextMenuState(
        LinkContextMenuMessage message,
        out LinkContextMenuState? state)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsCurrentOwner(message.Owner))
        {
            state = null;
            return false;
        }

        state = GetLinkContextMenuState(message.Target);
        return true;
    }

    public Task HandleLinkContextMenuSelectionAsync(
        LinkContextMenuMessage message,
        LinkOpenDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return IsCurrentOwner(message.Owner)
            ? OpenLinkAsync(message.Target, disposition, cancellationToken)
            : Task.CompletedTask;
    }

    public void ClearNavigationError() => NavigationErrorMessage = null;

    public void HandleOutline(DocumentOutlineMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsCurrentOwner(message.Owner))
        {
            return;
        }

        sidebar?.SetOutline(message.Items.Select(item =>
            new OutlineItemViewModel(item.Level, item.Text, item.Anchor, item.SourceLine)));
    }

    public void HandleDocumentChanged(DocumentChangedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsCurrentOwner(message.Owner) || ActiveTab is not { Buffer: not null, CanEdit: true })
        {
            return;
        }

        try
        {
            ActiveTab.ApplyEdit(message.Edit);
            if (ActiveTab.Buffer is { } buffer)
            {
                scheduleRecovery?.Invoke(buffer);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
        }
    }

    public void HandleModeChanged(DocumentModeChangedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (IsCurrentOwner(message.Owner) && ActiveTab is { } tab)
        {
            if (message.Mode != DocumentMode.Edit || tab.CanEdit)
            {
                tab.SetMode(message.Mode);
            }
        }
    }

    public void HandleDocumentUiHintsChanged(DocumentUiHintsChangedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (IsCurrentOwner(message.Owner) && ActiveTab is { } tab)
        {
            tab.ApplyUiHints(message.Hints);
            recordEditorPreferences?.Invoke(message.Hints);
        }
    }

    public async Task HandleExternalChangeAsync(
        DocumentTabViewModel tab,
        FileChangeNotice notice,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(notice);
        var buffer = tab.Buffer;
        if (buffer is null)
        {
            return;
        }

        var path = tab.Path;
        var revision = tab.Revision;
        var loadGeneration = loadGenerations.GetValueOrDefault(tab.Id);
        var navigationGeneration = navigationGenerations.GetValueOrDefault(tab.Id);
        var saveGeneration = saveGenerations.GetValueOrDefault(tab.Id);
        var externalNoticeGeneration = Interlocked.Increment(ref externalNoticeSequence);
        externalNoticeGenerations.AddOrUpdate(
            tab.Id,
            externalNoticeGeneration,
            (_, current) => Math.Max(current, externalNoticeGeneration));
        Task activation = Task.CompletedTask;
        await DispatchAsync(() =>
        {
            if (!IsCurrentExternalPathOwner(
                    tab,
                    buffer,
                    path,
                    loadGeneration,
                    navigationGeneration,
                    cancellationToken))
            {
                return;
            }

            if (!externalNoticeGenerations.TryGetValue(tab.Id, out var currentNoticeGeneration) ||
                currentNoticeGeneration != externalNoticeGeneration)
            {
                return;
            }

            if (!ReferenceEquals(ActiveTab, tab))
            {
                pendingExternalNotices[tab.Id] = new PendingExternalNotice(
                    tab,
                    buffer,
                    path,
                    loadGeneration,
                    navigationGeneration,
                    externalNoticeGeneration,
                    notice);
                return;
            }

            if (!IsCurrentExternalOwner(
                    tab,
                    buffer,
                    path,
                    revision,
                    loadGeneration,
                    navigationGeneration,
                    saveGeneration,
                    cancellationToken) ||
                !string.Equals(notice.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            pendingExternalNotices.TryRemove(tab.Id, out _);

            if (notice.Kind != FileChangeKind.Changed || notice.Document is null)
            {
                externalConflict = null;
                ClearKeepMineVersion(tab);
                ConflictBar.ShowPathState(notice);
                return;
            }

            if (tab.IsDirty)
            {
                ClearKeepMineVersion(tab);
                externalConflict = new ExternalConflictContext(
                    tab,
                    buffer,
                    path,
                    revision,
                    loadGeneration,
                    navigationGeneration,
                    saveGeneration,
                    tab.Text,
                    buffer.BaselineVersion,
                    notice.Document);
                ConflictBar.ShowConflict();
                return;
            }

            tab.ApplyExternalLoaded(notice.Document);
            ClearKeepMineVersion(tab);
            ClearExternalConflict();
            activation = activateDocument(tab, cancellationToken);
        }).ConfigureAwait(false);
        await activation.ConfigureAwait(false);
    }

    public async Task ResolveExternalChangeAsync(
        ExternalChangeDecision decision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ExternalReloadOwner? reloadOwner = null;
        Guid? recoveryToRemove = null;
        await DispatchAsync(() =>
        {
            var conflict = externalConflict;
            if (conflict is null ||
                !IsCurrentExternalOwner(
                    conflict.Tab,
                    conflict.Buffer,
                    conflict.Path,
                    conflict.Revision,
                    conflict.LoadGeneration,
                    conflict.NavigationGeneration,
                    conflict.SaveGeneration,
                    cancellationToken))
            {
                ClearExternalConflict();
                return;
            }

            switch (decision)
            {
                case ExternalChangeDecision.ReloadExternal:
                    conflict.Tab.ApplyExternalLoaded(conflict.External);
                    recoveryToRemove = conflict.Tab.Id;
                    ClearKeepMineVersion(conflict.Tab);
                    ClearExternalConflict();
                    reloadOwner = new ExternalReloadOwner(
                        lifetimeId,
                        conflict.Tab,
                        conflict.Tab.Id,
                        conflict.Buffer,
                        conflict.Path,
                        conflict.Tab.Revision,
                        conflict.External.Version,
                        conflict.Tab.FormatProvider,
                        conflict.LoadGeneration,
                        conflict.NavigationGeneration,
                        conflict.SaveGeneration,
                        GetExternalNoticeGeneration(conflict.Tab.Id),
                        ActiveTab);
                    break;
                case ExternalChangeDecision.KeepMine:
                    SetKeepMineVersion(conflict.Tab, conflict.External.Version);
                    ClearExternalConflict();
                    break;
                case ExternalChangeDecision.Compare:
                    ConflictBar.ShowComparison(new DocumentComparisonViewModel(
                        new ReadOnlyDocumentSnapshot(
                            conflict.Path,
                            conflict.MineText,
                            conflict.MineBaselineVersion),
                        new ReadOnlyDocumentSnapshot(
                            conflict.Path,
                            conflict.External.Text,
                            conflict.External.Version)));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(decision));
            }
        }).ConfigureAwait(false);
        var recoveryRemoved = true;
        if (recoveryToRemove is { } tabId && removeRecovery is not null)
        {
            try
            {
                await removeRecovery(tabId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                OperationCanceledException)
            {
                recoveryRemoved = false;
            }
        }

        Task? activation = null;
        if (reloadOwner is not null)
        {
            await DispatchAsync(() =>
            {
                if (IsCurrentExternalReload(reloadOwner, cancellationToken))
                {
                    if (!recoveryRemoved)
                    {
                        ShowEditingError("외부 버전을 불러왔지만 이전 복구본을 정리할 수 없습니다.");
                    }

                    activation = ActivateReloadedOwnerAsync(reloadOwner, cancellationToken);
                }
            }).ConfigureAwait(false);
        }

        if (activation is not null)
        {
            await activation.ConfigureAwait(false);
        }
    }

    private async Task ActivateReloadedOwnerAsync(
        ExternalReloadOwner owner,
        CancellationToken cancellationToken)
    {
        try
        {
            await activateDocument(owner.Tab, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await DispatchAsync(() =>
            {
                if (IsCurrentExternalReload(owner, cancellationToken))
                {
                    ShowEditingError(
                        "외부 버전을 불러왔지만 편집 화면을 갱신할 수 없습니다.",
                        blocksClose: false);
                }
            }).ConfigureAwait(false);
        }
    }

    public async Task HandleLinkOpenAsync(
        LinkOpenMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsCurrentOwner(message.Owner))
        {
            return;
        }

        try
        {
            await OpenLinkAsync(message.Target, message.Disposition, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            NavigationErrorMessage = exception.Message;
        }
    }

    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        var tab = RequireActiveTab();
        if (tab.Error?.CanRetry != true)
        {
            throw new InvalidOperationException("The active document cannot be retried without choosing an encoding.");
        }

        ClearOutlineIfActive(tab);
        var navigationGeneration = BeginNavigation(tab);
        if (await LoadAndActivateAsync(tab, selectedEncoding: null, cancellationToken) == NavigationLoadResult.Succeeded &&
            IsCurrentNavigation(tab, navigationGeneration))
        {
            CompleteSuccessfulNavigation(tab, recordHistory: true);
        }
    }

    public async Task RetryWithEncodingAsync(Encoding selectedEncoding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedEncoding);
        var tab = RequireActiveTab();
        if (tab.Error?.CanChooseEncoding != true)
        {
            throw new InvalidOperationException("The active document is not awaiting an encoding selection.");
        }

        ClearOutlineIfActive(tab);
        var navigationGeneration = BeginNavigation(tab);
        if (await LoadAndActivateAsync(tab, selectedEncoding, cancellationToken) == NavigationLoadResult.Succeeded &&
            IsCurrentNavigation(tab, navigationGeneration))
        {
            CompleteSuccessfulNavigation(tab, recordHistory: true);
        }
    }

    public async Task ActivateAsync(DocumentTabViewModel tab, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab))
        {
            throw new ArgumentException("The tab does not belong to this window.", nameof(tab));
        }

        var changedTab = !ReferenceEquals(ActiveTab, tab);
        var hadActiveSurface = ActiveTab is { Error: null, Revision: > 0 };
        ActiveTab = tab;
        if (changedTab && hadActiveSurface)
        {
            deactivateDocument();
        }

        if (tab.Error is null && tab.Revision > 0)
        {
            await RevalidatePendingExternalWithNewOperationAsync(tab, cancellationToken);
            if (disposed || !ReferenceEquals(ActiveTab, tab))
            {
                return;
            }

            await ActivateWithNewOperationAsync(tab, cancellationToken);
        }
        else
        {
            deactivateDocument();
        }
    }

    public void CloseActiveTab()
    {
        if (ActiveTab is not null)
        {
            CloseTab(ActiveTab);
        }
    }

    public void CloseTab(DocumentTabViewModel tab)
    {
        RemoveTab(tab, deactivateSurface: true);
    }

    private void RemoveTab(DocumentTabViewModel tab, bool deactivateSurface)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(ActiveTab, tab);
        if (wasActive)
        {
            ClearOutline();
            if (deactivateSurface)
            {
                deactivateDocument();
            }
        }

        Tabs.RemoveAt(index);
        CancelTabOperation(tab.Id);
        CancelExternalWatch(tab.Id);
        loadGenerations.TryRemove(tab.Id, out _);
        navigationGenerations.TryRemove(tab.Id, out _);
        saveGenerations.TryRemove(tab.Id, out _);
        keepMineVersions.TryRemove(tab.Id, out _);
        ClearPendingExternal(tab.Id);
        if (wasActive)
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        else if (Tabs.Count == 0 && deactivateSurface)
        {
            deactivateDocument();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (activeTab is not null)
        {
            activeTab.PropertyChanged -= ActiveTab_PropertyChanged;
        }

        var cancellations = tabOperations.Values.ToArray();
        tabOperations.Clear();
        foreach (var cancellation in cancellations)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        var externalCancellations = externalWatchOperations.Values.ToArray();
        externalWatchOperations.Clear();
        foreach (var cancellation in externalCancellations)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        loadGenerations.Clear();
        navigationGenerations.Clear();
        saveGenerations.Clear();
        keepMineVersions.Clear();
        pendingExternalNotices.Clear();
        externalNoticeGenerations.Clear();
        ClearOutline();
        deactivateDocument();
        GC.SuppressFinalize(this);
    }

    private async Task NavigateAsync(
        DocumentTarget target,
        OpenGesture gesture,
        bool recordHistory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        _ = formatRegistry.Resolve(target.Path);
        var hadActiveSurface = ActiveTab is { Error: null, Revision: > 0 };
        var decision = TabOpenPolicy.Decide(
            ActiveTab is not null,
            ActiveTab?.IsDirty ?? false,
            gesture);

        DocumentTabViewModel tab;
        if (decision == TabOpenDecision.ReplaceActive)
        {
            tab = ActiveTab!;
            ClearOutlineIfActive(tab);
            tab.PrepareForLoad(target);
        }
        else
        {
            tab = new DocumentTabViewModel(target);
            Tabs.Add(tab);
        }

        var navigationGeneration = BeginNavigation(tab);
        ActiveTab = tab;
        if (hadActiveSurface)
        {
            deactivateDocument();
        }

        if (await LoadAndActivateAsync(tab, selectedEncoding: null, cancellationToken) == NavigationLoadResult.Succeeded &&
            IsCurrentNavigation(tab, navigationGeneration))
        {
            CompleteSuccessfulNavigation(tab, recordHistory);
        }
    }

    private async Task<NavigationLoadResult> RestoreHistoryEntryAsync(
        DocumentTabViewModel tab,
        NavigationEntry entry,
        long navigationGeneration,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(ActiveTab, tab))
        {
            return NavigationLoadResult.Superseded;
        }

        if (tab.Error is null &&
            tab.Revision > 0 &&
            string.Equals(tab.Path, entry.Path, StringComparison.OrdinalIgnoreCase) &&
            (entry.Line is > 0 || !string.IsNullOrWhiteSpace(entry.Anchor)))
        {
            try
            {
                await SendNavigationAsync(tab, entry.Line, entry.Anchor, cancellationToken);
                if (!IsCurrentNavigation(tab, navigationGeneration) || !ReferenceEquals(ActiveTab, tab))
                {
                    return NavigationLoadResult.Superseded;
                }

                tab.ApplyNavigationTarget(entry.Line, entry.Anchor);
                return NavigationLoadResult.Succeeded;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (ReferenceEquals(ActiveTab, tab) && IsCurrentNavigation(tab, navigationGeneration))
                {
                    return NavigationLoadResult.Failed;
                }

                return NavigationLoadResult.Superseded;
            }
        }

        var snapshot = tab.CaptureNavigationSnapshot();
        ClearOutlineIfActive(tab);
        tab.PrepareForLoad(new DocumentTarget(entry.Path, entry.Line, entry.Anchor));
        deactivateDocument();
        try
        {
            var result = await LoadAndActivateAsync(tab, selectedEncoding: null, cancellationToken);
            if (!IsCurrentNavigation(tab, navigationGeneration))
            {
                return NavigationLoadResult.Superseded;
            }

            if (result == NavigationLoadResult.Failed && !ReferenceEquals(ActiveTab, tab))
            {
                tab.RestoreNavigationSnapshot(
                    snapshot,
                    () => IsCurrentNavigation(tab, navigationGeneration));
            }

            return result;
        }
        catch (OperationCanceledException) when (IsCurrentNavigation(tab, navigationGeneration))
        {
            tab.RestoreNavigationSnapshot(
                snapshot,
                () => IsCurrentNavigation(tab, navigationGeneration));
            throw;
        }
    }

    private async Task NavigateWithinCurrentDocumentAsync(
        DocumentTabViewModel tab,
        int? line,
        string? anchor,
        CancellationToken cancellationToken)
    {
        var revision = tab.Revision;
        var navigationGeneration = BeginNavigation(tab);
        await SendNavigationAsync(tab, line, anchor, cancellationToken);
        if (!IsCurrentOwner(tab.Id, revision) || !IsCurrentNavigation(tab, navigationGeneration))
        {
            return;
        }

        tab.ApplyNavigationTarget(line, anchor);
        tab.NavigationHistory.Push(CurrentEntry(tab));
        NotifyHistoryChanged();
    }

    private Task SendNavigationAsync(
        DocumentTabViewModel tab,
        int? line,
        string? anchor,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(ActiveTab, tab) || tab.Error is not null || tab.Revision <= 0)
        {
            return Task.CompletedTask;
        }

        if (line is > 0)
        {
            var sendLine = goToLine ?? throw new InvalidOperationException("Line navigation is not configured.");
            return sendLine(line.Value, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(anchor))
        {
            var sendAnchor = goToAnchor ?? throw new InvalidOperationException("Anchor navigation is not configured.");
            return sendAnchor(anchor, cancellationToken);
        }

        return Task.CompletedTask;
    }

    private async Task<NavigationLoadResult> LoadAndActivateAsync(
        DocumentTabViewModel tab,
        Encoding? selectedEncoding,
        CancellationToken cancellationToken)
    {
        var path = tab.Path;
        var loadGeneration = loadGenerations.GetValueOrDefault(tab.Id) + 1;
        loadGenerations[tab.Id] = loadGeneration;
        ClearPendingExternal(tab.Id);
        CancelExternalWatch(tab.Id);
        CancelTabOperation(tab.Id);
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationToken = loadCancellation.Token;
        tabOperations[tab.Id] = loadCancellation;
        tab.Error = null;
        try
        {
            LoadedDocument document;
            try
            {
                document = await loadDocument(path, selectedEncoding, operationToken);
            }
            catch (Exception exception) when (
                exception is DecoderFallbackException or IOException or UnauthorizedAccessException)
            {
                if (IsCurrentLoad(tab, path, loadGeneration))
                {
                    tab.Error = DocumentOpenErrorViewModel.From(exception);
                    ClearOutlineIfActive(tab);
                    return NavigationLoadResult.Failed;
                }

                return NavigationLoadResult.Superseded;
            }

            operationToken.ThrowIfCancellationRequested();

            if (!IsCurrentLoad(tab, path, loadGeneration))
            {
                return NavigationLoadResult.Superseded;
            }

            tab.ApplyLoaded(document, formatRegistry.Resolve(path));
            if (ReferenceEquals(ActiveTab, tab))
            {
                await activateDocument(tab, operationToken);
            }

            StartExternalWatch(tab, path, loadGeneration);

            return NavigationLoadResult.Succeeded;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NavigationLoadResult.Superseded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            ReleaseTabOperation(tab.Id, loadCancellation);
        }
    }

    private async Task ActivateWithNewOperationAsync(
        DocumentTabViewModel tab,
        CancellationToken cancellationToken)
    {
        if (tabOperations.ContainsKey(tab.Id))
        {
            return;
        }

        CancelTabOperation(tab.Id);
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tabOperations[tab.Id] = operation;
        try
        {
            await activateDocument(tab, operation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            ReleaseTabOperation(tab.Id, operation);
        }
    }

    private void CompleteSuccessfulNavigation(DocumentTabViewModel tab, bool recordHistory)
    {
        if (recordHistory)
        {
            tab.NavigationHistory.Push(CurrentEntry(tab));
        }

        RecordSuccessfulOpen(tab.Path);

        if (ReferenceEquals(ActiveTab, tab))
        {
            if (sidebar is not null && string.IsNullOrWhiteSpace(sidebar.RootPath))
            {
                sidebar.RootPath = Path.GetDirectoryName(Path.GetFullPath(tab.Path));
            }

            FollowCurrentDocumentIfRequired(tab.Path);
            NotifyHistoryChanged();
        }
    }

    private void FollowCurrentDocumentIfRequired(string documentPath)
    {
        if (sidebar?.RootMode != RootFollowMode.FollowCurrentDocument)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        if (directory is null || IsWithinRoot(documentPath, sidebar.RootPath))
        {
            return;
        }

        sidebar.RootPath = directory;
    }

    private static bool IsWithinRoot(string documentPath, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(rootPath);
            var path = Path.GetFullPath(documentPath);
            var relative = Path.GetRelativePath(root, path);
            return !Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static NavigationEntry CurrentEntry(DocumentTabViewModel tab) =>
        new(tab.Path, tab.TargetLine, tab.TargetAnchor, tab.Mode, null);

    private long BeginNavigation(DocumentTabViewModel tab)
    {
        var generation = navigationGenerations.GetValueOrDefault(tab.Id) + 1;
        navigationGenerations[tab.Id] = generation;
        return generation;
    }

    private bool IsCurrentNavigation(DocumentTabViewModel tab, long generation) =>
        Tabs.Contains(tab) &&
        navigationGenerations.TryGetValue(tab.Id, out var currentGeneration) &&
        currentGeneration == generation;

    private bool IsCurrentSave(
        DocumentTabViewModel tab,
        DocumentBuffer buffer,
        string path,
        long loadGeneration,
        long navigationGeneration,
        long saveGeneration) =>
        !disposed &&
        Tabs.Contains(tab) &&
        ReferenceEquals(tab.Buffer, buffer) &&
        string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase) &&
        loadGenerations.GetValueOrDefault(tab.Id) == loadGeneration &&
        navigationGenerations.GetValueOrDefault(tab.Id) == navigationGeneration &&
        saveGenerations.GetValueOrDefault(tab.Id) == saveGeneration;

    private bool IsCurrentSaveCompletion(
        SaveCompletionOwner owner,
        CancellationToken cancellationToken,
        bool requireExternalGeneration) =>
        owner.ShellLifetimeId == lifetimeId &&
        !disposed &&
        !cancellationToken.IsCancellationRequested &&
        owner.Tab.Id == owner.TabId &&
        Tabs.Contains(owner.Tab) &&
        ReferenceEquals(owner.Tab.Buffer, owner.Buffer) &&
        string.Equals(owner.Tab.Path, owner.SavedPath, StringComparison.OrdinalIgnoreCase) &&
        owner.Tab.Revision == owner.CommittedRevision &&
        Equals(owner.Tab.DiskVersion, owner.SavedVersion) &&
        ReferenceEquals(owner.Tab.FormatProvider, owner.FormatProvider) &&
        loadGenerations.GetValueOrDefault(owner.TabId) == owner.LoadGeneration &&
        navigationGenerations.GetValueOrDefault(owner.TabId) == owner.NavigationGeneration &&
        saveGenerations.GetValueOrDefault(owner.TabId) == owner.SaveGeneration &&
        (requireExternalGeneration
            ? HasExternalNoticeGeneration(owner.TabId, owner.ExternalNoticeGeneration)
            : !externalNoticeGenerations.ContainsKey(owner.TabId));

    private bool IsCurrentExternalReload(
        ExternalReloadOwner owner,
        CancellationToken cancellationToken) =>
        owner.ShellLifetimeId == lifetimeId &&
        !disposed &&
        !cancellationToken.IsCancellationRequested &&
        owner.Tab.Id == owner.TabId &&
        Tabs.Contains(owner.Tab) &&
        ReferenceEquals(ActiveTab, owner.ActiveTab) &&
        ReferenceEquals(owner.ActiveTab, owner.Tab) &&
        ReferenceEquals(owner.Tab.Buffer, owner.Buffer) &&
        string.Equals(owner.Tab.Path, owner.Path, StringComparison.OrdinalIgnoreCase) &&
        owner.Tab.Revision == owner.Revision &&
        Equals(owner.Tab.DiskVersion, owner.Version) &&
        ReferenceEquals(owner.Tab.FormatProvider, owner.FormatProvider) &&
        loadGenerations.GetValueOrDefault(owner.TabId) == owner.LoadGeneration &&
        navigationGenerations.GetValueOrDefault(owner.TabId) == owner.NavigationGeneration &&
        saveGenerations.GetValueOrDefault(owner.TabId) == owner.SaveGeneration &&
        HasExternalNoticeGeneration(owner.TabId, owner.ExternalNoticeGeneration);

    private long? GetExternalNoticeGeneration(Guid tabId) =>
        externalNoticeGenerations.TryGetValue(tabId, out var generation)
            ? generation
            : null;

    private bool HasExternalNoticeGeneration(Guid tabId, long? expected) =>
        expected is { } generation
            ? externalNoticeGenerations.TryGetValue(tabId, out var current) && current == generation
            : !externalNoticeGenerations.ContainsKey(tabId);

    private bool IsCurrentExternalOwner(
        DocumentTabViewModel tab,
        DocumentBuffer buffer,
        string path,
        long revision,
        long loadGeneration,
        long navigationGeneration,
        long saveGeneration,
        CancellationToken cancellationToken) =>
        !disposed &&
        !cancellationToken.IsCancellationRequested &&
        Tabs.Contains(tab) &&
        ReferenceEquals(ActiveTab, tab) &&
        ReferenceEquals(tab.Buffer, buffer) &&
        string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase) &&
        tab.Revision == revision &&
        loadGenerations.GetValueOrDefault(tab.Id) == loadGeneration &&
        navigationGenerations.GetValueOrDefault(tab.Id) == navigationGeneration &&
        saveGenerations.GetValueOrDefault(tab.Id) == saveGeneration;

    private bool IsCurrentExternalPathOwner(
        DocumentTabViewModel tab,
        DocumentBuffer buffer,
        string path,
        long loadGeneration,
        long navigationGeneration,
        CancellationToken cancellationToken) =>
        !disposed &&
        !cancellationToken.IsCancellationRequested &&
        Tabs.Contains(tab) &&
        ReferenceEquals(tab.Buffer, buffer) &&
        string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase) &&
        loadGenerations.GetValueOrDefault(tab.Id) == loadGeneration &&
        navigationGenerations.GetValueOrDefault(tab.Id) == navigationGeneration;

    private bool IsCurrentOwner(WebMessageOwner owner) =>
        owner.WindowId == WindowId && IsCurrentOwner(owner.TabId, owner.DocumentRevision);

    private bool IsCurrentOwner(Guid tabId, long revision) =>
        ActiveTab is { Error: null } tab && tab.Id == tabId && tab.Revision == revision;

    private bool IsCurrentLoad(DocumentTabViewModel tab, string path, long loadGeneration) =>
        Tabs.Contains(tab) &&
        string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase) &&
        loadGenerations.TryGetValue(tab.Id, out var currentGeneration) &&
        currentGeneration == loadGeneration;

    private void CancelTabOperation(Guid tabId)
    {
        if (!tabOperations.Remove(tabId, out var cancellation))
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void StartExternalWatch(
        DocumentTabViewModel tab,
        string path,
        long loadGeneration)
    {
        var watch = watchExternalChanges;
        if (watch is null ||
            disposed ||
            !IsCurrentLoad(tab, path, loadGeneration))
        {
            return;
        }

        CancelExternalWatch(tab.Id);
        var cancellation = new CancellationTokenSource();
        externalWatchOperations[tab.Id] = cancellation;
        _ = ObserveExternalChangesAsync(tab, path, watch, cancellation);
    }

    private async Task ObserveExternalChangesAsync(
        DocumentTabViewModel tab,
        string path,
        Func<string, CancellationToken, IAsyncEnumerable<FileChangeNotice>> watch,
        CancellationTokenSource cancellation)
    {
        try
        {
            await foreach (var notice in watch(path, cancellation.Token)
                .WithCancellation(cancellation.Token)
                .ConfigureAwait(false))
            {
                if (!IsCurrentExternalWatch(tab.Id, cancellation))
                {
                    break;
                }

                await HandleExternalChangeAsync(tab, notice, cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (IsCurrentExternalWatch(tab.Id, cancellation))
            {
                await HandleExternalChangeAsync(
                        tab,
                        FileChangeNotice.Inaccessible(path, exception.GetType().Name),
                        cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseExternalWatch(tab.Id, cancellation);
        }
    }

    private void RecordSuccessfulOpen(string path)
    {
        if (recordSuccessfulOpen is null)
        {
            return;
        }

        IReadOnlyList<RecentDocumentEntry> replacement;
        try
        {
            replacement = recordSuccessfulOpen(path);
        }
        catch (Exception)
        {
            return;
        }

        var generation = Interlocked.Increment(ref recentDocumentsGeneration);
        dispatcher(() =>
        {
            if (disposed || Volatile.Read(ref recentDocumentsGeneration) != generation)
            {
                return;
            }

            RecentDocuments.Clear();
            foreach (var entry in replacement)
            {
                if (disposed || Volatile.Read(ref recentDocumentsGeneration) != generation)
                {
                    return;
                }

                RecentDocuments.Add(entry);
            }
        });
    }

    private bool IsCurrentExternalWatch(Guid tabId, CancellationTokenSource cancellation) =>
        !disposed &&
        externalWatchOperations.TryGetValue(tabId, out var current) &&
        ReferenceEquals(current, cancellation) &&
        !cancellation.IsCancellationRequested;

    private void CancelExternalWatch(Guid tabId)
    {
        if (!externalWatchOperations.TryRemove(tabId, out var cancellation))
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ReleaseExternalWatch(Guid tabId, CancellationTokenSource cancellation)
    {
        if (externalWatchOperations.TryGetValue(tabId, out var current) &&
            ReferenceEquals(current, cancellation))
        {
            externalWatchOperations.TryRemove(tabId, out _);
            cancellation.Dispose();
        }
    }

    private Task DispatchAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            dispatcher(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private void ClearExternalConflict()
    {
        externalConflict = null;
        ConflictBar.Clear();
    }

    private async Task<bool> SynchronizeRecoveryAfterSaveAsync(
        DocumentTabViewModel tab,
        CancellationToken cancellationToken)
    {
        try
        {
            if (tab.IsDirty)
            {
                if (tab.Buffer is { } buffer)
                {
                    scheduleRecovery?.Invoke(buffer);
                }

                return true;
            }

            if (removeRecovery is not null)
            {
                await removeRecovery(tab.Id, cancellationToken);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            OperationCanceledException)
        {
            return false;
        }
    }

    private async Task NotifySaveCompletedAsync(
        SaveCompletionOwner owner,
        Func<Guid, long, CancellationToken, Task> notify,
        CancellationToken cancellationToken)
    {
        try
        {
            await notify(owner.TabId, owner.SavedRevision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await DispatchAsync(() =>
            {
                if (IsCurrentSaveCompletion(owner, cancellationToken, requireExternalGeneration: false) &&
                    ReferenceEquals(ActiveTab, owner.Tab))
                {
                    ShowEditingError(
                        "문서는 저장했지만 편집 화면을 갱신할 수 없습니다.",
                        blocksClose: false);
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task RevalidatePendingExternalWithNewOperationAsync(
        DocumentTabViewModel tab,
        CancellationToken cancellationToken)
    {
        if (!pendingExternalNotices.ContainsKey(tab.Id))
        {
            return;
        }

        CancelTabOperation(tab.Id);
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tabOperations[tab.Id] = operation;
        try
        {
            await RevalidatePendingExternalAsync(tab, operation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            ReleaseTabOperation(tab.Id, operation);
        }
    }

    private async Task RevalidatePendingExternalAsync(
        DocumentTabViewModel tab,
        CancellationToken cancellationToken)
    {
        if (!pendingExternalNotices.TryGetValue(tab.Id, out var pending) ||
            !IsCurrentExternalPathOwner(
                pending.Tab,
                pending.Buffer,
                pending.Path,
                pending.LoadGeneration,
                pending.NavigationGeneration,
                cancellationToken))
        {
            return;
        }

        LoadedDocument? actual = null;
        FileChangeNotice? pathNotice = null;
        try
        {
            actual = await loadDocument(pending.Path, null, cancellationToken);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            pathNotice = pending.Notice.Kind == FileChangeKind.Renamed &&
                pending.Notice.RelatedPath is { } relatedPath
                ? FileChangeNotice.Renamed(pending.Path, relatedPath)
                : FileChangeNotice.Deleted(pending.Path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            pathNotice = FileChangeNotice.Inaccessible(pending.Path, exception.GetType().Name);
        }

        await DispatchAsync(() =>
        {
            if (!IsCurrentExternalPathOwner(
                    pending.Tab,
                    pending.Buffer,
                    pending.Path,
                    pending.LoadGeneration,
                    pending.NavigationGeneration,
                    cancellationToken) ||
                !ReferenceEquals(ActiveTab, pending.Tab) ||
                !externalNoticeGenerations.TryGetValue(tab.Id, out var currentGeneration) ||
                currentGeneration != pending.Generation ||
                !pendingExternalNotices.TryRemove(
                    new KeyValuePair<Guid, PendingExternalNotice>(tab.Id, pending)))
            {
                return;
            }

            if (pathNotice is not null)
            {
                externalConflict = null;
                ClearKeepMineVersion(tab);
                ConflictBar.ShowPathState(pathNotice);
                return;
            }

            var document = actual ?? throw new InvalidOperationException("External revalidation returned no document.");
            if (Equals(document.Version, pending.Buffer.BaselineVersion))
            {
                ClearKeepMineVersion(tab);
                ClearExternalConflict();
                return;
            }

            if (tab.IsDirty)
            {
                ClearKeepMineVersion(tab);
                externalConflict = new ExternalConflictContext(
                    tab,
                    pending.Buffer,
                    pending.Path,
                    tab.Revision,
                    pending.LoadGeneration,
                    pending.NavigationGeneration,
                    saveGenerations.GetValueOrDefault(tab.Id),
                    tab.Text,
                    pending.Buffer.BaselineVersion,
                    document);
                ConflictBar.ShowConflict();
                return;
            }

            tab.ApplyExternalLoaded(document);
            ClearKeepMineVersion(tab);
            ClearExternalConflict();
        });
    }

    private void ClearPendingExternal(Guid tabId)
    {
        pendingExternalNotices.TryRemove(tabId, out _);
        externalNoticeGenerations.TryRemove(tabId, out _);
    }

    private void SetKeepMineVersion(DocumentTabViewModel tab, DiskFileVersion version)
    {
        keepMineVersions[tab.Id] = version;
        if (ReferenceEquals(ActiveTab, tab))
        {
            OnPropertyChanged(nameof(KeepMineObservedVersion));
        }
    }

    private void ClearKeepMineVersion(DocumentTabViewModel tab)
    {
        if (keepMineVersions.TryRemove(tab.Id, out _) && ReferenceEquals(ActiveTab, tab))
        {
            OnPropertyChanged(nameof(KeepMineObservedVersion));
        }
    }

    private void ReleaseTabOperation(Guid tabId, CancellationTokenSource cancellation)
    {
        if (tabOperations.TryGetValue(tabId, out var current) &&
            ReferenceEquals(current, cancellation))
        {
            tabOperations.Remove(tabId);
        }

        cancellation.Dispose();
    }

    private void ActiveTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTabViewModel.Error))
        {
            if (ActiveTab?.Error is not null)
            {
                ClearOutline();
            }

            NotifyActiveErrorChanged();
        }
    }

    private void NotifyActiveErrorChanged()
    {
        OnPropertyChanged(nameof(HasActiveError));
        OnPropertyChanged(nameof(ActiveErrorMessage));
        OnPropertyChanged(nameof(CanRetryActiveError));
        OnPropertyChanged(nameof(CanChooseEncodingForActiveError));
        OnPropertyChanged(nameof(CanCloseActiveError));
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void ClearOutlineIfActive(DocumentTabViewModel tab)
    {
        if (ReferenceEquals(ActiveTab, tab))
        {
            ClearOutline();
        }
    }

    private void ClearOutline() => sidebar?.SetOutline([]);

    private DocumentTabViewModel RequireActiveTab() =>
        ActiveTab ?? throw new InvalidOperationException("There is no active document tab.");

    private DocumentTabViewModel RequireCurrentDocument()
    {
        var tab = RequireActiveTab();
        if (tab.Error is not null || tab.Revision <= 0)
        {
            throw new InvalidOperationException("There is no active loaded document.");
        }

        return tab;
    }

    private enum NavigationLoadResult
    {
        Succeeded,
        Failed,
        Superseded,
    }

    private sealed record ExternalConflictContext(
        DocumentTabViewModel Tab,
        DocumentBuffer Buffer,
        string Path,
        long Revision,
        long LoadGeneration,
        long NavigationGeneration,
        long SaveGeneration,
        string MineText,
        DiskFileVersion MineBaselineVersion,
        LoadedDocument External);

    private sealed record SaveCompletionOwner(
        Guid ShellLifetimeId,
        DocumentTabViewModel Tab,
        Guid TabId,
        DocumentBuffer Buffer,
        string SavedPath,
        long SavedRevision,
        long CommittedRevision,
        DiskFileVersion SavedVersion,
        IDocumentFormatProvider? FormatProvider,
        long LoadGeneration,
        long NavigationGeneration,
        long SaveGeneration,
        long? ExternalNoticeGeneration,
        bool IsSaveAs);

    private sealed record ExternalReloadOwner(
        Guid ShellLifetimeId,
        DocumentTabViewModel Tab,
        Guid TabId,
        DocumentBuffer Buffer,
        string Path,
        long Revision,
        DiskFileVersion Version,
        IDocumentFormatProvider? FormatProvider,
        long LoadGeneration,
        long NavigationGeneration,
        long SaveGeneration,
        long? ExternalNoticeGeneration,
        DocumentTabViewModel? ActiveTab);

    private sealed record PendingExternalNotice(
        DocumentTabViewModel Tab,
        DocumentBuffer Buffer,
        string Path,
        long LoadGeneration,
        long NavigationGeneration,
        long Generation,
        FileChangeNotice Notice);
}
