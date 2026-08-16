namespace MarkUpViewMini.Core.Documents;

public sealed class MarkdownDocumentProvider : IDocumentFormatProvider
{
    public DocumentFormatDescriptor Descriptor { get; } = new(
        "markdown",
        [".md", ".markdown"],
        DocumentCapabilities.Read | DocumentCapabilities.Edit |
        DocumentCapabilities.FileNameSearch | DocumentCapabilities.BodySearch |
        DocumentCapabilities.InternalLinks);
}
