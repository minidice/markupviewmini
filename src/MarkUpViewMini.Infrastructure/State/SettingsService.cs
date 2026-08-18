using MarkUpViewMini.Core.Localization;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Paths;
using MarkUpViewMini.Infrastructure.Time;

namespace MarkUpViewMini.Infrastructure.State;

public sealed class SettingsChangedEventArgs(long generation, SettingsV1 snapshot) : EventArgs
{
    public long Generation { get; } = generation;

    public SettingsV1 Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
}

public sealed class SettingsService : IAsyncDisposable
{
    public const int MaximumRecentDocuments = 20;
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);
    private readonly object gate = new();
    private readonly IJsonStateStore store;
    private readonly IClock clock;
    private readonly TimeSpan debounce;
    private readonly SemaphoreSlim saveOperation = new(1, 1);
    private SettingsV1 current = SettingsV1.CreateDefault();
    private long generation;
    private long savedGeneration;
    private CancellationTokenSource? debounceCancellation;
    private Task debounceWork = Task.CompletedTask;
    private Task? disposeWork;
    private bool preserveFallbackSource;
    private bool disposed;

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public SettingsService(IAppDataPaths paths)
        : this(
            new JsonStateStore((paths ?? throw new ArgumentNullException(nameof(paths))).SettingsFile),
            new SystemClock(),
            DefaultDebounce)
    {
    }

    internal SettingsService(IJsonStateStore store, IClock clock, TimeSpan debounce)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounce, TimeSpan.Zero);
        this.debounce = debounce;
    }

    public SettingsV1 Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public async Task<SettingsV1> LoadAsync(CancellationToken cancellationToken)
    {
        long loadGeneration;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            loadGeneration = generation;
        }

        var loaded = Normalize(await store.LoadAsync(
            SettingsV1.CurrentSchemaVersion,
            SettingsV1.CreateDefault,
            cancellationToken).ConfigureAwait(false));
        SettingsV1 result;
        lock (gate)
        {
            if (!disposed && generation == loadGeneration)
            {
                current = loaded;
                preserveFallbackSource = store.PreserveSourceOnFallback;
            }

            result = current;
        }

        return result;
    }

    public void ScheduleSave(SettingsV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Update(_ => snapshot);
    }

    public void UpdateSidebarPreferences(
        RootFollowMode rootMode,
        SearchMode searchMode,
        SearchOptionsV1 searchOptions) =>
        Update(snapshot => snapshot with
        {
            RootMode = rootMode,
            SidebarSearchMode = searchMode,
            SidebarSearchOptions = searchOptions,
        });

    public void UpdateSidebarWidth(double width) =>
        Update(snapshot => snapshot with { SidebarWidth = width });

    /// <summary>Stores the UI language choice; an empty code means "follow the system".</summary>
    public void UpdateLanguage(string? languageCode) =>
        Update(snapshot => snapshot with { Language = languageCode ?? LanguagePreference.SystemCode });

    public void UpdateEditorPreferences(double splitRatio, FindOptionsV1 findOptions) =>
        Update(snapshot => snapshot with
        {
            EditorSplitRatio = splitRatio,
            FindOptions = findOptions,
        });

    private void Update(Func<SettingsV1, SettingsV1> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        SettingsV1 snapshot;
        long targetGeneration;
        CancellationTokenSource cancellation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            snapshot = Normalize(transform(current));
            if (preserveFallbackSource && snapshot.Equals(current))
            {
                return;
            }

            preserveFallbackSource = false;
            RetireDebounce(debounceWork, debounceCancellation);
            generation++;
            targetGeneration = generation;
            current = snapshot;
            cancellation = new CancellationTokenSource();
            debounceCancellation = cancellation;
            debounceWork = RunDebounceAsync(cancellation.Token);
        }

        NotifyChanged(new SettingsChangedEventArgs(targetGeneration, snapshot));
    }

    public void RecordSuccessfulOpen(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = Path.GetFullPath(path);
        Update(snapshot =>
        {
            var recent = snapshot.RecentDocuments
                .Where(entry => !string.Equals(
                    entry.Path,
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
                .Prepend(new RecentDocumentEntry(normalizedPath))
                .Take(MaximumRecentDocuments)
                .ToArray();
            return snapshot with { RecentDocuments = recent };
        });
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task debounceToJoin;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            debounceCancellation?.Cancel();
            debounceToJoin = debounceWork;
        }

        await IgnoreOwnedCancellationAsync(debounceToJoin).ConfigureAwait(false);
        await DrainLatestAsync(allowDisposed: false, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Task work;
        Task? debounceToJoin = null;
        TaskCompletionSource? completion = null;
        lock (gate)
        {
            if (disposeWork is not null)
            {
                return new ValueTask(disposeWork);
            }

            disposed = true;
            debounceCancellation?.Cancel();
            debounceToJoin = debounceWork;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposeWork = completion.Task;
            work = disposeWork;
        }

        _ = CompleteDisposeAsync(debounceToJoin!, completion!);

        return new ValueTask(work);
    }

    private async Task RunDebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await clock.Delay(debounce, cancellationToken).ConfigureAwait(false);
            await DrainLatestAsync(allowDisposed: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task DrainLatestAsync(bool allowDisposed, CancellationToken cancellationToken)
    {
        await saveOperation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                SettingsV1 snapshot;
                long targetGeneration;
                lock (gate)
                {
                    if (disposed && !allowDisposed)
                    {
                        return;
                    }

                    if (savedGeneration >= generation)
                    {
                        return;
                    }

                    targetGeneration = generation;
                    snapshot = current;
                }

                await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    savedGeneration = Math.Max(savedGeneration, targetGeneration);
                    if (savedGeneration >= generation)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            saveOperation.Release();
        }
    }

    private async Task CompleteDisposeAsync(
        Task debounceToJoin,
        TaskCompletionSource completion)
    {
        try
        {
            await IgnoreOwnedFailureAsync(debounceToJoin).ConfigureAwait(false);
            try
            {
                await DrainLatestAsync(allowDisposed: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            lock (gate)
            {
                debounceCancellation?.Dispose();
                debounceCancellation = null;
            }

            saveOperation.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static SettingsV1 Normalize(SettingsV1 source)
    {
        if (source.SchemaVersion != SettingsV1.CurrentSchemaVersion)
        {
            return SettingsV1.CreateDefault();
        }

        // Only the *shape* is normalised here. Whether the code names a language we ship is
        // decided against the catalogue at display time, so a settings file naming a language
        // added in a later build is preserved instead of being reset on every launch.
        var language = LanguagePreference.Sanitize(source.Language);

        var recent = new List<RecentDocumentEntry>(MaximumRecentDocuments);
        foreach (var entry in source.RecentDocuments ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(entry.Path);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (recent.Any(existing => string.Equals(
                existing.Path,
                path,
                StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            recent.Add(new RecentDocumentEntry(path));
            if (recent.Count == MaximumRecentDocuments)
            {
                break;
            }
        }

        return source with
        {
            SchemaVersion = SettingsV1.CurrentSchemaVersion,
            Language = language,
            RootMode = Enum.IsDefined(source.RootMode)
                ? source.RootMode
                : RootFollowMode.KeepRoot,
            SidebarWidth = double.IsFinite(source.SidebarWidth) && source.SidebarWidth > 0
                ? source.SidebarWidth
                : 280,
            EditorSplitRatio = double.IsFinite(source.EditorSplitRatio) &&
                source.EditorSplitRatio is >= 0.1 and <= 0.9
                    ? source.EditorSplitRatio
                    : 0.5,
            SidebarSearchMode = Enum.IsDefined(source.SidebarSearchMode)
                ? source.SidebarSearchMode
                : SearchMode.FileName,
            SidebarSearchOptions = source.SidebarSearchOptions ?? new(false, false, false),
            FindOptions = source.FindOptions ?? new(false, false, false),
            RecentDocuments = recent.ToArray(),
        };
    }

    private void NotifyChanged(SettingsChangedEventArgs change)
    {
        foreach (EventHandler<SettingsChangedEventArgs> handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, change);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void RetireDebounce(Task work, CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        _ = work.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task IgnoreOwnedCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task IgnoreOwnedFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
