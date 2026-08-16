using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Infrastructure.Files;

internal interface IFileAccess
{
    Task<DiskFileVersion?> ReadVersionAsync(string path, CancellationToken cancellationToken);

    string CreateTemporaryPath(string targetPath);

    Task CreateNewAsync(string path, CancellationToken cancellationToken);

    Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);

    void FlushToDisk(string path);

    void Replace(string sourcePath, string destinationPath);

    void Move(string sourcePath, string destinationPath);

    void DeleteIfExists(string path);

    DateTime GetLastWriteTimeUtc(string path);
}
