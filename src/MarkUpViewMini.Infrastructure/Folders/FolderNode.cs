namespace MarkUpViewMini.Infrastructure.Folders;

public sealed record FolderNode(
    string Name,
    string FullPath,
    bool IsDirectory,
    IReadOnlyList<FolderNode> Children,
    string? Error);
