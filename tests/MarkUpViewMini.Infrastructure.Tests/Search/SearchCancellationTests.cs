using System.Diagnostics;
using System.Text;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Search;

namespace MarkUpViewMini.Infrastructure.Tests.Search;

public sealed class SearchCancellationTests
{
    [Fact]
    public async Task SearchAsync_skips_an_oversize_body_before_loading_it()
    {
        // Break caught: opening the document before enforcing MaxBodyBytes wastes memory and can decode a file that policy rejects.
        var root = Path.GetFullPath("body-search-root");
        var path = Path.Combine(root, "large.md");
        var loaderWasCalled = false;
        var service = CreateService(
            root,
            [path],
            (_, _) =>
            {
                loaderWasCalled = true;
                return Task.FromResult(CreateLoadedDocument("needle"));
            },
            _ => 11);

        var events = await CollectAsync(service, CreateBodyQuery(root, maxBodyBytes: 10));

        Assert.False(loaderWasCalled);
        Assert.Empty(events.OfType<SearchMatch>());
        var summary = Assert.IsType<SearchSummary>(Assert.Single(events));
        Assert.Equal(1, summary.FilesScanned);
        Assert.Equal(1, summary.SkippedLargeFiles);
        Assert.Equal(0, summary.UnreadableFiles);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("decode")]
    public async Task SearchAsync_counts_expected_per_file_load_failures_and_continues(
        string failureKind)
    {
        // Break caught: allowing an expected I/O, access, or strict-decoder exception to escape aborts the complete search request.
        var root = Path.GetFullPath("body-search-root");
        var unreadablePath = Path.Combine(root, "unreadable.md");
        var readablePath = Path.Combine(root, "readable.md");
        var service = CreateService(
            root,
            [unreadablePath, readablePath],
            (path, _) => path == unreadablePath
                ? Task.FromException<LoadedDocument>(CreateExpectedException(failureKind))
                : Task.FromResult(CreateLoadedDocument("needle")),
            _ => 1);

        var events = await CollectAsync(service, CreateBodyQuery(root));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal(readablePath, match.Path);
        var summary = Assert.IsType<SearchSummary>(Assert.Single(events.OfType<SearchSummary>()));
        Assert.Equal(2, summary.FilesScanned);
        Assert.Equal(1, summary.UnreadableFiles);
        Assert.False(summary.WasCancelled);
    }

    [Fact]
    public async Task SearchAsync_counts_a_file_length_access_failure_and_continues()
    {
        // Break caught: a metadata access failure outside the loader can escape the per-file error boundary and suppress later matches.
        var root = Path.GetFullPath("body-search-root");
        var unreadablePath = Path.Combine(root, "unreadable.md");
        var readablePath = Path.Combine(root, "readable.md");
        var service = CreateService(
            root,
            [unreadablePath, readablePath],
            (_, _) => Task.FromResult(CreateLoadedDocument("needle")),
            path => path == unreadablePath
                ? throw new UnauthorizedAccessException("Access denied for test.")
                : 1);

        var events = await CollectAsync(service, CreateBodyQuery(root));

        Assert.Equal(readablePath, Assert.Single(events.OfType<SearchMatch>()).Path);
        var summary = Assert.Single(events.OfType<SearchSummary>());
        Assert.Equal(2, summary.FilesScanned);
        Assert.Equal(1, summary.UnreadableFiles);
    }

    [Fact]
    public async Task SearchAsync_cancels_a_delayed_load_within_500_ms_and_emits_one_terminal_summary()
    {
        // Break caught: failing to pass/catch the search token leaves the loader running or ends the stream with an exception instead of a summary.
        var root = Path.GetFullPath("body-search-root");
        var path = Path.Combine(root, "delayed.md");
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            root,
            [path],
            async (_, cancellationToken) =>
            {
                loadStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CreateLoadedDocument("unreachable");
            },
            _ => 1);
        using var cancellation = new CancellationTokenSource();

        var collection = CollectAsync(service, CreateBodyQuery(root), cancellation.Token);
        await loadStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        var events = await collection.WaitAsync(TimeSpan.FromMilliseconds(500));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        var summary = Assert.IsType<SearchSummary>(Assert.Single(events));
        Assert.True(summary.WasCancelled);
        Assert.Equal(1, summary.FilesScanned);
    }

    [Fact]
    public void MatchLines_yields_before_enumerating_all_high_density_matches()
    {
        // Break caught: materializing every match before the first yield drains this observable high-density iterator.
        var root = Path.GetFullPath("body-search-root");
        var matchesVisited = 0;
        var matcher = new SearchTextMatcher(
            CreateBodyQuery(root, text: "a*", useRegex: true),
            _ => ObserveHighDensityMatches());

        using var matches = matcher.MatchLines("bbb", CancellationToken.None).GetEnumerator();

        Assert.True(matches.MoveNext());
        Assert.Equal(1, matchesVisited);

        IEnumerable<SearchTextSpan> ObserveHighDensityMatches()
        {
            for (var index = 0; index < 100_000; index++)
            {
                matchesVisited++;
                yield return new SearchTextSpan(0, 0);
            }
        }
    }

    [Fact]
    public void MatchLines_does_not_advance_the_match_source_after_cancelling_a_yielded_match()
    {
        // Break caught: foreach resumes the inner iterator before its body can observe cancellation, entering one unnecessary next scan.
        var root = Path.GetFullPath("body-search-root");
        using var cancellation = new CancellationTokenSource();
        var nextScanCount = 0;
        var matcher = new SearchTextMatcher(
            CreateBodyQuery(root),
            _ => ObserveMatches());
        using var matches = matcher.MatchLines("needle needle", cancellation.Token).GetEnumerator();

        Assert.True(matches.MoveNext());
        cancellation.Cancel();

        Assert.False(matches.MoveNext());
        Assert.Equal(0, nextScanCount);

        IEnumerable<SearchTextSpan> ObserveMatches()
        {
            yield return new SearchTextSpan(0, 6);
            nextScanCount++;
            yield return new SearchTextSpan(7, 6);
        }
    }

    [Fact]
    public async Task SearchAsync_cancels_during_dense_match_iteration_with_one_terminal_summary()
    {
        // Break caught: cancellation after a streamed dense regex match can continue matcher work or omit/duplicate the terminal summary.
        var root = Path.GetFullPath("body-search-root");
        var path = Path.Combine(root, "dense.md");
        var service = CreateService(
            root,
            [path],
            (_, _) => Task.FromResult(CreateLoadedDocument(new string('b', 100_000))),
            _ => 100_000);
        using var cancellation = new CancellationTokenSource();
        await using var events = service.SearchAsync(
                CreateBodyQuery(root, text: "a*", useRegex: true),
                cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<SearchMatch>(events.Current);
        cancellation.Cancel();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        var summary = Assert.IsType<SearchSummary>(events.Current);
        Assert.True(summary.WasCancelled);
        Assert.Equal(1, summary.FilesScanned);
        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task SearchAsync_translates_a_throwing_matcher_cancellation_to_one_terminal_summary()
    {
        // Break caught: a conventional cancellation-aware matcher can throw from MoveNext and fault the channel instead of completing the request.
        var root = Path.GetFullPath("body-search-root");
        var path = Path.Combine(root, "throwing-matcher.md");
        var service = new DocumentSearchService(
            directory => StringComparer.OrdinalIgnoreCase.Equals(directory.FullName, root)
                ? [new FileInfo(path)]
                : [],
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal,
            (_, _) => Task.FromResult(CreateLoadedDocument("needle")),
            _ => 6,
            query => new SearchTextMatcher(query, (_, cancellationToken) => ThrowAfterFirstMatch(cancellationToken)));
        using var cancellation = new CancellationTokenSource();
        await using var events = service.SearchAsync(
                CreateBodyQuery(root),
                cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<SearchMatch>(events.Current);
        cancellation.Cancel();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.True(Assert.IsType<SearchSummary>(events.Current).WasCancelled);
        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));

        static IEnumerable<SearchTextSpan> ThrowAfterFirstMatch(CancellationToken cancellationToken)
        {
            yield return new SearchTextSpan(0, 6);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task SearchAsync_propagates_an_unexpected_matcher_fault_after_a_streamed_match()
    {
        var root = Path.GetFullPath("body-search-root");
        var path = Path.Combine(root, "faulting-matcher.md");
        var service = new DocumentSearchService(
            directory => StringComparer.OrdinalIgnoreCase.Equals(directory.FullName, root)
                ? [new FileInfo(path)]
                : [],
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal,
            (_, _) => Task.FromResult(CreateLoadedDocument("needle")),
            _ => 6,
            query => new SearchTextMatcher(query, (_, _) => ThrowUnexpectedlyAfterFirstMatch()));
        await using var events = service.SearchAsync(
                CreateBodyQuery(root),
                CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<SearchMatch>(events.Current);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.Equal("Matcher fault for test.", exception.Message);

        static IEnumerable<SearchTextSpan> ThrowUnexpectedlyAfterFirstMatch()
        {
            yield return new SearchTextSpan(0, 6);
            throw new InvalidOperationException("Matcher fault for test.");
        }
    }

    [Fact]
    public async Task SearchAsync_translates_a_throwing_enumerator_cancellation_to_one_terminal_summary()
    {
        var root = Path.GetFullPath("file-search-root");
        var path = new FileInfo(Path.Combine(root, "guide.md"));
        var service = new DocumentSearchService(
            (_, cancellationToken) => ThrowAfterFirstFile(cancellationToken),
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal);
        using var cancellation = new CancellationTokenSource();
        await using var events = service.SearchAsync(
                CreateFileNameQuery(root),
                cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<SearchMatch>(events.Current);
        cancellation.Cancel();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.True(Assert.IsType<SearchSummary>(events.Current).WasCancelled);
        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));

        IEnumerable<FileSystemInfo> ThrowAfterFirstFile(CancellationToken cancellationToken)
        {
            yield return path;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task SearchAsync_early_disposal_stops_before_a_blocked_next_file_enumeration()
    {
        // Break caught: acknowledging the first result before cancellation lets DisposeAsync wait forever in the next physical MoveNext.
        var root = Path.GetFullPath("file-search-root");
        var first = new FileInfo(Path.Combine(root, "guide.md"));
        var second = new FileInfo(Path.Combine(root, "guide-two.md"));
        using var nextEnumerationStarted = new ManualResetEventSlim();
        using var releaseEnumeration = new ManualResetEventSlim();
        using var workerStopped = new ManualResetEventSlim();
        var enumerationToken = CancellationToken.None;
        var service = new DocumentSearchService(
            (_, cancellationToken) =>
            {
                enumerationToken = cancellationToken;
                return Enumerate(cancellationToken);
            },
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal);
        using var requestCancellation = new CancellationTokenSource();
        var events = service.SearchAsync(
                CreateFileNameQuery(root),
                requestCancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<SearchMatch>(events.Current);

        var disposedPromptly = await DisposeWithinAsync(events, releaseEnumeration.Set);

        Assert.True(disposedPromptly);
        Assert.False(requestCancellation.IsCancellationRequested);
        Assert.True(enumerationToken.IsCancellationRequested);
        Assert.False(nextEnumerationStarted.IsSet);
        Assert.True(workerStopped.Wait(TimeSpan.FromMilliseconds(500)));

        IEnumerable<FileSystemInfo> Enumerate(CancellationToken cancellationToken)
        {
            try
            {
                yield return first;
                nextEnumerationStarted.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => cancellationToken.IsCancellationRequested || releaseEnumeration.IsSet,
                    TimeSpan.FromSeconds(5)));
                cancellationToken.ThrowIfCancellationRequested();
                yield return second;
            }
            finally
            {
                workerStopped.Set();
            }
        }
    }

    [Fact]
    public async Task SearchAsync_early_disposal_cancels_loader_work_without_cancelling_the_request()
    {
        // Break caught: a loader that receives only the request token cannot observe consumer abandonment and can retain the producer forever.
        var root = Path.GetFullPath("body-search-root");
        var first = Path.Combine(root, "first.md");
        var second = Path.Combine(root, "second.md");
        var releaseSecondLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoadStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLoadToken = CancellationToken.None;
        var service = CreateService(
            root,
            [first, second],
            async (path, cancellationToken) =>
            {
                if (path == first)
                {
                    firstLoadToken = cancellationToken;
                    return CreateLoadedDocument("needle");
                }

                secondLoadStarted.TrySetResult();
                try
                {
                    await Task.WhenAny(
                        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                        releaseSecondLoad.Task);
                    cancellationToken.ThrowIfCancellationRequested();
                    return CreateLoadedDocument("needle");
                }
                finally
                {
                    secondLoadStopped.TrySetResult();
                }
            },
            _ => 1);
        using var requestCancellation = new CancellationTokenSource();
        var events = service.SearchAsync(
                CreateBodyQuery(root),
                requestCancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<SearchMatch>(events.Current);

        var disposedPromptly = await DisposeWithinAsync(events, () => releaseSecondLoad.TrySetResult());

        Assert.True(disposedPromptly);
        Assert.False(requestCancellation.IsCancellationRequested);
        Assert.True(firstLoadToken.IsCancellationRequested);
        if (secondLoadStarted.Task.IsCompleted)
        {
            await secondLoadStopped.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
    }

    [Fact]
    public async Task SearchAsync_early_disposal_stops_a_blocked_matcher_without_a_worker_leak()
    {
        // Break caught: dense matching that observes only the request token can keep DisposeAsync waiting after the first streamed match.
        var root = Path.GetFullPath("body-search-root");
        var path = Path.Combine(root, "dense.md");
        using var releaseMatcher = new ManualResetEventSlim();
        using var matcherStopped = new ManualResetEventSlim();
        var matcherToken = CancellationToken.None;
        var service = new DocumentSearchService(
            directory => StringComparer.OrdinalIgnoreCase.Equals(directory.FullName, root)
                ? [new FileInfo(path)]
                : [],
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal,
            (_, _) => Task.FromResult(CreateLoadedDocument("needle")),
            _ => 6,
            query => new SearchTextMatcher(query, (_, cancellationToken) => ObserveMatches(cancellationToken)));
        using var requestCancellation = new CancellationTokenSource();
        var events = service.SearchAsync(
                CreateBodyQuery(root),
                requestCancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.IsType<SearchMatch>(events.Current);

        var disposedPromptly = await DisposeWithinAsync(events, releaseMatcher.Set);

        Assert.True(disposedPromptly);
        Assert.False(requestCancellation.IsCancellationRequested);
        Assert.True(matcherToken.IsCancellationRequested);
        Assert.True(matcherStopped.Wait(TimeSpan.FromMilliseconds(500)));

        IEnumerable<SearchTextSpan> ObserveMatches(CancellationToken cancellationToken)
        {
            matcherToken = cancellationToken;
            try
            {
                yield return new SearchTextSpan(0, 6);
                Assert.True(SpinWait.SpinUntil(
                    () => cancellationToken.IsCancellationRequested || releaseMatcher.IsSet,
                    TimeSpan.FromSeconds(5)));
            }
            finally
            {
                matcherStopped.Set();
            }
        }
    }

    private static DocumentSearchService CreateService(
        string root,
        IReadOnlyList<string> paths,
        Func<string, CancellationToken, Task<LoadedDocument>> loadDocument,
        Func<string, long> getFileLength)
    {
        return new DocumentSearchService(
            directory => StringComparer.OrdinalIgnoreCase.Equals(directory.FullName, root)
                ? paths.Select(path => (FileSystemInfo)new FileInfo(path))
                : [],
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal,
            loadDocument,
            getFileLength);
    }

    private static async Task<List<SearchEvent>> CollectAsync(
        DocumentSearchService service,
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var events = new List<SearchEvent>();
        await foreach (var searchEvent in service.SearchAsync(query, cancellationToken))
        {
            events.Add(searchEvent);
        }

        return events;
    }

    private static async Task<bool> DisposeWithinAsync(
        IAsyncEnumerator<SearchEvent> events,
        Action releaseBlockedWork)
    {
        var disposal = events.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(500)));
        var disposedPromptly = ReferenceEquals(completed, disposal);
        if (!disposedPromptly)
        {
            releaseBlockedWork();
        }

        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        return disposedPromptly;
    }

    private static SearchQuery CreateBodyQuery(
        string root,
        long maxBodyBytes = 10 * 1024 * 1024,
        string text = "needle",
        bool useRegex = false)
    {
        return new SearchQuery(
            Guid.NewGuid(), root, text, SearchMode.Body,
            MatchCase: false, WholeWord: false, UseRegex: useRegex,
            Extensions: MarkdownExtensions, MaxBodyBytes: maxBodyBytes);
    }

    private static SearchQuery CreateFileNameQuery(string root)
    {
        return new SearchQuery(
            Guid.NewGuid(), root, "guide", SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024);
    }

    private static LoadedDocument CreateLoadedDocument(string text)
    {
        return new LoadedDocument(
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(text.Length, DateTime.UnixEpoch, new string('a', 64)));
    }

    private static Exception CreateExpectedException(string failureKind)
    {
        return failureKind switch
        {
            "io" => new IOException("I/O failure for test."),
            "access" => new UnauthorizedAccessException("Access denied for test."),
            "decode" => new DecoderFallbackException("Strict decoding failure for test."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
    }

    private static IReadOnlySet<string> MarkdownExtensions { get; } =
        new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase);
}
