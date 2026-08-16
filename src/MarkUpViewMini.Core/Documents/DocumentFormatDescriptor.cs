namespace MarkUpViewMini.Core.Documents;

public sealed record DocumentFormatDescriptor(
    string Id,
    IReadOnlyList<string> Extensions,
    DocumentCapabilities Capabilities);
