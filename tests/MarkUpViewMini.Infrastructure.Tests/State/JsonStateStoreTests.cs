using System.Text;
using MarkUpViewMini.Infrastructure.State;
using MarkUpViewMini.Infrastructure.Time;

namespace MarkUpViewMini.Infrastructure.Tests.State;

public sealed class JsonStateStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        nameof(JsonStateStoreTests),
        Guid.NewGuid().ToString("N"));

    public JsonStateStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Missing_file_returns_defaults_and_unknown_fields_are_ignored()
    {
        // Break caught: a first run or a newer writer's harmless extra field prevents settings startup.
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonStateStore(path);

        var missing = await store.LoadAsync(
            SettingsV1.CurrentSchemaVersion,
            SettingsV1.CreateDefault,
            CancellationToken.None);
        await File.WriteAllTextAsync(
            path,
            """
            {"schemaVersion":1,"sidebarWidth":321,"futureDecoration":"ignored"}
            """);
        var loaded = await store.LoadAsync(
            SettingsV1.CurrentSchemaVersion,
            SettingsV1.CreateDefault,
            CancellationToken.None);

        Assert.Equal(SettingsV1.CreateDefault(), missing);
        Assert.Equal(321, loaded.SidebarWidth);
        Assert.Equal(SettingsV1.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public async Task Future_schema_returns_defaults_without_changing_the_source()
    {
        // Break caught: loading an unsupported schema rewrites data that only a future app understands.
        var path = Path.Combine(directory, "settings.json");
        var source = "{\"schemaVersion\":2,\"sidebarWidth\":444,\"futureSecret\":\"keep\"}";
        await File.WriteAllTextAsync(path, source);
        var store = new JsonStateStore(path);

        var loaded = await store.LoadAsync(
            SettingsV1.CurrentSchemaVersion,
            SettingsV1.CreateDefault,
            CancellationToken.None);

        Assert.Equal(SettingsV1.CreateDefault(), loaded);
        Assert.Equal(source, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Unchanged_future_schema_fallback_is_not_overwritten_by_shutdown_flush()
    {
        // Break caught: the window's unconditional close snapshot destroys a future settings schema after load returns defaults.
        var path = Path.Combine(directory, "settings.json");
        var source = "{\"schemaVersion\":2,\"futureOnly\":true}";
        await File.WriteAllTextAsync(path, source);
        var clock = new FixedClock(DateTime.UnixEpoch);
        var service = new SettingsService(
            new JsonStateStore(path, clock, new PhysicalStateFileAccess()),
            clock,
            TimeSpan.FromMinutes(1));

        var fallback = await service.LoadAsync(CancellationToken.None);
        service.ScheduleSave(fallback);
        await service.DisposeAsync();

        Assert.Equal(source, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Malformed_json_is_quarantined_without_overwriting_a_name_collision()
    {
        // Break caught: two corrupt loads in one clock tick either overwrite evidence or leave startup blocked.
        var path = Path.Combine(directory, "settings.json");
        const string rawSecret = "secret-find-term-{broken";
        await File.WriteAllTextAsync(path, rawSecret);
        var timestamp = new DateTime(2026, 8, 13, 1, 2, 3, 456, DateTimeKind.Utc);
        var collision = Path.Combine(directory, "settings.corrupt-20260813010203456Z.json");
        await File.WriteAllTextAsync(collision, "existing-corrupt-file");
        var store = new JsonStateStore(path, new FixedClock(timestamp), new PhysicalStateFileAccess());

        var loaded = await store.LoadAsync(
            SettingsV1.CurrentSchemaVersion,
            SettingsV1.CreateDefault,
            CancellationToken.None);

        Assert.Equal(SettingsV1.CreateDefault(), loaded);
        Assert.False(File.Exists(path));
        Assert.Equal("existing-corrupt-file", await File.ReadAllTextAsync(collision));
        var quarantined = Assert.Single(
            Directory.GetFiles(directory, "settings.corrupt-20260813010203456Z-*.json"));
        Assert.Equal(rawSecret, await File.ReadAllTextAsync(quarantined));
    }

    [Fact]
    public async Task Quarantine_failure_still_returns_defaults_without_exposing_corrupt_content()
    {
        // Break caught: denied quarantine IO propagates out of startup instead of isolating the bad state logically.
        const string corruptSecret = "private-search-term-{broken";
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonStateStore(
            path,
            new FixedClock(DateTime.UnixEpoch),
            new DeniedQuarantineFileAccess(path, Encoding.UTF8.GetBytes(corruptSecret)));

        var loaded = await store.LoadAsync(
            SettingsV1.CurrentSchemaVersion,
            SettingsV1.CreateDefault,
            CancellationToken.None);

        Assert.Equal(SettingsV1.CreateDefault(), loaded);
    }

    [Fact]
    public async Task Read_failure_returns_defaults_without_quarantining_the_source()
    {
        // Break caught: a transient sharing/permission failure moves an otherwise valid settings file away.
        var path = Path.Combine(directory, "settings.json");
        var files = new FailingReadStateFileAccess(path);
        var store = new JsonStateStore(path, new FixedClock(DateTime.UnixEpoch), files);

        var loaded = await store.LoadAsync(
            SettingsV1.CurrentSchemaVersion,
            SettingsV1.CreateDefault,
            CancellationToken.None);

        Assert.Equal(SettingsV1.CreateDefault(), loaded);
        Assert.True(files.SourceStillExists);
        Assert.Equal(0, files.MoveAttempts);
    }

    [Fact]
    public async Task Save_uses_same_directory_temp_flush_and_atomic_replacement_then_cleans_up()
    {
        // Break caught: writing settings in place can expose truncated JSON after a crash.
        var path = Path.Combine(directory, "settings.json");
        var files = new RecordingStateFileAccess(path, existingTarget: true);
        var store = new JsonStateStore(path, new FixedClock(DateTime.UnixEpoch), files);

        await store.SaveAsync(SettingsV1.CreateDefault() with { SidebarWidth = 333 }, CancellationToken.None);

        Assert.Equal(
            ["ensure", "temporary", "create", "write", "flush", "exists", "replace", "delete"],
            files.Operations);
        Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(files.TemporaryPath));
        Assert.Contains("\"sidebarWidth\":333", Encoding.UTF8.GetString(files.CommittedBytes));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class RecordingStateFileAccess(string target, bool existingTarget) : IStateFileAccess
    {
        private byte[] temporaryBytes = [];

        public List<string> Operations { get; } = [];

        public string TemporaryPath { get; private set; } = string.Empty;

        public byte[] CommittedBytes { get; private set; } = [];

        public void EnsureDirectory(string path) => Operations.Add("ensure");

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string CreateTemporaryPath(string targetPath)
        {
            Operations.Add("temporary");
            TemporaryPath = Path.Combine(Path.GetDirectoryName(target)!, ".settings.test.tmp");
            return TemporaryPath;
        }

        public Task CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            Operations.Add("create");
            return Task.CompletedTask;
        }

        public Task WriteAllBytesAsync(
            string path,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            Operations.Add("write");
            temporaryBytes = bytes.ToArray();
            return Task.CompletedTask;
        }

        public void FlushToDisk(string path) => Operations.Add("flush");

        public bool Exists(string path)
        {
            Operations.Add("exists");
            return existingTarget;
        }

        public void Replace(string sourcePath, string destinationPath)
        {
            Operations.Add("replace");
            CommittedBytes = temporaryBytes;
        }

        public void Move(string sourcePath, string destinationPath) =>
            throw new NotSupportedException();

        public void MoveWithoutOverwrite(string sourcePath, string destinationPath) =>
            throw new NotSupportedException();

        public void DeleteIfExists(string path) => Operations.Add("delete");
    }

    private sealed class DeniedQuarantineFileAccess(string target, byte[] bytes) : IStateFileAccess
    {
        public void EnsureDirectory(string path) => throw new NotSupportedException();

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(bytes);

        public string CreateTemporaryPath(string targetPath) => throw new NotSupportedException();

        public Task CreateNewAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void FlushToDisk(string path) => throw new NotSupportedException();

        public bool Exists(string path) => string.Equals(path, target, StringComparison.OrdinalIgnoreCase);

        public void Replace(string sourcePath, string destinationPath) => throw new NotSupportedException();

        public void Move(string sourcePath, string destinationPath) => throw new NotSupportedException();

        public void MoveWithoutOverwrite(string sourcePath, string destinationPath) =>
            throw new UnauthorizedAccessException("quarantine denied");

        public void DeleteIfExists(string path) => throw new NotSupportedException();
    }

    private sealed class FailingReadStateFileAccess(string target) : IStateFileAccess
    {
        public bool SourceStillExists { get; private set; } = true;

        public int MoveAttempts { get; private set; }

        public void EnsureDirectory(string path) => throw new NotSupportedException();

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
            throw new IOException("sharing violation for private settings");

        public string CreateTemporaryPath(string targetPath) => throw new NotSupportedException();

        public Task CreateNewAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void FlushToDisk(string path) => throw new NotSupportedException();

        public bool Exists(string path) => SourceStillExists &&
            string.Equals(path, target, StringComparison.OrdinalIgnoreCase);

        public void Replace(string sourcePath, string destinationPath) => throw new NotSupportedException();

        public void Move(string sourcePath, string destinationPath) => throw new NotSupportedException();

        public void MoveWithoutOverwrite(string sourcePath, string destinationPath)
        {
            MoveAttempts++;
            SourceStillExists = false;
        }

        public void DeleteIfExists(string path) => throw new NotSupportedException();
    }
}
