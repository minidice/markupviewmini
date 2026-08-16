using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Search;

namespace MarkUpViewMini.Infrastructure.Tests.Search;

public sealed class DocumentSearchServiceFileNameTests : IDisposable
{
    private readonly string _root;
    private readonly DocumentSearchService _service = new();

    public DocumentSearchServiceFileNameTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            nameof(DocumentSearchServiceFileNameTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SearchAsync_finds_registered_file_names_recursively_without_returning_directories()
    {
        // Break caught: searching only the root, matching case-sensitively, or treating a matching directory as a file misses or invents results.
        await File.WriteAllTextAsync(Path.Combine(_root, "GUIDE.md"), "root");
        var chapter = Directory.CreateDirectory(Path.Combine(_root, "chapter"));
        await File.WriteAllTextAsync(Path.Combine(chapter.FullName, "guide.markdown"), "nested");
        await File.WriteAllTextAsync(Path.Combine(chapter.FullName, "guide.txt"), "unregistered");
        Directory.CreateDirectory(Path.Combine(_root, "guide"));

        var requestId = Guid.NewGuid();
        var events = await CollectAsync(new SearchQuery(
            requestId, _root, "guide", SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024));

        var matches = events.OfType<SearchMatch>().ToList();
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal(requestId, match.RequestId));
        Assert.Contains(matches, match => Path.GetFileName(match.Path) == "GUIDE.md");
        Assert.Contains(matches, match => Path.GetFileName(match.Path) == "guide.markdown");
        Assert.DoesNotContain(matches, match => Directory.Exists(match.Path));
        var summary = Assert.IsType<SearchSummary>(Assert.Single(events.OfType<SearchSummary>()));
        Assert.Equal(requestId, summary.RequestId);
        Assert.Equal(2, summary.FilesScanned);
        Assert.False(summary.WasCancelled);
    }

    [Fact]
    public async Task SearchAsync_rejects_an_invalid_regular_expression_before_enumeration()
    {
        // Break caught: allowing regex parsing to happen during traversal turns a query error into a partial filesystem search.
        var query = new SearchQuery(
            Guid.NewGuid(), Path.Combine(_root, "missing-root"), "[", SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: true,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<SearchQueryException>(() => CollectAsync(query));

        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public async Task SearchAsync_rejects_a_backtracking_only_regular_expression_before_enumeration()
    {
        // Break caught: accepting a backreference permits an unbounded backtracking regex to block cancellation while searching file names.
        var query = new SearchQuery(
            Guid.NewGuid(), Path.Combine(_root, "not-a-directory"), @"(\w+)\1", SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: true,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024);
        var service = new DocumentSearchService(
            _ => throw new InvalidOperationException("Enumeration must not start for an invalid query."),
            _ => FileAttributes.Directory);

        await Assert.ThrowsAsync<SearchQueryException>(() => CollectAsync(service, query));
    }

    [Theory]
    [InlineData("chapter-42.md", "^chapter-[0-9]+\\.md$", false, false, true, 0, 13)]
    [InlineData("guide[draft].md", "[draft]", false, false, false, 5, 7)]
    [InlineData("manual.md", ".md", false, false, false, 6, 3)]
    public async Task SearchAsync_matches_the_full_file_name_with_the_requested_options(
        string fileName,
        string text,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        int expectedStart,
        int expectedLength)
    {
        // Break caught: matching a path, stem, case-insensitive text, or unescaped literal metacharacters produces the wrong file-name result or highlight range.
        await File.WriteAllTextAsync(Path.Combine(_root, fileName), "content");

        var events = await CollectAsync(new SearchQuery(
            Guid.NewGuid(), _root, text, SearchMode.FileName,
            matchCase, wholeWord, useRegex, MarkdownExtensions, 10 * 1024 * 1024));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal(fileName, Path.GetFileName(match.Path));
        Assert.Equal(fileName, match.Preview);
        Assert.Equal(expectedStart, match.MatchStart);
        Assert.Equal(expectedLength, match.MatchLength);
        Assert.Null(match.LineNumber);
        Assert.Single(events.OfType<SearchSummary>());
    }

    [Fact]
    public async Task SearchAsync_respects_case_sensitive_file_name_matching()
    {
        // Break caught: adding IgnoreCase when MatchCase is selected returns guide.md as a false-positive for Guide.
        await File.WriteAllTextAsync(Path.Combine(_root, "Guide.md"), "content");
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"), "content");

        var events = await CollectAsync(new SearchQuery(
            Guid.NewGuid(), _root, "Guide", SearchMode.FileName,
            MatchCase: true, WholeWord: false, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal("Guide.md", Path.GetFileName(match.Path));
    }

    [Fact]
    public async Task SearchAsync_requires_word_boundaries_when_whole_word_is_selected()
    {
        // Break caught: substring matching when whole-word is selected returns guideline.md for a search for guide.
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"), "content");
        await File.WriteAllTextAsync(Path.Combine(_root, "guideline.md"), "content");

        var events = await CollectAsync(new SearchQuery(
            Guid.NewGuid(), _root, "guide", SearchMode.FileName,
            MatchCase: false, WholeWord: true, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal("guide.md", Path.GetFileName(match.Path));
    }

    [Fact]
    public async Task SearchAsync_searches_hidden_files_and_directories()
    {
        // Break caught: a hidden-attribute exclusion skips documents the user explicitly asked the search to include.
        var hiddenFile = Path.Combine(_root, "hidden-file.md");
        await File.WriteAllTextAsync(hiddenFile, "content");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
        var hiddenDirectory = Directory.CreateDirectory(Path.Combine(_root, "hidden-directory"));
        File.SetAttributes(hiddenDirectory.FullName, File.GetAttributes(hiddenDirectory.FullName) | FileAttributes.Hidden);
        await File.WriteAllTextAsync(Path.Combine(hiddenDirectory.FullName, "hidden-child.md"), "content");

        var events = await CollectAsync(new SearchQuery(
            Guid.NewGuid(), _root, "hidden", SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024));

        Assert.Equal(
            ["hidden-child.md", "hidden-file.md"],
            events.OfType<SearchMatch>().Select(match => Path.GetFileName(match.Path)).Order());
    }

    [Fact]
    public async Task SearchAsync_does_not_search_through_a_reparse_point_directory()
    {
        // Break caught: traversing a reparse-point directory can follow a link cycle and return documents outside the selected root.
        var linkedDirectory = new DirectoryInfo(Path.Combine(_root, "linked"));
        var nestedDirectoryWasEnumerated = false;
        var service = new DocumentSearchService(
            directory =>
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(directory.FullName, _root))
                {
                    return [linkedDirectory];
                }

                nestedDirectoryWasEnumerated = true;
                return [new FileInfo(Path.Combine(linkedDirectory.FullName, "guide.md"))];
            },
            entry => StringComparer.OrdinalIgnoreCase.Equals(entry.FullName, linkedDirectory.FullName)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory);

        var events = await CollectAsync(service, CreateFileNameQuery("guide"));

        var summary = Assert.IsType<SearchSummary>(Assert.Single(events));
        Assert.Equal(0, summary.FilesScanned);
        Assert.False(nestedDirectoryWasEnumerated);
    }

    [Fact]
    public async Task SearchAsync_counts_an_unreadable_entry_in_its_terminal_summary()
    {
        // Break caught: swallowing a per-entry access failure without accounting for it makes the terminal progress summary inaccurate.
        var unreadableFile = new FileInfo(Path.Combine(_root, "guide.md"));
        var service = new DocumentSearchService(
            _ => [unreadableFile],
            entry => entry.FullName == unreadableFile.FullName
                ? throw new UnauthorizedAccessException("Access denied for test.")
                : FileAttributes.Directory);

        var events = await CollectAsync(service, CreateFileNameQuery("guide"));

        var summary = Assert.IsType<SearchSummary>(Assert.Single(events));
        Assert.Equal(0, summary.FilesScanned);
        Assert.Equal(1, summary.UnreadableFiles);
        Assert.False(summary.WasCancelled);
    }

    [Fact]
    public async Task SearchAsync_reports_a_single_cancelled_summary_when_cancellation_is_requested_before_enumeration()
    {
        // Break caught: throwing or omitting the terminal event on cancellation leaves consumers unable to complete a request deterministically.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var events = await CollectAsync(new SearchQuery(
            Guid.NewGuid(), _root, "guide", SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024), cancellation.Token);

        var summary = Assert.IsType<SearchSummary>(Assert.Single(events));
        Assert.True(summary.WasCancelled);
        Assert.Equal(0, summary.FilesScanned);
    }

    [Fact]
    public async Task SearchAsync_marks_the_summary_cancelled_when_cancellation_follows_a_streamed_match()
    {
        // Break caught: cancellation after a streamed match could stop enumeration but incorrectly report the request as completed.
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"), "content");
        using var cancellation = new CancellationTokenSource();
        var query = new SearchQuery(
            Guid.NewGuid(), _root, "guide", SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024);

        await using var enumerator = _service.SearchAsync(query, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<SearchMatch>(enumerator.Current);

        cancellation.Cancel();

        Assert.True(await enumerator.MoveNextAsync());
        var summary = Assert.IsType<SearchSummary>(enumerator.Current);
        Assert.True(summary.WasCancelled);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task SearchAsync_returns_before_blocked_file_name_enumeration_runs_on_a_worker()
    {
        // Break caught: MoveNextAsync can synchronously scan a large no-match root on the WPF dispatcher before returning an awaitable.
        var callerThread = 0;
        var enumerationThread = 0;
        var callReturnedBeforeRelease = false;
        using var enumerationStarted = new ManualResetEventSlim();
        using var callReturned = new ManualResetEventSlim();
        using var releaseEnumeration = new ManualResetEventSlim();
        var service = new DocumentSearchService(
            _ => Enumerate(),
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal);
        var observer = Task.Run(() =>
        {
            Assert.True(enumerationStarted.Wait(TimeSpan.FromSeconds(5)));
            callReturnedBeforeRelease = callReturned.Wait(TimeSpan.FromSeconds(1));
            releaseEnumeration.Set();
        });
        await using var events = service.SearchAsync(
            CreateFileNameQuery("missing"),
            CancellationToken.None).GetAsyncEnumerator();
        Task<bool>? moveNext = null;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            moveNext = events.MoveNextAsync().AsTask();
            callReturned.Set();
        });

        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        await observer;

        Assert.NotNull(moveNext);
        Assert.True(await moveNext);
        Assert.IsType<SearchSummary>(events.Current);
        Assert.True(callReturnedBeforeRelease);
        Assert.NotEqual(callerThread, enumerationThread);

        IEnumerable<FileSystemInfo> Enumerate()
        {
            enumerationThread = Environment.CurrentManagedThreadId;
            enumerationStarted.Set();
            Assert.True(releaseEnumeration.Wait(TimeSpan.FromSeconds(5)));
            yield break;
        }
    }

    [Fact]
    public async Task SearchAsync_cancellation_reaches_an_in_progress_file_name_worker_without_a_late_match()
    {
        // Break caught: dispatcher-bound enumeration prevents cancel input and can publish a file yielded after cancellation.
        using var enumerationStarted = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var file = new FileInfo(Path.Combine(_root, "guide.md"));
        var service = new DocumentSearchService(
            _ => Enumerate(),
            entry => entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal);
        await using var events = service.SearchAsync(
            CreateFileNameQuery("guide"),
            cancellation.Token).GetAsyncEnumerator();

        var moveNext = events.MoveNextAsync().AsTask();
        Assert.True(enumerationStarted.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        Assert.True(await moveNext.WaitAsync(TimeSpan.FromSeconds(5)));
        var summary = Assert.IsType<SearchSummary>(events.Current);
        Assert.True(summary.WasCancelled);
        Assert.False(await events.MoveNextAsync());

        IEnumerable<FileSystemInfo> Enumerate()
        {
            enumerationStarted.Set();
            Assert.True(SpinWait.SpinUntil(
                () => cancellation.IsCancellationRequested,
                TimeSpan.FromSeconds(5)));
            yield return file;
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private async Task<List<SearchEvent>> CollectAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        return await CollectAsync(_service, query, cancellationToken);
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

    private SearchQuery CreateFileNameQuery(string text)
    {
        return new SearchQuery(
            Guid.NewGuid(), _root, text, SearchMode.FileName,
            MatchCase: false, WholeWord: false, UseRegex: false,
            Extensions: MarkdownExtensions, MaxBodyBytes: 10 * 1024 * 1024);
    }

    private static IReadOnlySet<string> MarkdownExtensions { get; } =
        new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase);
}
