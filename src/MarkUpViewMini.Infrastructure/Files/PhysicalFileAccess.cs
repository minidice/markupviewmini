using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Infrastructure.Files;

internal sealed class PhysicalFileAccess : IFileAccess
{
    public async Task<DiskFileVersion?> ReadVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > Array.MaxLength)
        {
            throw new IOException("The file is too large to version in memory.");
        }

        var lastWriteBefore = File.GetLastWriteTimeUtc(path);
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var lastWriteAfter = File.GetLastWriteTimeUtc(path);
        if (stream.Length != bytes.LongLength || lastWriteBefore != lastWriteAfter)
        {
            throw new IOException("The file changed while its version was being read.");
        }

        return DocumentFileService.CreateVersion(bytes, lastWriteAfter);
    }

    public string CreateTemporaryPath(string targetPath)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget) ??
            throw new ArgumentException("The target must have a parent directory.", nameof(targetPath));
        return Path.Combine(directory, $".{Path.GetFileName(fullTarget)}.{Guid.NewGuid():N}.tmp");
    }

    public async Task CreateNewAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
}
