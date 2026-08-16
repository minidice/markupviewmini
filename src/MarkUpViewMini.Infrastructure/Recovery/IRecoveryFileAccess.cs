namespace MarkUpViewMini.Infrastructure.Recovery;

internal interface IRecoveryFileAccess
{
    void EnsureDirectory(string path);

    IReadOnlyList<string> EnumerateFiles(string directory, string pattern);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    string CreateTemporaryPath(string targetPath);

    Task CreateNewAsync(string path, CancellationToken cancellationToken);

    Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);

    void FlushToDisk(string path);

    bool Exists(string path);

    void Replace(string sourcePath, string destinationPath);

    void Move(string sourcePath, string destinationPath);

    void DeleteIfExists(string path);
}
