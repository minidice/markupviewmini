using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.Infrastructure.State;

public sealed record SessionV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<SessionWindowV1> Windows { get; init; } = [];

    public static SessionV1 CreateDefault() => new();
}

public sealed record SessionWindowV1
{
    public Guid WindowId { get; init; }

    public IReadOnlyList<SessionTabV1> Tabs { get; init; } = [];

    public Guid? ActiveTabId { get; init; }

    public string? RootPath { get; init; }

    public SessionWindowLayoutV1 Layout { get; init; } = SessionWindowLayoutV1.CreateDefault();
}

public sealed record SessionTabV1
{
    public Guid TabId { get; init; }

    public string Path { get; init; } = string.Empty;

    public DocumentMode Mode { get; init; } = DocumentMode.Read;

    public IReadOnlyList<SessionNavigationEntryV1> History { get; init; } = [];

    public int HistoryIndex { get; init; } = -1;

    public SessionEditorHintsV1 Hints { get; init; } = SessionEditorHintsV1.CreateDefault();
}

public sealed record SessionNavigationEntryV1(
    string Path,
    int? Line,
    string? Anchor,
    DocumentMode Mode,
    double? ScrollOffset);

public sealed record SessionEditorHintsV1(
    int SelectionAnchor,
    int SelectionHead,
    double ScrollTop,
    double SplitRatio)
{
    public static SessionEditorHintsV1 CreateDefault() => new(0, 0, 0, 0.5);
}

public sealed record SessionWindowLayoutV1(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized)
{
    public static SessionWindowLayoutV1 CreateDefault() => new(100, 100, 1024, 768, false);
}

public sealed record SessionLoadResult(SessionV1 Session, int SkippedEntries);
