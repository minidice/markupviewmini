using System.Text.Json;
using MarkUpViewMini.Infrastructure.Time;

namespace MarkUpViewMini.Infrastructure.State;

internal interface IJsonStateStore
{
    bool PreserveSourceOnFallback { get; }

    Task<T> LoadAsync<T>(
        int supportedSchemaVersion,
        Func<T> defaultFactory,
        CancellationToken cancellationToken);

    Task SaveAsync<T>(T state, CancellationToken cancellationToken);
}

internal interface IStateFileAccess
{
    void EnsureDirectory(string path);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    string CreateTemporaryPath(string targetPath);

    Task CreateNewAsync(string path, CancellationToken cancellationToken);

    Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);

    void FlushToDisk(string path);

    bool Exists(string path);

    void Replace(string sourcePath, string destinationPath);

    void Move(string sourcePath, string destinationPath);

    void MoveWithoutOverwrite(string sourcePath, string destinationPath);

    void DeleteIfExists(string path);
}

public sealed class JsonStateStore : IJsonStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string statePath;
    private readonly IClock clock;
    private readonly IStateFileAccess files;

    public bool PreserveSourceOnFallback { get; private set; }

    public JsonStateStore(string statePath)
        : this(statePath, new SystemClock(), new PhysicalStateFileAccess())
    {
    }

    internal JsonStateStore(string statePath, IClock clock, IStateFileAccess files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        this.statePath = Path.GetFullPath(statePath);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<T> LoadAsync<T>(
        int supportedSchemaVersion,
        Func<T> defaultFactory,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(supportedSchemaVersion, 1);
        ArgumentNullException.ThrowIfNull(defaultFactory);
        PreserveSourceOnFallback = false;
        if (!files.Exists(statePath))
        {
            return defaultFactory();
        }

        byte[] bytes;
        try
        {
            bytes = await files.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return defaultFactory();
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                TryQuarantine();
                return defaultFactory();
            }

            if (schemaVersion > supportedSchemaVersion)
            {
                PreserveSourceOnFallback = true;
                return defaultFactory();
            }

            if (schemaVersion != supportedSchemaVersion)
            {
                TryQuarantine();
                return defaultFactory();
            }

            var state = JsonSerializer.Deserialize<T>(bytes, SerializerOptions);
            if (state is null)
            {
                TryQuarantine();
                return defaultFactory();
            }

            return state;
        }
        catch (JsonException)
        {
            TryQuarantine();
            return defaultFactory();
        }
        catch (Exception)
        {
            return defaultFactory();
        }
    }

    public async Task SaveAsync<T>(T state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(statePath) ??
            throw new InvalidOperationException("The state file must have a parent directory.");
        files.EnsureDirectory(directory);
        var temporary = files.CreateTemporaryPath(statePath);
        EnsureSibling(temporary);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
            await files.CreateNewAsync(temporary, cancellationToken).ConfigureAwait(false);
            await files.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            files.FlushToDisk(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Exists(statePath))
            {
                files.Replace(temporary, statePath);
            }
            else
            {
                files.Move(temporary, statePath);
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

    private void TryQuarantine()
    {
        try
        {
            Quarantine();
        }
        catch (Exception)
        {
        }
    }

    private void Quarantine()
    {
        if (!files.Exists(statePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(statePath)!;
        var fileName = Path.GetFileNameWithoutExtension(statePath);
        var stamp = DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc)
            .ToString("yyyyMMddHHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        for (var collision = 0; ; collision++)
        {
            var suffix = collision == 0 ? string.Empty : $"-{collision}";
            var quarantine = Path.Combine(directory, $"{fileName}.corrupt-{stamp}{suffix}.json");
            EnsureSibling(quarantine);
            if (files.Exists(quarantine))
            {
                continue;
            }

            try
            {
                files.MoveWithoutOverwrite(statePath, quarantine);
                return;
            }
            catch (IOException) when (files.Exists(quarantine))
            {
            }
        }
    }

    private void EnsureSibling(string path)
    {
        var expected = Path.GetDirectoryName(statePath);
        var actual = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("State files must stay in their configured directory.");
        }
    }
}

internal sealed class PhysicalStateFileAccess : IStateFileAccess
{
    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(path, cancellationToken);

    public string CreateTemporaryPath(string targetPath)
    {
        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target) ??
            throw new ArgumentException("The target must have a parent directory.", nameof(targetPath));
        return Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
    }

    public async Task CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    public bool Exists(string path) => File.Exists(path);

    public void Replace(string sourcePath, string destinationPath) =>
        File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);

    public void Move(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath);

    public void MoveWithoutOverwrite(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: false);

    public void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
