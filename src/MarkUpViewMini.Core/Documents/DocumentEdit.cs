namespace MarkUpViewMini.Core.Documents;

public sealed record DocumentEdit(
    long ExpectedRevision,
    IReadOnlyList<TextChange> Changes);
