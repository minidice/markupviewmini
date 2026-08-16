namespace MarkUpViewMini.Infrastructure.Folders;

public sealed class FolderTreeService
{
    private readonly Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateEntries;
    private readonly Func<FileSystemInfo, FileAttributes> getAttributes;

    public FolderTreeService()
        : this(
            directory => directory.EnumerateFileSystemInfos(),
            entry => File.GetAttributes(entry.FullName))
    {
    }

    internal FolderTreeService(Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateEntries)
        : this(enumerateEntries, entry => entry.Attributes)
    {
    }

    internal FolderTreeService(
        Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateEntries,
        Func<FileSystemInfo, FileAttributes> getAttributes)
    {
        this.enumerateEntries = enumerateEntries ?? throw new ArgumentNullException(nameof(enumerateEntries));
        this.getAttributes = getAttributes ?? throw new ArgumentNullException(nameof(getAttributes));
    }

    public Task<FolderNode> BuildAsync(
        string root,
        IReadOnlySet<string> extensions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(extensions);
        cancellationToken.ThrowIfCancellationRequested();

        var rootDirectory = new DirectoryInfo(root);
        return Task.Run(
            () => BuildRoot(rootDirectory, extensions, cancellationToken),
            cancellationToken);
    }

    private FolderNode BuildRoot(
        DirectoryInfo rootDirectory,
        IReadOnlySet<string> extensions,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ShouldTraverse(getAttributes(rootDirectory)))
            {
                return new FolderNode(
                    rootDirectory.Name,
                    rootDirectory.FullName,
                    true,
                    [],
                    null);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            return CreateDirectoryErrorNode(rootDirectory, exception);
        }
        catch (IOException exception)
        {
            return CreateDirectoryErrorNode(rootDirectory, exception);
        }

        return BuildDirectory(rootDirectory, extensions, cancellationToken);
    }

    internal static bool ShouldTraverse(FileAttributes attributes)
    {
        return (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private FolderNode BuildDirectory(
        DirectoryInfo directory,
        IReadOnlySet<string> extensions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<FileSystemInfo>();
        try
        {
            foreach (var entry in enumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            return CreateDirectoryErrorNode(directory, exception);
        }
        catch (IOException exception)
        {
            return CreateDirectoryErrorNode(directory, exception);
        }

        var children = new List<FolderNode>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = getAttributes(entry);
                if (IsDirectory(attributes))
                {
                    var childDirectory = new DirectoryInfo(entry.FullName);
                    children.Add(ShouldTraverse(attributes)
                        ? BuildDirectory(childDirectory, extensions, cancellationToken)
                        : new FolderNode(entry.Name, entry.FullName, true, [], null));
                }
                else if (extensions.Contains(Path.GetExtension(entry.Name)))
                {
                    children.Add(new FolderNode(entry.Name, entry.FullName, false, [], null));
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                children.Add(CreateEntryErrorNode(entry, exception));
            }
            catch (IOException exception)
            {
                children.Add(CreateEntryErrorNode(entry, exception));
            }
        }

        return new FolderNode(
            directory.Name,
            directory.FullName,
            true,
            children
                .OrderByDescending(node => node.IsDirectory)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name, StringComparer.Ordinal)
                .ToList(),
            null);
    }

    private static bool IsDirectory(FileAttributes attributes)
    {
        return (attributes & FileAttributes.Directory) != 0;
    }

    private static FolderNode CreateDirectoryErrorNode(DirectoryInfo directory, Exception exception)
    {
        return new FolderNode(directory.Name, directory.FullName, true, [], exception.Message);
    }

    private static FolderNode CreateEntryErrorNode(FileSystemInfo entry, Exception exception)
    {
        return new FolderNode(
            entry.Name,
            entry.FullName,
            entry is DirectoryInfo,
            [],
            exception.Message);
    }
}
