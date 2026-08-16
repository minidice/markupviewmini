using System.Text.Json;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Files;
using MarkUpViewMini.Infrastructure.Paths;
using MarkUpViewMini.Infrastructure.Recovery;
using MarkUpViewMini.Infrastructure.Time;

namespace MarkUpViewMini.Infrastructure.Tests.Recovery;

public sealed class RecoveryServiceTests
{
    [Fact]
    public async Task Edits_inside_two_seconds_write_one_latest_atomic_record_in_recovery_directory()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        var service = CreateService(clock, files);
        var buffer = DirtyBuffer("first");
        service.Schedule(buffer);
        clock.Advance(TimeSpan.FromSeconds(1));
        buffer.Apply(new DocumentEdit(buffer.Revision, [new TextChange(buffer.Text.Length, buffer.Text.Length, " latest")]));
        service.Schedule(buffer);

        clock.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Yield();
        Assert.Equal(0, files.CommitCount);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => files.CommitCount == 1);

        var record = ReadOnlyRecord(files, buffer.TabId);
        Assert.Equal("first latest", record.DecodeBody());
        Assert.Equal(buffer.Revision, record.Revision);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(3), record.SavedAtUtc);
        Assert.All(files.TouchedPaths, path => Assert.StartsWith(RecoveryDirectory, path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Path.GetDirectoryName(buffer.Path)!, files.TouchedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["directory", "temp", "create", "write", "flush", "exists", "move", "cleanup"], files.CommitOperations);
    }

    [Fact]
    public async Task Dirty_heartbeat_refreshes_latest_record_every_thirty_seconds()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        await using var service = CreateService(clock, files);
        var buffer = DirtyBuffer("heartbeat");
        service.Schedule(buffer);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => files.CommitCount == 1);
        var first = ReadOnlyRecord(files, buffer.TabId);

        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => files.CommitCount == 2);

        var second = ReadOnlyRecord(files, buffer.TabId);
        Assert.Equal(first.DecodeBody(), second.DecodeBody());
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.SavedAtUtc.AddSeconds(30), second.SavedAtUtc);
    }

    [Fact]
    public async Task A_failed_write_is_retried_at_the_next_heartbeat_without_leaking_or_spinning()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess { RemainingWriteFailures = 1 };
        await using var service = CreateService(clock, files);
        var buffer = DirtyBuffer("retry secret");
        service.Schedule(buffer);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => files.WriteAttempts == 1);
        Assert.Equal(0, files.CommitCount);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Yield();
        Assert.Equal(1, files.WriteAttempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => files.CommitCount == 1);

        Assert.Equal(2, files.WriteAttempts);
        Assert.Equal("retry secret", ReadOnlyRecord(files, buffer.TabId).DecodeBody());
        Assert.DoesNotContain("retry secret", string.Join('|', files.Operations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_buffers_never_create_records_and_dispose_only_flushes_due_dirty_work()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        var service = CreateService(clock, files);
        service.Schedule(CleanBuffer());
        var due = DirtyBuffer("due");
        service.Schedule(due);
        clock.AdvanceWithoutCompleting(TimeSpan.FromSeconds(1));
        var notDue = DirtyBuffer("not due");
        service.Schedule(notDue);
        clock.AdvanceWithoutCompleting(TimeSpan.FromSeconds(1));

        await service.DisposeAsync();

        Assert.Null(files.TryRead(TargetPath(notDue.TabId)));
        Assert.Equal("due", ReadOnlyRecord(files, due.TabId).DecodeBody());
        var commits = files.CommitCount;
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Yield();
        Assert.Equal(commits, files.CommitCount);
    }

    [Fact]
    public async Task Dispose_does_not_rewrite_a_snapshot_before_its_heartbeat_is_due()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        var service = CreateService(clock, files);
        var buffer = DirtyBuffer("already persisted");
        service.Schedule(buffer);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => files.CommitCount == 1);
        clock.AdvanceWithoutCompleting(TimeSpan.FromSeconds(10));

        await service.DisposeAsync();

        Assert.Equal(1, files.CommitCount);
    }

    [Fact]
    public async Task Remove_waits_for_inflight_commit_then_deletes_and_stale_timer_cannot_resurrect()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess { BlockNextWrite = true };
        await using var service = CreateService(clock, files);
        var buffer = DirtyBuffer("secret body");
        service.Schedule(buffer);
        clock.Advance(TimeSpan.FromSeconds(2));
        await files.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var removal = service.RemoveAsync(buffer.TabId, CancellationToken.None);
        files.ReleaseWrite.TrySetResult();
        await removal;
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Yield();

        Assert.Null(files.TryRead(TargetPath(buffer.TabId)));
        Assert.Equal("delete", files.Operations[^1]);
    }

    [Fact]
    public async Task Save_or_discard_removes_only_the_exact_tab_record()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        await using var service = CreateService(clock, files);
        var saved = DirtyBuffer("saved");
        var other = DirtyBuffer("other");
        service.Schedule(saved);
        service.Schedule(other);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => files.CommitCount == 2);

        await service.RemoveAsync(saved.TabId, CancellationToken.None);

        Assert.Null(files.TryRead(TargetPath(saved.TabId)));
        Assert.Equal("other", ReadOnlyRecord(files, other.TabId).DecodeBody());
    }

    [Fact]
    public async Task Timer_owned_by_an_older_revision_writes_only_its_immutable_scheduled_snapshot()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        await using var service = CreateService(clock, files);
        var buffer = DirtyBuffer("scheduled");
        service.Schedule(buffer);
        buffer.Apply(new DocumentEdit(
            buffer.Revision,
            [new TextChange(buffer.Text.Length, buffer.Text.Length, " unscheduled")]));

        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => files.CommitCount == 1);
        var record = ReadOnlyRecord(files, buffer.TabId);
        Assert.Equal("scheduled", record.DecodeBody());
        Assert.Equal(buffer.Revision - 1, record.Revision);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Malformed_or_unsupported_records_are_isolated_without_leaking_body_or_blocking_valid_records()
    {
        const string secret = "PRIVATE-RECOVERY-BODY";
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        var valid = CreateRecord(Guid.NewGuid(), "valid");
        files.Seed(TargetPath(valid.TabId), JsonSerializer.SerializeToUtf8Bytes(valid));
        files.Seed(TargetPath(Guid.NewGuid()), JsonSerializer.SerializeToUtf8Bytes(CreateRecord(Guid.NewGuid(), secret) with { SchemaVersion = 999 }));
        files.Seed(TargetPath(Guid.NewGuid()), JsonSerializer.SerializeToUtf8Bytes(new { body = secret, broken = new[] { 1, 2 } })[..^1]);
        await using var service = CreateService(clock, files);

        var records = await service.LoadAvailableAsync(CancellationToken.None);

        Assert.Equal(valid, Assert.Single(records));
        Assert.DoesNotContain(secret, string.Join('|', files.Operations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semantic_validation_isolates_every_invalid_record_and_accepts_supported_encodings()
    {
        DocumentFileService.RegisterCodePages();
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        var utf16 = CreateRecord(Guid.NewGuid(), "utf16") with
        {
            Encoding = new EncodingDescriptor("utf-16", true),
        };
        var cp949 = CreateRecord(Guid.NewGuid(), "cp949") with
        {
            Encoding = new EncodingDescriptor("ks_c_5601-1987", false),
        };
        var utf16BigEndian = CreateRecord(Guid.NewGuid(), "utf16-be") with
        {
            Encoding = new EncodingDescriptor("utf-16BE", true),
        };
        SeedRecord(files, utf16);
        SeedRecord(files, cp949);
        SeedRecord(files, utf16BigEndian);

        var invalid = new RecoveryRecord[]
        {
            CreateRecord(Guid.NewGuid(), "relative") with { Path = "relative.md" },
            CreateRecord(Guid.NewGuid(), "traversal") with
            {
                Path = Path.Combine(OriginalDirectory, "nested", "..", "traversal.md"),
            },
            CreateRecord(Guid.NewGuid(), "hash") with { BaselineVersion = Version('g') },
            CreateRecord(Guid.NewGuid(), "short") with { BaselineVersion = Version('a') with { Sha256 = "aa" } },
            CreateRecord(Guid.NewGuid(), "time") with { SavedAtUtc = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Local) },
            CreateRecord(Guid.NewGuid(), "baseline time") with
            {
                BaselineVersion = Version('a') with { LastWriteTimeUtc = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Unspecified) },
            },
            CreateRecord(Guid.NewGuid(), "encoding") with { Encoding = new EncodingDescriptor("not-an-encoding", false) },
            CreateRecord(Guid.NewGuid(), "bom") with { Encoding = new EncodingDescriptor("ks_c_5601-1987", true) },
            CreateRecord(Guid.NewGuid(), "utf32") with { Encoding = new EncodingDescriptor("utf-32", false) },
            CreateRecord(Guid.NewGuid(), "newline") with { NewLine = (NewLineKind)999 },
            CreateRecord(Guid.NewGuid(), "preferred") with { PreferredNewLine = "\n\r" },
            CreateRecord(Guid.NewGuid(), "revision") with { Revision = 0 },
            CreateRecord(Guid.NewGuid(), "base64") with { BodyUtf16Base64 = "AQ==" },
            CreateRecord(Guid.NewGuid(), "noncanonical base64") with { BodyUtf16Base64 = " YQA= " },
        };
        foreach (var record in invalid)
        {
            SeedRecord(files, record);
        }

        var filenameMismatch = CreateRecord(Guid.NewGuid(), "mismatch");
        files.Seed(TargetPath(Guid.NewGuid()), JsonSerializer.SerializeToUtf8Bytes(filenameMismatch));
        await using var service = CreateService(clock, files);

        var loaded = await service.LoadAvailableAsync(CancellationToken.None);

        Assert.Equal(
            new[] { cp949.TabId, utf16.TabId, utf16BigEndian.TabId }.Order(),
            loaded.Select(record => record.TabId).Order());
    }

    [Fact]
    public async Task Recovery_roundtrips_isolated_utf16_surrogates_losslessly()
    {
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess();
        await using var service = CreateService(clock, files);
        var body = "before\uD800middle\uDC00after";
        var buffer = DirtyBuffer(body);
        service.Schedule(buffer);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => files.CommitCount == 1);

        var json = files.TryRead(TargetPath(buffer.TabId))!;
        var record = JsonSerializer.Deserialize<RecoveryRecord>(json)!;

        Assert.Equal(body, record.DecodeBody());
        Assert.DoesNotContain("before", System.Text.Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        var loaded = await service.LoadAvailableAsync(CancellationToken.None);
        Assert.Equal(body, Assert.Single(loaded).DecodeBody());
    }

    [Fact]
    public async Task Background_write_failure_is_contained_and_never_exposes_recovery_body()
    {
        const string secret = "BODY-MUST-NOT-LEAK";
        var clock = new FakeClock();
        var files = new RecordingRecoveryFileAccess { WriteFailure = new InvalidOperationException(secret) };
        var service = CreateService(clock, files);
        service.Schedule(DirtyBuffer(secret));
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => files.WriteAttempts == 1);

        var error = await Record.ExceptionAsync(async () => await service.DisposeAsync());

        Assert.Null(error);
        Assert.DoesNotContain(secret, string.Join('|', files.Operations), StringComparison.Ordinal);
    }

    private static RecoveryService CreateService(FakeClock clock, RecordingRecoveryFileAccess files) =>
        new(new TestAppDataPaths(), clock, files);

    private static DocumentBuffer CleanBuffer() =>
        DocumentBuffer.Create(
            Guid.NewGuid(),
            Path.Combine(OriginalDirectory, $"{Guid.NewGuid():N}.md"),
            "clean",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Mixed,
            "\n",
            Version('a'));

    private static DocumentBuffer DirtyBuffer(string text)
    {
        var buffer = CleanBuffer();
        buffer.Apply(new DocumentEdit(0, [new TextChange(0, buffer.Text.Length, text)]));
        return buffer;
    }

    private static RecoveryRecord CreateRecord(Guid tabId, string body) =>
        new(
            RecoveryRecord.CurrentSchemaVersion,
            tabId,
            Path.Combine(OriginalDirectory, $"{tabId:N}.md"),
            Version('a'),
            new EncodingDescriptor("utf-8", true),
            NewLineKind.Mixed,
            "\r\n",
            7,
            DateTime.UnixEpoch.AddHours(1),
            RecoveryRecord.EncodeBody(body));

    private static RecoveryRecord ReadOnlyRecord(RecordingRecoveryFileAccess files, Guid tabId) =>
        JsonSerializer.Deserialize<RecoveryRecord>(files.TryRead(TargetPath(tabId))!)!;

    private static void SeedRecord(RecordingRecoveryFileAccess files, RecoveryRecord record) =>
        files.Seed(TargetPath(record.TabId), JsonSerializer.SerializeToUtf8Bytes(record));

    private static DiskFileVersion Version(char hash) =>
        new(12, DateTime.UnixEpoch, new string(hash, 64));

    private static string TargetPath(Guid tabId) => Path.Combine(RecoveryDirectory, $"{tabId:N}.json");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static readonly string DataDirectory = Path.GetFullPath(Path.Combine("test-data", "app"));
    private static readonly string RecoveryDirectory = Path.Combine(DataDirectory, "recovery");
    private static readonly string OriginalDirectory = Path.GetFullPath(Path.Combine("test-data", "documents"));

    private sealed class TestAppDataPaths : IAppDataPaths
    {
        public string DataDirectory => RecoveryServiceTests.DataDirectory;
        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public string SessionFile => Path.Combine(DataDirectory, "session.json");
        public string RecoveryDirectory => RecoveryServiceTests.RecoveryDirectory;
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string WebView2Directory => Path.Combine(DataDirectory, "webview2");
    }

    private sealed class FakeClock : IClock
    {
        private readonly object gate = new();
        private readonly List<PendingDelay> delays = [];
        private DateTime utcNow = DateTime.UnixEpoch;

        public DateTime UtcNow
        {
            get
            {
                lock (gate)
                {
                    return utcNow;
                }
            }
        }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                var pending = new PendingDelay(utcNow + delay);
                pending.Registration = cancellationToken.Register(() => pending.Completion.TrySetCanceled(cancellationToken));
                delays.Add(pending);
                return pending.Completion.Task;
            }
        }

        internal void Advance(TimeSpan amount)
        {
            List<PendingDelay> due;
            lock (gate)
            {
                utcNow += amount;
                due = delays.Where(delay => delay.Due <= utcNow).ToList();
                delays.RemoveAll(delay => due.Contains(delay));
            }

            foreach (var delay in due)
            {
                delay.Registration.Dispose();
                delay.Completion.TrySetResult();
            }
        }

        internal void AdvanceWithoutCompleting(TimeSpan amount)
        {
            lock (gate)
            {
                utcNow += amount;
            }
        }

        private sealed class PendingDelay(DateTime due)
        {
            internal DateTime Due { get; } = due;
            internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationTokenRegistration Registration { get; set; }
        }
    }

    private sealed class RecordingRecoveryFileAccess : IRecoveryFileAccess
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        private string? temporaryPath;

        internal List<string> Operations { get; } = [];
        internal List<string> CommitOperations { get; } = [];
        internal List<string> TouchedPaths { get; } = [];
        internal bool BlockNextWrite { get; init; }
        internal Exception? WriteFailure { get; init; }
        internal int RemainingWriteFailures { get; set; }
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CommitCount { get; private set; }
        internal int WriteAttempts { get; private set; }

        public void EnsureDirectory(string path) => RecordOperation("directory", path, commit: true);

        public IReadOnlyList<string> EnumerateFiles(string directory, string pattern) =>
            files.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)).ToArray();

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(files[path].ToArray());

        public string CreateTemporaryPath(string targetPath)
        {
            temporaryPath = Path.Combine(Path.GetDirectoryName(targetPath)!, $".{Path.GetFileName(targetPath)}.test.tmp");
            RecordOperation("temp", temporaryPath, commit: true);
            return temporaryPath;
        }

        public Task CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            RecordOperation("create", path, commit: true);
            files.Add(path, []);
            return Task.CompletedTask;
        }

        public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            RecordOperation("write", path, commit: true);
            WriteAttempts++;
            WriteStarted.TrySetResult();
            if (BlockNextWrite)
            {
                await ReleaseWrite.Task;
            }

            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            if (RemainingWriteFailures > 0)
            {
                RemainingWriteFailures--;
                throw new IOException("recovery write failed");
            }

            files[path] = bytes.ToArray();
        }

        public void FlushToDisk(string path) => RecordOperation("flush", path, commit: true);

        public bool Exists(string path)
        {
            RecordOperation("exists", path, commit: true);
            return files.ContainsKey(path);
        }

        public void Replace(string sourcePath, string destinationPath)
        {
            RecordOperation("replace", destinationPath, commit: true);
            files[destinationPath] = files[sourcePath];
            files.Remove(sourcePath);
            CommitCount++;
        }

        public void Move(string sourcePath, string destinationPath)
        {
            RecordOperation("move", destinationPath, commit: true);
            files.Add(destinationPath, files[sourcePath]);
            files.Remove(sourcePath);
            CommitCount++;
        }

        public void DeleteIfExists(string path)
        {
            var isCleanup = string.Equals(path, temporaryPath, StringComparison.OrdinalIgnoreCase);
            RecordOperation(isCleanup ? "cleanup" : "delete", path, commit: isCleanup);
            files.Remove(path);
        }

        internal void Seed(string path, byte[] bytes) => files[path] = bytes;
        internal byte[]? TryRead(string path) => files.TryGetValue(path, out var bytes) ? bytes : null;

        private void RecordOperation(string operation, string path, bool commit)
        {
            Operations.Add(operation);
            if (commit)
            {
                CommitOperations.Add(operation);
            }

            TouchedPaths.Add(path);
        }
    }
}
