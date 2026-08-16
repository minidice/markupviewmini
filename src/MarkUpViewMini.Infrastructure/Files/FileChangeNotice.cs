using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Infrastructure.Files;

public enum FileChangeKind
{
    Changed,
    Deleted,
    Renamed,
    Inaccessible,
}

public sealed record FileChangeNotice
{
    private FileChangeNotice(
        string path,
        FileChangeKind kind,
        LoadedDocument? document,
        string? relatedPath,
        string? errorType,
        string displayMessage)
    {
        Path = System.IO.Path.GetFullPath(path);
        Kind = kind;
        Document = document;
        RelatedPath = relatedPath is null ? null : System.IO.Path.GetFullPath(relatedPath);
        ErrorType = errorType;
        DisplayMessage = displayMessage;
    }

    public string Path { get; }

    public FileChangeKind Kind { get; }

    public LoadedDocument? Document { get; }

    public DiskFileVersion? Version => Document?.Version;

    public string? RelatedPath { get; }

    public string? ErrorType { get; }

    public string DisplayMessage { get; }

    public static FileChangeNotice Changed(string path, LoadedDocument document) =>
        new(
            path,
            FileChangeKind.Changed,
            document ?? throw new ArgumentNullException(nameof(document)),
            null,
            null,
            "파일이 외부에서 변경되었습니다.");

    public static FileChangeNotice Deleted(string path) =>
        new(path, FileChangeKind.Deleted, null, null, null, "원본 파일이 삭제되었습니다.");

    public static FileChangeNotice Renamed(string path, string newPath) =>
        new(path, FileChangeKind.Renamed, null, newPath, null, "원본 파일의 이름 또는 위치가 변경되었습니다.");

    public static FileChangeNotice Inaccessible(string path, string errorType) =>
        new(
            path,
            FileChangeKind.Inaccessible,
            null,
            null,
            string.IsNullOrWhiteSpace(errorType) ? nameof(IOException) : errorType,
            "원본 파일을 읽을 수 없습니다.");
}
