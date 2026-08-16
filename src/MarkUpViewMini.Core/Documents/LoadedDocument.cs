namespace MarkUpViewMini.Core.Documents;

public sealed record LoadedDocument(
    string Text,
    EncodingDescriptor Encoding,
    NewLineKind NewLine,
    string PreferredNewLine,
    DiskFileVersion Version);
