using System.Threading.Channels;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.Infrastructure.Tests.Files;

public sealed class FileChangeServiceTests
{
    private static readonly string TargetPath = Path.GetFullPath("watched.md");

    [Fact]
    public async Task Duplicate_changed_and_renamed_signals_are_coalesced_and_hash_verified_once()
    {
        var source = new FakeWatcherFactory();
        var delay = new FakeDelay();
        var external = Loaded("external", 'b');
        var loadCount = 0;
        var service = new FileChangeService(
            (path, token) =>
            {
                loadCount++;
                return Task.FromResult(external);
            },
            source,
            delay);
        await using var notices = service.WatchAsync(TargetPath, CancellationToken.None).GetAsyncEnumerator();

        var next = notices.MoveNextAsync().AsTask();
        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        source.Watcher.Emit(FileChangeSignal.Renamed(TargetPath + ".tmp", TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();

        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(FileChangeKind.Changed, notices.Current.Kind);
        Assert.Same(external, notices.Current.Document);
        Assert.Equal(external.Version, notices.Current.Version);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task Exact_saved_version_is_suppressed_but_immediate_later_external_version_is_reported()
    {
        var source = new FakeWatcherFactory();
        var delay = new FakeDelay();
        var saved = Loaded("mine", 's');
        var external = Loaded("theirs", 't');
        var loads = new Queue<LoadedDocument>([saved, external]);
        var service = new FileChangeService(
            (path, token) => Task.FromResult(loads.Dequeue()),
            source,
            delay);
        service.RecordSavedVersion(TargetPath, saved.Version);
        await using var notices = service.WatchAsync(TargetPath, CancellationToken.None).GetAsyncEnumerator();

        var next = notices.MoveNextAsync().AsTask();
        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();
        await WaitUntilAsync(() => loads.Count == 1);
        Assert.False(next.IsCompleted);

        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();

        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(external.Version, notices.Current.Version);
        Assert.Equal("theirs", notices.Current.Document?.Text);
    }

    [Fact]
    public async Task Mismatched_old_observation_cannot_remove_a_newer_saved_suppression_token()
    {
        var source = new FakeWatcherFactory();
        var delay = new FakeDelay();
        var savedOne = Loaded("saved one", 'q');
        var externalOne = Loaded("external one", 'r');
        var savedTwo = Loaded("saved two", 's');
        var externalTwo = Loaded("external two", 't');
        var loads = new Queue<LoadedDocument>([externalOne, savedTwo, externalTwo]);
        var mismatchObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMismatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileChangeService(
            (path, token) => Task.FromResult(loads.Dequeue()),
            source,
            delay,
            async () =>
            {
                mismatchObserved.TrySetResult();
                await releaseMismatch.Task;
            });
        service.RecordSavedVersion(TargetPath, savedOne.Version);
        await using var notices = service.WatchAsync(TargetPath, CancellationToken.None).GetAsyncEnumerator();

        var first = notices.MoveNextAsync().AsTask();
        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();
        await mismatchObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.RecordSavedVersion(TargetPath, savedTwo.Version);
        releaseMismatch.TrySetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(externalOne.Version, notices.Current.Version);

        var next = notices.MoveNextAsync().AsTask();
        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();
        await WaitUntilAsync(() => loads.Count == 1);
        Assert.False(next.IsCompleted);

        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(externalTwo.Version, notices.Current.Version);
    }

    [Theory]
    [InlineData(false, FileChangeKind.Deleted)]
    [InlineData(true, FileChangeKind.Renamed)]
    public async Task Missing_target_reports_terminal_path_state_and_releases_watcher(
        bool renamed,
        FileChangeKind expectedKind)
    {
        var source = new FakeWatcherFactory();
        var delay = new FakeDelay();
        var service = new FileChangeService(
            (path, token) => throw new FileNotFoundException(),
            source,
            delay);

        FileChangeNotice notice;
        await using (var notices = service.WatchAsync(TargetPath, CancellationToken.None).GetAsyncEnumerator())
        {
            var next = notices.MoveNextAsync().AsTask();
            source.Watcher.Emit(!renamed
                ? FileChangeSignal.Deleted(TargetPath)
                : FileChangeSignal.Renamed(TargetPath, TargetPath + ".renamed"));
            await delay.WaitUntilPendingAsync();
            delay.ReleaseNext();
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
            notice = notices.Current;
            Assert.False(await notices.MoveNextAsync());
        }

        Assert.Equal(expectedKind, notice.Kind);
        Assert.Equal(renamed ? TargetPath + ".renamed" : null, notice.RelatedPath);
        Assert.Equal(0, service.ActiveWatchCount);
        Assert.Equal(1, source.Watcher.DisposeCount);
    }

    [Fact]
    public async Task Inaccessible_target_reports_terminal_error_without_exposing_exception_message()
    {
        var source = new FakeWatcherFactory();
        var delay = new FakeDelay();
        var service = new FileChangeService(
            (path, token) => throw new UnauthorizedAccessException("secret body"),
            source,
            delay);
        await using var notices = service.WatchAsync(TargetPath, CancellationToken.None).GetAsyncEnumerator();

        var next = notices.MoveNextAsync().AsTask();
        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();

        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(FileChangeKind.Inaccessible, notices.Current.Kind);
        Assert.Equal(nameof(UnauthorizedAccessException), notices.Current.ErrorType);
        Assert.DoesNotContain("secret", notices.Current.DisplayMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(await notices.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_during_coalescing_terminates_and_releases_all_ownership()
    {
        var source = new FakeWatcherFactory();
        var delay = new FakeDelay();
        var service = new FileChangeService(
            (path, token) => Task.FromResult(Loaded("unused", 'u')),
            source,
            delay);
        using var cancellation = new CancellationTokenSource();
        await using var notices = service.WatchAsync(TargetPath, cancellation.Token).GetAsyncEnumerator();
        var next = notices.MoveNextAsync().AsTask();
        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Equal(0, service.ActiveWatchCount);
        Assert.Equal(1, source.Watcher.DisposeCount);
    }

    [Fact]
    public async Task Consumer_abandonment_disposes_callback_source_without_a_worker_leak()
    {
        var source = new FakeWatcherFactory();
        var delay = new FakeDelay();
        var service = new FileChangeService(
            (path, token) => Task.FromResult(Loaded("unused", 'u')),
            source,
            delay);
        var notices = service.WatchAsync(TargetPath, CancellationToken.None).GetAsyncEnumerator();
        var next = notices.MoveNextAsync().AsTask();
        source.Watcher.Emit(FileChangeSignal.Changed(TargetPath));
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));

        await notices.DisposeAsync();

        Assert.Equal(0, service.ActiveWatchCount);
        Assert.Equal(1, source.Watcher.DisposeCount);
        Assert.Equal(0, source.Watcher.SubscriberCount);
    }

    private static LoadedDocument Loaded(string text, char hash) =>
        new(
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(text.Length, DateTime.UnixEpoch.AddDays(hash), new string(hash, 64)));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeWatcherFactory : IFileChangeWatcherFactory
    {
        internal FakeWatcher Watcher { get; } = new();

        public IFileChangeWatcher Create(string directory, string fileName)
        {
            Assert.Equal(Path.GetDirectoryName(TargetPath), directory);
            Assert.Equal(Path.GetFileName(TargetPath), fileName);
            return Watcher;
        }
    }

    private sealed class FakeWatcher : IFileChangeWatcher
    {
        private Action<FileChangeSignal>? signal;

        public event Action<FileChangeSignal>? Signal
        {
            add => signal += value;
            remove => signal -= value;
        }

        internal int DisposeCount { get; private set; }
        internal int SubscriberCount => signal?.GetInvocationList().Length ?? 0;
        internal void Emit(FileChangeSignal value) => signal?.Invoke(value);

        public void Start()
        {
        }

        public void Dispose()
        {
            DisposeCount++;
            signal = null;
        }
    }

    private sealed class FakeDelay : IFileChangeDelay
    {
        private readonly Channel<TaskCompletionSource> pending = Channel.CreateUnbounded<TaskCompletionSource>();
        private readonly Channel<TaskCompletionSource> observed = Channel.CreateUnbounded<TaskCompletionSource>();

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(150), delay);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await pending.Writer.WriteAsync(completion, cancellationToken);
            await observed.Writer.WriteAsync(completion, cancellationToken);
            await completion.Task.WaitAsync(cancellationToken);
        }

        internal async Task WaitUntilPendingAsync() =>
            _ = await observed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        internal void ReleaseNext()
        {
            Assert.True(pending.Reader.TryRead(out var completion));
            completion.SetResult();
        }
    }
}
