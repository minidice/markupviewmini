namespace MarkUpViewMini.Core.Mermaid;

public sealed record MermaidBlockSnapshot(
    Guid SessionId,
    Guid TabId,
    long DocumentRevision,
    int From,
    int To,
    string Source,
    string SourceHash);
