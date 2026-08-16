namespace MarkUpViewMini.Infrastructure.Recovery;

internal sealed class PhysicalRecoveryFileAccess : IRecoveryFileAccess
{
    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public IReadOnlyList<string> EnumerateFiles(string directory, string pattern) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            : [];

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

    public void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
