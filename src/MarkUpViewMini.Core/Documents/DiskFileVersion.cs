namespace MarkUpViewMini.Core.Documents;

public sealed record DiskFileVersion(
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256);
