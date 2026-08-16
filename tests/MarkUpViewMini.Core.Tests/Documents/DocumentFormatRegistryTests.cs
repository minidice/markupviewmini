using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Core.Tests.Documents;

public sealed class DocumentFormatRegistryTests
{
    [Theory]
    [InlineData("guide.md")]
    [InlineData("GUIDE.MARKDOWN")]
    public void Resolve_returns_markdown_provider_for_registered_extensions(string path)
    {
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);

        Assert.Equal("markdown", registry.Resolve(path).Descriptor.Id);
    }

    [Fact]
    public void Resolve_rejects_unregistered_extension()
    {
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);

        Assert.Throws<NotSupportedException>(() => registry.Resolve("page.html"));
    }

    [Fact]
    public void Constructor_rejects_duplicate_extensions_case_insensitively()
    {
        var firstProvider = new TestDocumentProvider("first", [".md"]);
        var secondProvider = new TestDocumentProvider("second", [".MD"]);

        Assert.Throws<ArgumentException>(() => new DocumentFormatRegistry([firstProvider, secondProvider]));
    }

    [Fact]
    public void Markdown_provider_exposes_its_required_descriptor()
    {
        var descriptor = new MarkdownDocumentProvider().Descriptor;

        Assert.Equal("markdown", descriptor.Id);
        Assert.Equal([".md", ".markdown"], descriptor.Extensions);
        Assert.Equal(
            DocumentCapabilities.Read | DocumentCapabilities.Edit |
            DocumentCapabilities.FileNameSearch | DocumentCapabilities.BodySearch |
            DocumentCapabilities.InternalLinks,
            descriptor.Capabilities);
    }

    [Fact]
    public void Searchable_extensions_are_derived_from_registered_provider_capabilities()
    {
        // Break caught: UI composition can fall back to repeated Markdown literals and ignore later registered formats.
        var registry = new DocumentFormatRegistry(
        [
            new TestDocumentProvider("markdown", [".md", ".markdown"], DocumentCapabilities.Read | DocumentCapabilities.FileNameSearch | DocumentCapabilities.BodySearch),
            new TestDocumentProvider("html", [".html", ".htm"], DocumentCapabilities.Read | DocumentCapabilities.FileNameSearch),
            new TestDocumentProvider("image", [".png"], DocumentCapabilities.Read),
        ]);

        Assert.Equal([".htm", ".html", ".markdown", ".md", ".png"], registry.GetExtensions(DocumentCapabilities.Read));
        Assert.Equal([".htm", ".html", ".markdown", ".md"], registry.GetExtensions(DocumentCapabilities.FileNameSearch));
        Assert.Equal([".markdown", ".md"], registry.GetExtensions(DocumentCapabilities.BodySearch));
    }

    private sealed class TestDocumentProvider(
        string id,
        IReadOnlyList<string> extensions,
        DocumentCapabilities capabilities = DocumentCapabilities.None) : IDocumentFormatProvider
    {
        public DocumentFormatDescriptor Descriptor { get; } = new(
            id,
            extensions,
            capabilities);
    }
}
