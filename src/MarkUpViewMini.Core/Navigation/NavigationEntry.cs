using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.Core.Navigation;

public sealed record NavigationEntry(
    string Path,
    int? Line,
    string? Anchor,
    DocumentMode Mode,
    double? ScrollOffset);
