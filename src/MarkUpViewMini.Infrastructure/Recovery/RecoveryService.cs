using System.Text.Json;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Files;
using MarkUpViewMini.Infrastructure.Paths;
using MarkUpViewMini.Infrastructure.Time;

namespace MarkUpViewMini.Infrastructure.Recovery;

public sealed class RecoveryService : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly string recoveryDirectory;
    private readonly IClock clock;
    private readonly IRecoveryFileAccess files;
    private readonly Dictionary<Guid, TabState> tabs = [];
    private bool disposed;

    public RecoveryService(IAppDataPaths paths)
        : this(paths, new SystemClock(), new PhysicalRecoveryFileAccess())
    {
    }

    internal RecoveryService(IAppDataPaths paths, IClock clock, IRecoveryFileAccess files)
    {
        ArgumentNullException.ThrowIfNull(paths);
        recoveryDirectory = Path.GetFullPath(paths.RecoveryDirectory);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public void Schedule(DocumentBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var snapshot = buffer.CaptureSnapshot();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!snapshot.IsDirty)
            {
                return;
            }

            if (!tabs.TryGetValue(snapshot.TabId, out var state))
            {
                state = new TabState();
                tabs.Add(snapshot.TabId, state);
            }

            RetireWorker(state.Work, state.WorkCancellation);
            state.Generation++;
            state.Snapshot = snapshot;
            state.NextWriteDueUtc = clock.UtcNow + Debounce;
            state.WorkCancellation = new CancellationTokenSource();
            var generation = state.Generation;
            state.Work = RunAsync(snapshot.TabId, state, generation, state.WorkCancellation.Token);
        }
    }

    public async Task RemoveAsync(Guid tabId, CancellationToken cancellationToken)
    {
        TabState state;
        Task worker;
        long removalGeneration;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!tabs.TryGetValue(tabId, out state!))
            {
                state = new TabState();
                tabs.Add(tabId, state);
            }

            RetireWorker(state.Work, state.WorkCancellation);
            state.WorkCancellation = null;
            state.Generation++;
            removalGeneration = state.Generation;
            state.Snapshot = null;
            worker = state.Work;
        }

        await state.Operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool stillOwnsRemoval;
            lock (gate)
            {
                stillOwnsRemoval = state.Generation == removalGeneration && state.Snapshot is null;
            }

            if (stillOwnsRemoval)
            {
                files.DeleteIfExists(GetRecordPath(tabId));
            }
        }
        finally
        {
            state.Operation.Release();
        }

        await IgnoreOwnedCancellationAsync(worker).ConfigureAwait(false);
        lock (gate)
        {
            if (state.Generation == removalGeneration && state.Snapshot is null)
            {
                tabs.Remove(tabId);
                state.WorkCancellation?.Dispose();
            }
        }
    }

    public async Task<IReadOnlyList<RecoveryRecord>> LoadAvailableAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        IReadOnlyList<string> paths;
        try
        {
            paths = files.EnumerateFiles(recoveryDirectory, "*.json");
        }
        catch (Exception)
        {
            return [];
        }

        var records = new List<RecoveryRecord>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await files.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<RecoveryRecord>(bytes);
                if (record is not null && IsValid(record, path))
                {
                    records.Add(record with { Path = Path.GetFullPath(record.Path) });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }

        return records
            .OrderByDescending(record => record.SavedAtUtc)
            .ThenBy(record => record.TabId)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        List<(Guid TabId, TabState State, bool IsDue)> owned;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            var now = clock.UtcNow;
            owned = tabs
                .Select(pair =>
                {
                    var state = pair.Value;
                    var isDue = state.Snapshot?.IsDirty == true &&
                        state.NextWriteDueUtc <= now;
                    return (pair.Key, state, isDue);
                })
                .ToList();
            foreach (var (_, state, _) in owned)
            {
                state.WorkCancellation?.Cancel();
                state.Generation++;
            }
        }

        foreach (var (_, state, _) in owned)
        {
            await IgnoreOwnedCancellationAsync(state.Work).ConfigureAwait(false);
        }

        foreach (var (tabId, state, isDue) in owned)
        {
            if (!isDue || state.Snapshot?.IsDirty != true)
            {
                continue;
            }

            await state.Operation.WaitAsync().ConfigureAwait(false);
            try
            {
                var record = CreateRecord(state.Snapshot, clock.UtcNow);
                if (record is not null)
                {
                    try
                    {
                        await TryWriteRecordAsync(tabId, record, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            finally
            {
                state.Operation.Release();
            }
        }

        lock (gate)
        {
            tabs.Clear();
        }

        foreach (var (_, state, _) in owned)
        {
            state.WorkCancellation?.Dispose();
        }
    }

    private async Task RunAsync(
        Guid tabId,
        TabState state,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await clock.Delay(Debounce, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    await WriteOwnedRecordAsync(tabId, state, generation, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                }

                await clock.Delay(Heartbeat, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WriteOwnedRecordAsync(
        Guid tabId,
        TabState state,
        long generation,
        CancellationToken cancellationToken)
    {
        await state.Operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DocumentBufferSnapshot? snapshot;
            lock (gate)
            {
                if (disposed ||
                    !tabs.TryGetValue(tabId, out var current) ||
                    !ReferenceEquals(current, state) ||
                    state.Generation != generation)
                {
                    return;
                }

                snapshot = state.Snapshot;
            }

            var record = snapshot is null
                ? null
                : CreateRecord(snapshot, clock.UtcNow);
            if (record is null)
            {
                return;
            }

            await TryWriteRecordAsync(tabId, record, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (!disposed &&
                    tabs.TryGetValue(tabId, out var current) &&
                    ReferenceEquals(current, state) &&
                    state.Generation == generation)
                {
                    state.NextWriteDueUtc = clock.UtcNow + Heartbeat;
                }
            }
        }
        finally
        {
            state.Operation.Release();
        }
    }

    private async Task TryWriteRecordAsync(
        Guid tabId,
        RecoveryRecord record,
        CancellationToken cancellationToken)
    {
        var target = GetRecordPath(tabId);
        files.EnsureDirectory(recoveryDirectory);
        var temporary = files.CreateTemporaryPath(target);
        EnsureRecoveryChild(temporary);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            await files.CreateNewAsync(temporary, cancellationToken).ConfigureAwait(false);
            await files.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            files.FlushToDisk(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Exists(target))
            {
                files.Replace(temporary, target);
            }
            else
            {
                files.Move(temporary, target);
            }
        }
        finally
        {
            try
            {
                files.DeleteIfExists(temporary);
            }
            catch (Exception)
            {
            }
        }
    }

    private static RecoveryRecord? CreateRecord(
        DocumentBufferSnapshot snapshot,
        DateTime savedAtUtc)
    {
        if (!snapshot.IsDirty)
        {
            return null;
        }

        return new RecoveryRecord(
            RecoveryRecord.CurrentSchemaVersion,
            snapshot.TabId,
            snapshot.Path,
            snapshot.BaselineVersion,
            snapshot.Encoding,
            snapshot.NewLine,
            snapshot.PreferredNewLine,
            snapshot.Revision,
            DateTime.SpecifyKind(savedAtUtc, DateTimeKind.Utc),
            RecoveryRecord.EncodeBody(snapshot.Text));
    }

    private bool IsValid(RecoveryRecord record, string recordPath)
    {
        if (record.SchemaVersion != RecoveryRecord.CurrentSchemaVersion ||
            record.TabId == Guid.Empty ||
            string.IsNullOrWhiteSpace(record.Path) ||
            record.BaselineVersion is null ||
            record.BaselineVersion.Length < 0 ||
            record.BaselineVersion.LastWriteTimeUtc.Kind != DateTimeKind.Utc ||
            !IsSha256(record.BaselineVersion.Sha256) ||
            record.Encoding is null ||
            string.IsNullOrWhiteSpace(record.Encoding.WebName) ||
            !Enum.IsDefined(record.NewLine) ||
            record.Revision <= 0 ||
            record.BodyUtf16Base64 is null ||
            record.PreferredNewLine is not ("\r\n" or "\n" or "\r") ||
            record.SavedAtUtc.Kind != DateTimeKind.Utc ||
            !Path.IsPathFullyQualified(record.Path) ||
            !string.Equals(record.Path, Path.GetFullPath(record.Path), StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(recordPath),
                record.TabId.ToString("N"),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var encoding = DocumentFileService.CreateStrictEncoding(record.Encoding);
            var preamble = encoding.GetPreamble();
            var isSupportedUnicode = encoding.CodePage is 65001 or 1200 or 1201;
            if ((!isSupportedUnicode && preamble.Length > 0) ||
                (record.Encoding.EmitPreamble && preamble.Length == 0))
            {
                return false;
            }

            _ = record.DecodeBody();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void RetireWorker(Task worker, CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        _ = worker.ContinueWith(
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

    private string GetRecordPath(Guid tabId)
    {
        var path = Path.Combine(recoveryDirectory, $"{tabId:N}.json");
        EnsureRecoveryChild(path);
        return path;
    }

    private void EnsureRecoveryChild(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.Equals(directory, recoveryDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Recovery files must stay in the recovery directory.");
        }
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

    private sealed class TabState
    {
        internal SemaphoreSlim Operation { get; } = new(1, 1);
        internal long Generation { get; set; }
        internal DocumentBufferSnapshot? Snapshot { get; set; }
        internal DateTime NextWriteDueUtc { get; set; }
        internal CancellationTokenSource? WorkCancellation { get; set; }
        internal Task Work { get; set; } = Task.CompletedTask;
    }
}
