using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Files;
using MarkUpViewMini.Infrastructure.Folders;

namespace MarkUpViewMini.Infrastructure.Search;

public sealed class DocumentSearchService : IDocumentSearchService
{
    private readonly Func<DirectoryInfo, CancellationToken, IEnumerable<FileSystemInfo>> enumerateEntries;
    private readonly Func<FileSystemInfo, FileAttributes> getAttributes;
    private readonly Func<string, CancellationToken, Task<LoadedDocument>> loadDocument;
    private readonly Func<string, long> getFileLength;
    private readonly Func<SearchQuery, SearchTextMatcher> createMatcher;

    public DocumentSearchService()
        : this(
            (directory, _) => directory.EnumerateFileSystemInfos(),
            entry => File.GetAttributes(entry.FullName),
            new DocumentFileService().LoadAsync,
            path => new FileInfo(path).Length,
            query => new SearchTextMatcher(query))
    {
    }

    public DocumentSearchService(DocumentFileService documentFileService)
        : this(
            (directory, _) => directory.EnumerateFileSystemInfos(),
            entry => File.GetAttributes(entry.FullName),
            (documentFileService ?? throw new ArgumentNullException(nameof(documentFileService))).LoadAsync,
            path => new FileInfo(path).Length,
            query => new SearchTextMatcher(query))
    {
        DocumentFileService = documentFileService;
    }

    internal DocumentFileService? DocumentFileService { get; }

    internal DocumentSearchService(
        Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateEntries,
        Func<FileSystemInfo, FileAttributes> getAttributes)
        : this(
            (directory, _) => enumerateEntries(directory),
            getAttributes,
            new DocumentFileService().LoadAsync,
            path => new FileInfo(path).Length,
            query => new SearchTextMatcher(query))
    {
    }

    internal DocumentSearchService(
        Func<DirectoryInfo, CancellationToken, IEnumerable<FileSystemInfo>> enumerateEntries,
        Func<FileSystemInfo, FileAttributes> getAttributes)
        : this(
            enumerateEntries,
            getAttributes,
            new DocumentFileService().LoadAsync,
            path => new FileInfo(path).Length,
            query => new SearchTextMatcher(query))
    {
    }

    internal DocumentSearchService(
        Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateEntries,
        Func<FileSystemInfo, FileAttributes> getAttributes,
        Func<string, CancellationToken, Task<LoadedDocument>> loadDocument,
        Func<string, long> getFileLength)
        : this(
            (directory, _) => enumerateEntries(directory),
            getAttributes,
            loadDocument,
            getFileLength,
            query => new SearchTextMatcher(query))
    {
    }

    internal DocumentSearchService(
        Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateEntries,
        Func<FileSystemInfo, FileAttributes> getAttributes,
        Func<string, CancellationToken, Task<LoadedDocument>> loadDocument,
        Func<string, long> getFileLength,
        Func<SearchQuery, SearchTextMatcher> createMatcher)
        : this(
            (directory, _) => enumerateEntries(directory),
            getAttributes,
            loadDocument,
            getFileLength,
            createMatcher)
    {
    }

    private DocumentSearchService(
        Func<DirectoryInfo, CancellationToken, IEnumerable<FileSystemInfo>> enumerateEntries,
        Func<FileSystemInfo, FileAttributes> getAttributes,
        Func<string, CancellationToken, Task<LoadedDocument>> loadDocument,
        Func<string, long> getFileLength,
        Func<SearchQuery, SearchTextMatcher> createMatcher)
    {
        this.enumerateEntries = enumerateEntries ?? throw new ArgumentNullException(nameof(enumerateEntries));
        this.getAttributes = getAttributes ?? throw new ArgumentNullException(nameof(getAttributes));
        this.loadDocument = loadDocument ?? throw new ArgumentNullException(nameof(loadDocument));
        this.getFileLength = getFileLength ?? throw new ArgumentNullException(nameof(getFileLength));
        this.createMatcher = createMatcher ?? throw new ArgumentNullException(nameof(createMatcher));
    }

    public async IAsyncEnumerable<SearchEvent> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Root);
        ArgumentNullException.ThrowIfNull(query.Text);
        ArgumentNullException.ThrowIfNull(query.Extensions);

        var matcher = createMatcher(query);
        var channel = Channel.CreateBounded<ProducedSearchEvent>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var consumerCancellation = new CancellationTokenSource();
        using var workCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            consumerCancellation.Token);
        var producer = Task.Run(
            () => ProduceSearchEventsAsync(
                query,
                matcher,
                channel.Writer,
                cancellationToken,
                consumerCancellation.Token,
                workCancellation.Token),
            CancellationToken.None);

        ProducedSearchEvent? outstandingEvent = null;
        try
        {
            await foreach (var searchEvent in channel.Reader
                .ReadAllAsync(consumerCancellation.Token)
                .ConfigureAwait(false))
            {
                outstandingEvent = searchEvent;
                yield return searchEvent.Event;
                outstandingEvent.Acknowledged.TrySetResult();
                outstandingEvent = null;
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            consumerCancellation.Cancel();
            outstandingEvent?.Acknowledged.TrySetResult();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (consumerCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ProduceSearchEventsAsync(
        SearchQuery query,
        SearchTextMatcher matcher,
        ChannelWriter<ProducedSearchEvent> writer,
        CancellationToken cancellationToken,
        CancellationToken consumerCancellationToken,
        CancellationToken workCancellationToken)
    {
        try
        {
            await WriteSearchEventsAsync(
                query,
                matcher,
                writer,
                cancellationToken,
                consumerCancellationToken,
                workCancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private async Task WriteSearchEventsAsync(
        SearchQuery query,
        SearchTextMatcher matcher,
        ChannelWriter<ProducedSearchEvent> writer,
        CancellationToken cancellationToken,
        CancellationToken consumerCancellationToken,
        CancellationToken workCancellationToken)
    {
        var filesScanned = 0;
        var skippedLargeFiles = 0;
        var unreadableFiles = 0;
        var wasCancelled = false;

        if (consumerCancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await WriteEventAsync(
                writer,
                CreateSummary(query, filesScanned, skippedLargeFiles, unreadableFiles, wasCancelled: true),
                consumerCancellationToken).ConfigureAwait(false);
            return;
        }

        if (query.Mode == SearchMode.FileName)
        {
            foreach (var path in EnumerateFiles(query.Root, query.Extensions, workCancellationToken, () => unreadableFiles++))
            {
                if (consumerCancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }

                filesScanned++;
                var fileName = Path.GetFileName(path);
                var match = matcher.Match(fileName);
                if (match.Success)
                {
                    await WriteEventAsync(
                        writer,
                        new SearchMatch(
                            query.RequestId,
                            path,
                            null,
                            fileName,
                            match.Index,
                            match.Length),
                        consumerCancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (query.Mode == SearchMode.Body)
        {
            foreach (var path in EnumerateFiles(query.Root, query.Extensions, workCancellationToken, () => unreadableFiles++))
            {
                if (consumerCancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }

                filesScanned++;
                long fileLength;
                try
                {
                    fileLength = getFileLength(path);
                }
                catch (UnauthorizedAccessException)
                {
                    unreadableFiles++;
                    continue;
                }
                catch (IOException)
                {
                    unreadableFiles++;
                    continue;
                }

                if (fileLength > query.MaxBodyBytes)
                {
                    skippedLargeFiles++;
                    continue;
                }

                LoadedDocument document;
                try
                {
                    document = await loadDocument(path, workCancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (consumerCancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    unreadableFiles++;
                    continue;
                }
                catch (IOException)
                {
                    unreadableFiles++;
                    continue;
                }
                catch (DecoderFallbackException)
                {
                    unreadableFiles++;
                    continue;
                }

                try
                {
                    foreach (var match in matcher.MatchLines(document.Text, workCancellationToken))
                    {
                        if (consumerCancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            wasCancelled = true;
                            break;
                        }

                        await WriteEventAsync(
                            writer,
                            new SearchMatch(
                                query.RequestId,
                                path,
                                match.LineNumber,
                                match.Preview,
                                match.MatchStart,
                                match.MatchLength),
                            consumerCancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (consumerCancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }
            }
        }

        if (consumerCancellationToken.IsCancellationRequested)
        {
            return;
        }

        await WriteEventAsync(
            writer,
            CreateSummary(
                query,
                filesScanned,
                skippedLargeFiles,
                unreadableFiles,
                wasCancelled || cancellationToken.IsCancellationRequested),
            consumerCancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(
        ChannelWriter<ProducedSearchEvent> writer,
        SearchEvent searchEvent,
        CancellationToken cancellationToken)
    {
        var produced = new ProducedSearchEvent(
            searchEvent,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await writer.WriteAsync(produced, cancellationToken).ConfigureAwait(false);
        await produced.Acknowledged.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<string> EnumerateFiles(
        string root,
        IReadOnlySet<string> extensions,
        CancellationToken cancellationToken,
        Action recordUnreadableFile)
    {
        var pendingDirectories = new Stack<DirectoryInfo>();
        pendingDirectories.Push(new DirectoryInfo(root));

        while (pendingDirectories.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var directory = pendingDirectories.Pop();
            if (!TryGetAttributes(directory, out var directoryAttributes))
            {
                recordUnreadableFile();
                continue;
            }

            if (!FolderTreeService.ShouldTraverse(directoryAttributes))
            {
                continue;
            }

            var openResult = TryOpenEnumerator(directory, cancellationToken, out var entries);
            if (openResult == EnumerationResult.Cancelled)
            {
                yield break;
            }

            if (openResult == EnumerationResult.Unreadable)
            {
                recordUnreadableFile();
                continue;
            }

            using (entries)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var enumerationResult = TryMoveNext(entries, cancellationToken, out var entry);
                    if (enumerationResult == EnumerationResult.Completed)
                    {
                        break;
                    }

                    if (enumerationResult == EnumerationResult.Cancelled)
                    {
                        yield break;
                    }

                    if (enumerationResult == EnumerationResult.Unreadable)
                    {
                        recordUnreadableFile();
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }

                    var currentEntry = entry!;
                    if (!TryGetAttributes(currentEntry, out var attributes))
                    {
                        recordUnreadableFile();
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (FolderTreeService.ShouldTraverse(attributes))
                        {
                            pendingDirectories.Push(new DirectoryInfo(currentEntry.FullName));
                        }

                        continue;
                    }

                    if (extensions.Contains(Path.GetExtension(currentEntry.Name)))
                    {
                        yield return currentEntry.FullName;
                    }
                }
            }
        }
    }

    private bool TryGetAttributes(FileSystemInfo entry, out FileAttributes attributes)
    {
        try
        {
            attributes = getAttributes(entry);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
        catch (IOException)
        {
            attributes = default;
            return false;
        }
    }

    private EnumerationResult TryOpenEnumerator(
        DirectoryInfo directory,
        CancellationToken cancellationToken,
        out IEnumerator<FileSystemInfo> entries)
    {
        try
        {
            entries = enumerateEntries(directory, cancellationToken).GetEnumerator();
            return EnumerationResult.Entry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entries = null!;
            return EnumerationResult.Cancelled;
        }
        catch (UnauthorizedAccessException)
        {
            entries = null!;
            return EnumerationResult.Unreadable;
        }
        catch (IOException)
        {
            entries = null!;
            return EnumerationResult.Unreadable;
        }
    }

    private static EnumerationResult TryMoveNext(
        IEnumerator<FileSystemInfo> entries,
        CancellationToken cancellationToken,
        out FileSystemInfo? entry)
    {
        try
        {
            if (entries.MoveNext())
            {
                entry = entries.Current;
                return EnumerationResult.Entry;
            }

            entry = null;
            return EnumerationResult.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entry = null;
            return EnumerationResult.Cancelled;
        }
        catch (UnauthorizedAccessException)
        {
            entry = null;
            return EnumerationResult.Unreadable;
        }
        catch (IOException)
        {
            entry = null;
            return EnumerationResult.Unreadable;
        }
    }

    private static SearchSummary CreateSummary(
        SearchQuery query,
        int filesScanned,
        int skippedLargeFiles,
        int unreadableFiles,
        bool wasCancelled)
    {
        return new SearchSummary(
            query.RequestId,
            filesScanned,
            skippedLargeFiles,
            unreadableFiles,
            wasCancelled);
    }

    private enum EnumerationResult
    {
        Entry,
        Completed,
        Unreadable,
        Cancelled
    }

    private sealed record ProducedSearchEvent(
        SearchEvent Event,
        TaskCompletionSource Acknowledged);
}
