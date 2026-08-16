using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Infrastructure.Files;

internal enum FileChangeSignalKind
{
    Changed,
    Deleted,
    Renamed,
}

internal sealed record FileChangeSignal(
    FileChangeSignalKind Kind,
    string Path,
    string? OldPath = null)
{
    internal static FileChangeSignal Changed(string path) =>
        new(FileChangeSignalKind.Changed, System.IO.Path.GetFullPath(path));

    internal static FileChangeSignal Deleted(string path) =>
        new(FileChangeSignalKind.Deleted, System.IO.Path.GetFullPath(path));

    internal static FileChangeSignal Renamed(string oldPath, string newPath) =>
        new(
            FileChangeSignalKind.Renamed,
            System.IO.Path.GetFullPath(newPath),
            System.IO.Path.GetFullPath(oldPath));
}

internal interface IFileChangeWatcher : IDisposable
{
    event Action<FileChangeSignal>? Signal;

    void Start();
}

internal interface IFileChangeWatcherFactory
{
    IFileChangeWatcher Create(string directory, string fileName);
}

internal interface IFileChangeDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class FileChangeService
{
    private static readonly TimeSpan CoalescingDelay = TimeSpan.FromMilliseconds(150);
    private readonly Func<string, CancellationToken, Task<LoadedDocument>> loadDocument;
    private readonly IFileChangeWatcherFactory watcherFactory;
    private readonly IFileChangeDelay delay;
    private readonly Func<Task>? beforeMismatchedSavedVersionRemoval;
    private readonly ConcurrentDictionary<string, DiskFileVersion> savedVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private int activeWatchCount;

    public FileChangeService(DocumentFileService documentFileService)
        : this(
            (path, token) => documentFileService.LoadAsync(path, token),
            new PhysicalFileChangeWatcherFactory(),
            new SystemFileChangeDelay())
    {
        ArgumentNullException.ThrowIfNull(documentFileService);
    }

    internal FileChangeService(
        Func<string, CancellationToken, Task<LoadedDocument>> loadDocument,
        IFileChangeWatcherFactory watcherFactory,
        IFileChangeDelay delay,
        Func<Task>? beforeMismatchedSavedVersionRemoval = null)
    {
        this.loadDocument = loadDocument ?? throw new ArgumentNullException(nameof(loadDocument));
        this.watcherFactory = watcherFactory ?? throw new ArgumentNullException(nameof(watcherFactory));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.beforeMismatchedSavedVersionRemoval = beforeMismatchedSavedVersionRemoval;
    }

    internal int ActiveWatchCount => Volatile.Read(ref activeWatchCount);

    public void RecordSavedVersion(string path, DiskFileVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(version);
        savedVersions[Path.GetFullPath(path)] = version;
    }

    public async IAsyncEnumerable<FileChangeNotice> WatchAsync(
        string path,
        [EnumeratorCancellation] CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var targetPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(targetPath) ??
            throw new ArgumentException("The watched path must have a parent directory.", nameof(path));
        var channel = Channel.CreateUnbounded<FileChangeSignal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        using var watcher = watcherFactory.Create(directory, Path.GetFileName(targetPath));
        void OnSignal(FileChangeSignal signal)
        {
            if (IsRelevant(signal, targetPath))
            {
                channel.Writer.TryWrite(signal);
            }
        }

        watcher.Signal += OnSignal;
        Interlocked.Increment(ref activeWatchCount);
        try
        {
            watcher.Start();
            while (await channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var signals = new List<FileChangeSignal>();
                while (channel.Reader.TryRead(out var signal))
                {
                    signals.Add(signal);
                }

                await delay.DelayAsync(CoalescingDelay, token).ConfigureAwait(false);
                while (channel.Reader.TryRead(out var signal))
                {
                    signals.Add(signal);
                }

                LoadedDocument? document = null;
                FileChangeNotice? terminalNotice = null;
                try
                {
                    document = await loadDocument(targetPath, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    var rename = signals.LastOrDefault(signal => IsRenameAway(signal, targetPath));
                    terminalNotice = rename is null
                        ? FileChangeNotice.Deleted(targetPath)
                        : FileChangeNotice.Renamed(targetPath, rename.Path);
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException)
                {
                    terminalNotice = FileChangeNotice.Inaccessible(targetPath, exception.GetType().Name);
                }

                if (terminalNotice is not null)
                {
                    yield return terminalNotice;
                    yield break;
                }

                document = document ?? throw new InvalidOperationException("The file load returned no document.");
                if (savedVersions.TryGetValue(targetPath, out var savedVersion))
                {
                    if (Equals(savedVersion, document.Version))
                    {
                        continue;
                    }

                    if (beforeMismatchedSavedVersionRemoval is { } beforeRemoval)
                    {
                        await beforeRemoval().ConfigureAwait(false);
                    }

                    savedVersions.TryRemove(
                        new KeyValuePair<string, DiskFileVersion>(targetPath, savedVersion));
                }

                yield return FileChangeNotice.Changed(targetPath, document);
            }
        }
        finally
        {
            watcher.Signal -= OnSignal;
            channel.Writer.TryComplete();
            Interlocked.Decrement(ref activeWatchCount);
        }
    }

    private static bool IsRelevant(FileChangeSignal signal, string targetPath) =>
        string.Equals(signal.Path, targetPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(signal.OldPath, targetPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsRenameAway(FileChangeSignal signal, string targetPath) =>
        signal.Kind == FileChangeSignalKind.Renamed &&
        string.Equals(signal.OldPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(signal.Path, targetPath, StringComparison.OrdinalIgnoreCase);

    private sealed class SystemFileChangeDelay : IFileChangeDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    private sealed class PhysicalFileChangeWatcherFactory : IFileChangeWatcherFactory
    {
        public IFileChangeWatcher Create(string directory, string fileName) =>
            new PhysicalFileChangeWatcher(directory, fileName);
    }

    private sealed class PhysicalFileChangeWatcher : IFileChangeWatcher
    {
        private readonly FileSystemWatcher watcher;
        private readonly string targetPath;

        internal PhysicalFileChangeWatcher(string directory, string fileName)
        {
            targetPath = Path.GetFullPath(Path.Combine(directory, fileName));
            watcher = new FileSystemWatcher(directory)
            {
                Filter = "*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Deleted;
            watcher.Renamed += Renamed;
        }

        public event Action<FileChangeSignal>? Signal;

        public void Start() => watcher.EnableRaisingEvents = true;

        public void Dispose()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= Changed;
            watcher.Created -= Changed;
            watcher.Deleted -= Deleted;
            watcher.Renamed -= Renamed;
            watcher.Dispose();
        }

        private void Changed(object sender, FileSystemEventArgs args)
        {
            if (IsTarget(args.FullPath))
            {
                Signal?.Invoke(FileChangeSignal.Changed(args.FullPath));
            }
        }

        private void Deleted(object sender, FileSystemEventArgs args)
        {
            if (IsTarget(args.FullPath))
            {
                Signal?.Invoke(FileChangeSignal.Deleted(args.FullPath));
            }
        }

        private void Renamed(object sender, RenamedEventArgs args)
        {
            if (IsTarget(args.FullPath) || IsTarget(args.OldFullPath))
            {
                Signal?.Invoke(FileChangeSignal.Renamed(args.OldFullPath, args.FullPath));
            }
        }

        private bool IsTarget(string path) =>
            string.Equals(Path.GetFullPath(path), targetPath, StringComparison.OrdinalIgnoreCase);
    }
}
