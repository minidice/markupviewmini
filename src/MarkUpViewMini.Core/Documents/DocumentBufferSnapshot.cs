namespace MarkUpViewMini.Core.Documents;

public sealed record DocumentBufferSnapshot(
    Guid TabId,
    string Path,
    string Text,
    long Revision,
    bool IsDirty,
    EncodingDescriptor Encoding,
    NewLineKind NewLine,
    string PreferredNewLine,
    DiskFileVersion BaselineVersion);
