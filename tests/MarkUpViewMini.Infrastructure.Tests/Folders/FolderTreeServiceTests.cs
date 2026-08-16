using MarkUpViewMini.Infrastructure.Folders;

namespace MarkUpViewMini.Infrastructure.Tests.Folders;

public sealed class FolderTreeServiceTests : IDisposable
{
    private readonly string _root;

    public FolderTreeServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            nameof(FolderTreeServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task BuildAsync_includes_registered_documents_recursively()
    {
        // Break caught: filtering only the root or not filtering extensions would expose nested Markdown incorrectly or include notes.txt.
        await File.WriteAllTextAsync(Path.Combine(_root, "guide.md"), "guide");
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "notes");
        var chapter = Directory.CreateDirectory(Path.Combine(_root, "chapter"));
        await File.WriteAllTextAsync(Path.Combine(chapter.FullName, "two.markdown"), "two");
        var service = new FolderTreeService();

        var tree = await service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);

        Assert.Contains(tree.Children, node => node.FullPath.EndsWith("guide.md", StringComparison.Ordinal));
        Assert.Contains(
            tree.Children.Single(node => node.Name == "chapter").Children,
            node => node.FullPath.EndsWith("two.markdown", StringComparison.Ordinal));
        Assert.DoesNotContain(tree.Children, node => node.FullPath.EndsWith("notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_orders_directories_before_files_and_names_case_insensitively()
    {
        // Break caught: alphabetical ordering without a directory-first group or case-sensitive comparison produces an unstable navigation tree.
        Directory.CreateDirectory(Path.Combine(_root, "zebra"));
        Directory.CreateDirectory(Path.Combine(_root, "Apple"));
        await File.WriteAllTextAsync(Path.Combine(_root, "zebra.md"), "zebra");
        await File.WriteAllTextAsync(Path.Combine(_root, "apple.md"), "apple");
        var service = new FolderTreeService();

        var tree = await service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);

        Assert.Equal(["Apple", "zebra", "apple.md", "zebra.md"], tree.Children.Select(node => node.Name));
    }

    [Fact]
    public async Task BuildAsync_returns_an_error_node_when_a_child_directory_cannot_be_enumerated()
    {
        // Break caught: allowing a child enumeration exception to abort the whole tree removes the inaccessible folder and its visible error state.
        var unavailable = Directory.CreateDirectory(Path.Combine(_root, "unavailable"));
        var service = new FolderTreeService(directory =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(directory.FullName, unavailable.FullName))
            {
                throw new UnauthorizedAccessException("Access denied for test.");
            }

            return directory.EnumerateFileSystemInfos();
        });

        var tree = await service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);

        var errorNode = Assert.Single(tree.Children, node => node.Name == "unavailable");
        Assert.True(errorNode.IsDirectory);
        Assert.Equal(unavailable.FullName, errorNode.FullPath);
        Assert.Equal("Access denied for test.", errorNode.Error);
        Assert.Empty(errorNode.Children);
    }

    [Fact]
    public void ShouldTraverse_returns_false_for_reparse_point_attributes()
    {
        // Break caught: treating a reparse point as a traversable directory can leave tree enumeration in a filesystem cycle.
        var attributes = FileAttributes.Directory | FileAttributes.ReparsePoint;

        var shouldTraverse = FolderTreeService.ShouldTraverse(attributes);

        Assert.False(shouldTraverse);
    }

    [Fact]
    public async Task BuildAsync_retains_a_reparse_point_directory_without_enumerating_it()
    {
        // Break caught: recursing into a reparse point can leave tree enumeration in a filesystem cycle instead of retaining it as a leaf node.
        var reparsePath = Path.Combine(_root, "linked");
        var enumeratedDirectories = new List<string>();
        var service = new FolderTreeService(
            directory =>
            {
                enumeratedDirectories.Add(directory.FullName);
                return directory.FullName == _root
                    ? [new FileInfo(reparsePath)]
                    : directory.EnumerateFileSystemInfos();
            },
            entry => entry.FullName == reparsePath
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : entry.Attributes);

        var tree = await service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);

        var node = Assert.Single(tree.Children);
        Assert.Equal("linked", node.Name);
        Assert.True(node.IsDirectory);
        Assert.Empty(node.Children);
        Assert.Equal([_root], enumeratedDirectories);
    }

    [Fact]
    public async Task BuildAsync_retains_a_reparse_point_root_without_enumerating_it()
    {
        // Break caught: entering a selected root reparse point follows it even though reparse-point directories must remain untraversed.
        var enumerationRequests = 0;
        var service = new FolderTreeService(
            _ =>
            {
                enumerationRequests++;
                return [];
            },
            _ => FileAttributes.Directory | FileAttributes.ReparsePoint);

        var tree = await service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);

        Assert.Equal(new DirectoryInfo(_root).Name, tree.Name);
        Assert.True(tree.IsDirectory);
        Assert.Empty(tree.Children);
        Assert.Null(tree.Error);
        Assert.Equal(0, enumerationRequests);
    }

    [Fact]
    public async Task BuildAsync_orders_case_variant_names_with_an_ordinal_tie_breaker()
    {
        // Break caught: names equal under case-insensitive comparison retain filesystem enumeration order rather than a deterministic ordinal order.
        var lowerCase = new FileInfo(Path.Combine(_root, "a.md"));
        var upperCase = new FileInfo(Path.Combine(_root, "A.md"));
        var service = new FolderTreeService(
            _ => [lowerCase, upperCase],
            _ => FileAttributes.Normal);

        var tree = await service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);

        Assert.Equal(["A.md", "a.md"], tree.Children.Select(node => node.Name));
    }

    [Fact]
    public async Task BuildAsync_returns_an_error_node_when_entry_metadata_cannot_be_read()
    {
        // Break caught: a metadata access failure that escapes the entry boundary aborts the whole tree instead of exposing the failed entry.
        var unreadablePath = Path.Combine(_root, "unreadable.md");
        var service = new FolderTreeService(
            _ => [new FileInfo(unreadablePath)],
            entry => entry.FullName == unreadablePath
                ? throw new IOException("Metadata unavailable for test.")
                : FileAttributes.Directory);

        var tree = await service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);

        var node = Assert.Single(tree.Children);
        Assert.Equal("unreadable.md", node.Name);
        Assert.False(node.IsDirectory);
        Assert.Equal("Metadata unavailable for test.", node.Error);
    }

    [Fact]
    public async Task BuildAsync_propagates_cancellation()
    {
        // Break caught: swallowing cancellation turns a requested stop into a completed tree build.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new FolderTreeService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.BuildAsync(_root, MarkdownExtensions, cancellation.Token));
    }

    [Fact]
    public async Task BuildAsync_returns_before_blocked_physical_enumeration_runs_on_a_worker()
    {
        // Break caught: recursively enumerating before BuildAsync returns blocks the WPF dispatcher that initiated a refresh.
        var callerThread = 0;
        var enumerationThread = 0;
        var callReturnedBeforeRelease = false;
        using var enumerationStarted = new ManualResetEventSlim();
        using var callReturned = new ManualResetEventSlim();
        using var releaseEnumeration = new ManualResetEventSlim();
        var service = new FolderTreeService(_ => Enumerate());
        Task<FolderNode>? build = null;
        var observer = Task.Run(() =>
        {
            Assert.True(enumerationStarted.Wait(TimeSpan.FromSeconds(5)));
            callReturnedBeforeRelease = callReturned.Wait(TimeSpan.FromSeconds(1));
            releaseEnumeration.Set();
        });
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            build = service.BuildAsync(_root, MarkdownExtensions, CancellationToken.None);
            callReturned.Set();
        });

        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        await observer;
        Assert.NotNull(build);
        await build;

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
    public async Task BuildAsync_can_be_cancelled_while_physical_enumeration_is_in_progress()
    {
        // Break caught: a refresh that owns the caller thread cannot receive cancellation until recursive enumeration has already completed.
        using var enumerationStarted = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var service = new FolderTreeService(_ => Enumerate());

        var build = service.BuildAsync(_root, MarkdownExtensions, cancellation.Token);
        Assert.True(enumerationStarted.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);

        IEnumerable<FileSystemInfo> Enumerate()
        {
            enumerationStarted.Set();
            Assert.True(SpinWait.SpinUntil(
                () => cancellation.IsCancellationRequested,
                TimeSpan.FromSeconds(5)));
            yield return new FileInfo(Path.Combine(_root, "cancelled.md"));
        }
    }

    [Fact]
    public async Task BuildAsync_stops_before_reading_an_entry_after_cancellation_is_observed()
    {
        // Break caught: materializing a directory before checking cancellation keeps reading the entry after cancellation is observed.
        var first = new FileInfo(Path.Combine(_root, "first.md"));
        var second = new FileInfo(Path.Combine(_root, "second.md"));
        var third = new FileInfo(Path.Combine(_root, "third.md"));
        var yieldedEntries = 0;
        using var cancellation = new CancellationTokenSource();
        var service = new FolderTreeService(_ => Enumerate());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.BuildAsync(_root, MarkdownExtensions, cancellation.Token));

        Assert.Equal(2, yieldedEntries);

        IEnumerable<FileSystemInfo> Enumerate()
        {
            yieldedEntries++;
            yield return first;
            cancellation.Cancel();
            yieldedEntries++;
            yield return second;
            yieldedEntries++;
            yield return third;
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private static IReadOnlySet<string> MarkdownExtensions { get; } =
        new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase);

}
