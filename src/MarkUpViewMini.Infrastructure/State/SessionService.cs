using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Paths;

namespace MarkUpViewMini.Infrastructure.State;

public enum SessionSaveReason
{
    AutomaticRestore,
    UserMutation,
}

public sealed class SessionService : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IJsonStateStore store;
    private readonly SemaphoreSlim saveOperation = new(1, 1);
    private SessionV1 current = SessionV1.CreateDefault();
    private long generation;
    private long savedGeneration;
    private bool preserveFallbackSource;
    private bool disposed;
    private Task? disposeWork;

    public SessionService(IAppDataPaths paths)
        : this(new JsonStateStore(
            (paths ?? throw new ArgumentNullException(nameof(paths))).SessionFile))
    {
    }

    internal SessionService(IJsonStateStore store) =>
        this.store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        long loadGeneration;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            loadGeneration = generation;
        }

        var loaded = await store.LoadAsync(
            SessionV1.CurrentSchemaVersion,
            SessionV1.CreateDefault,
            cancellationToken).ConfigureAwait(false);
        var result = Normalize(loaded);
        lock (gate)
        {
            if (!disposed && generation == loadGeneration)
            {
                current = result.Session;
                preserveFallbackSource = store.PreserveSourceOnFallback;
            }

            return generation == loadGeneration
                ? result
                : new SessionLoadResult(current, 0);
        }
    }

    public void ScheduleSave(SessionV1 snapshot) =>
        ScheduleSave(snapshot, SessionSaveReason.UserMutation);

    public void ScheduleSave(SessionV1 snapshot, SessionSaveReason reason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        var normalized = Normalize(snapshot).Session;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            current = normalized;
            if (preserveFallbackSource && reason == SessionSaveReason.AutomaticRestore)
            {
                return;
            }

            preserveFallbackSource = false;
            generation++;
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken) =>
        DrainLatestAsync(allowDisposed: false, cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposeWork is not null)
            {
                return new ValueTask(disposeWork);
            }

            disposed = true;
            disposeWork = CompleteDisposeAsync();
            return new ValueTask(disposeWork);
        }
    }

    private async Task CompleteDisposeAsync()
    {
        try
        {
            await DrainLatestAsync(allowDisposed: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            saveOperation.Dispose();
        }
    }

    private async Task DrainLatestAsync(bool allowDisposed, CancellationToken cancellationToken)
    {
        await saveOperation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                SessionV1 snapshot;
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

    private static SessionLoadResult Normalize(SessionV1 source)
    {
        if (source.SchemaVersion != SessionV1.CurrentSchemaVersion)
        {
            return new SessionLoadResult(SessionV1.CreateDefault(), 1);
        }

        var skipped = 0;
        var windows = new List<SessionWindowV1>();
        var windowIds = new HashSet<Guid>();
        var tabIds = new HashSet<Guid>();
        foreach (var sourceWindow in source.Windows ?? [])
        {
            if (sourceWindow is null ||
                sourceWindow.WindowId == Guid.Empty ||
                !windowIds.Add(sourceWindow.WindowId))
            {
                skipped++;
                continue;
            }

            var tabs = new List<SessionTabV1>();
            foreach (var sourceTab in sourceWindow.Tabs ?? [])
            {
                if (!TryNormalizeTab(sourceTab, out var tab) || !tabIds.Add(tab!.TabId))
                {
                    skipped++;
                    continue;
                }

                tabs.Add(tab);
            }

            windows.Add(sourceWindow with
            {
                Tabs = tabs.ToArray(),
                ActiveTabId = tabs.Any(tab => tab.TabId == sourceWindow.ActiveTabId)
                    ? sourceWindow.ActiveTabId
                    : null,
                RootPath = NormalizeOptionalPath(sourceWindow.RootPath),
                Layout = NormalizeLayout(sourceWindow.Layout),
            });
        }

        return new SessionLoadResult(
            source with
            {
                SchemaVersion = SessionV1.CurrentSchemaVersion,
                Windows = windows.ToArray(),
            },
            skipped);
    }

    private static bool TryNormalizeTab(SessionTabV1? source, out SessionTabV1? result)
    {
        result = null;
        if (source is null || source.TabId == Guid.Empty || !TryNormalizePath(source.Path, out var path))
        {
            return false;
        }

        var history = new List<SessionNavigationEntryV1>();
        foreach (var entry in source.History ?? [])
        {
            if (entry is null || !TryNormalizePath(entry.Path, out var historyPath))
            {
                continue;
            }

            history.Add(entry with
            {
                Path = historyPath,
                Line = entry.Line is > 0 ? entry.Line : null,
                Mode = Enum.IsDefined(entry.Mode) ? entry.Mode : DocumentMode.Read,
                ScrollOffset = IsNonNegativeFinite(entry.ScrollOffset) ? entry.ScrollOffset : null,
            });
        }

        var historyIndex = history.Count == 0
            ? -1
            : Math.Clamp(source.HistoryIndex, 0, history.Count - 1);
        result = source with
        {
            Path = path,
            Mode = Enum.IsDefined(source.Mode) ? source.Mode : DocumentMode.Read,
            History = history.ToArray(),
            HistoryIndex = historyIndex,
            Hints = NormalizeHints(source.Hints),
        };
        return true;
    }

    private static SessionEditorHintsV1 NormalizeHints(SessionEditorHintsV1? source) =>
        source is not null &&
        source.SelectionAnchor >= 0 &&
        source.SelectionHead >= 0 &&
        IsNonNegativeFinite(source.ScrollTop) &&
        double.IsFinite(source.SplitRatio) &&
        source.SplitRatio is >= 0.1 and <= 0.9
            ? source
            : SessionEditorHintsV1.CreateDefault();

    private static SessionWindowLayoutV1 NormalizeLayout(SessionWindowLayoutV1? source) =>
        source is not null &&
        double.IsFinite(source.Left) &&
        double.IsFinite(source.Top) &&
        double.IsFinite(source.Width) &&
        source.Width >= 320 &&
        double.IsFinite(source.Height) &&
        source.Height >= 240
            ? source
            : SessionWindowLayoutV1.CreateDefault();

    private static string? NormalizeOptionalPath(string? path) =>
        TryNormalizePath(path, out var normalized) ? normalized : null;

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsNonNegativeFinite(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0;
}
