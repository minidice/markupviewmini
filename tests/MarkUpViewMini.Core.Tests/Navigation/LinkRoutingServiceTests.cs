using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;

namespace MarkUpViewMini.Core.Tests.Navigation;

public sealed class LinkRoutingServiceTests
{
    private readonly LinkRoutingService service = new(
        new DocumentFormatRegistry([new MarkdownDocumentProvider()]));

    [Theory]
    [InlineData("chapter.md", LinkRouteKind.InternalCurrentTab)]
    [InlineData("https://example.com", LinkRouteKind.DefaultBrowser)]
    [InlineData("image.png", LinkRouteKind.WindowsAssociatedApp)]
    public void Route_uses_approved_default_policy(string link, LinkRouteKind expected)
    {
        var route = service.Route(link, @"C:\Docs\guide.md", LinkOpenDisposition.Default);

        Assert.Equal(expected, route.Kind);
    }

    [Fact]
    public void Route_resolves_a_supported_local_target_with_line_and_anchor()
    {
        var route = service.Route(
            @"chapters\setup.md:17#install",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default);

        Assert.Equal(LinkRouteKind.InternalCurrentTab, route.Kind);
        Assert.Equal(@"C:\Docs\chapters\setup.md", route.Target);
        Assert.Equal(17, route.Line);
        Assert.Equal("install", route.Anchor);
    }

    [Fact]
    public void Route_treats_a_terminal_numeric_suffix_as_a_line_not_a_uri_scheme()
    {
        var route = service.Route(
            "chapter.md:27#details",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default);

        Assert.Equal(LinkRouteKind.InternalCurrentTab, route.Kind);
        Assert.Equal(@"C:\Docs\chapter.md", route.Target);
        Assert.Equal(27, route.Line);
        Assert.Equal("details", route.Anchor);
    }

    [Fact]
    public void Route_maps_a_same_document_anchor_to_the_current_document()
    {
        var route = service.Route(
            "#install",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default);

        Assert.Equal(LinkRouteKind.InternalCurrentTab, route.Kind);
        Assert.Equal(@"C:\Docs\guide.md", route.Target);
        Assert.Null(route.Line);
        Assert.Equal("install", route.Anchor);
    }

    [Fact]
    public void Route_maps_new_tab_for_a_supported_markdown_target()
    {
        var route = service.Route(
            "chapter.markdown",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.NewTab);

        Assert.Equal(LinkRouteKind.InternalNewTab, route.Kind);
        Assert.Equal(@"C:\Docs\chapter.markdown", route.Target);
    }

    [Fact]
    public void Route_rejects_explicit_internal_open_for_an_unsupported_format()
    {
        Assert.Throws<NotSupportedException>(() => service.Route(
            "diagram.png",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Internal));
    }

    [Fact]
    public void Route_preserves_a_web_query_and_fragment_verbatim()
    {
        const string link = "https://example.com/search?q=a%20b&lang=en#part%202";

        var route = service.Route(link, @"C:\Docs\guide.md", LinkOpenDisposition.Default);

        Assert.Equal(LinkRouteKind.DefaultBrowser, route.Kind);
        Assert.Equal(link, route.Target);
        Assert.Null(route.Line);
        Assert.Null(route.Anchor);
    }

    [Fact]
    public void Route_decodes_a_percent_encoded_local_path_before_resolving_it()
    {
        var route = service.Route(
            "chapters/chapter%20one.md#install",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default);

        Assert.Equal(@"C:\Docs\chapters\chapter one.md", route.Target);
        Assert.Equal("install", route.Anchor);
    }

    [Fact]
    public void Route_keeps_a_percent_encoded_hash_in_the_local_file_name()
    {
        var route = service.Route(
            "chapter%23one.md",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default);

        Assert.Equal(LinkRouteKind.InternalCurrentTab, route.Kind);
        Assert.Equal(@"C:\Docs\chapter#one.md", route.Target);
        Assert.Null(route.Anchor);
    }

    [Fact]
    public void Route_does_not_decode_percent_sequences_from_the_current_document_directory()
    {
        var route = service.Route(
            "chapter.md",
            @"C:\Docs\literal%20folder\guide.md",
            LinkOpenDisposition.Default);

        Assert.Equal(@"C:\Docs\literal%20folder\chapter.md", route.Target);
    }

    [Fact]
    public void Route_maps_windows_default_to_the_associated_app_even_for_markdown()
    {
        var route = service.Route(
            "chapter.md",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.WindowsDefault);

        Assert.Equal(LinkRouteKind.WindowsAssociatedApp, route.Kind);
        Assert.Equal(@"C:\Docs\chapter.md", route.Target);
    }

    [Theory]
    [InlineData("mailto:person@example.com")]
    [InlineData("ftp://example.com/guide.md")]
    [InlineData("javascript:123")]
    [InlineData("custom.scheme:123")]
    [InlineData("//example.com/guide.md")]
    public void Route_rejects_unapproved_uri_forms(string link)
    {
        Assert.Throws<NotSupportedException>(() => service.Route(
            link,
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default));
    }

    [Theory]
    [InlineData("//server/share/chapter.md")]
    [InlineData("%2F%2Fserver/share/chapter.md")]
    [InlineData(@"\\server\share\chapter.md")]
    [InlineData("%5C%5Cserver%5Cshare%5Cchapter.md")]
    public void Route_rejects_raw_or_encoded_unc_targets(string link)
    {
        Assert.Throws<NotSupportedException>(() => service.Route(
            link,
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default));
    }

    [Theory]
    [InlineData("/chapter.md")]
    [InlineData("%2Fchapter.md")]
    [InlineData(@"\chapter.md")]
    [InlineData("%5Cchapter.md")]
    [InlineData(@"C:\Docs\chapter.md")]
    [InlineData("C%3A%5CDocs%5Cchapter.md")]
    [InlineData("C:/Docs/chapter.md")]
    [InlineData("C%3A%2FDocs%2Fchapter.md")]
    public void Route_rejects_raw_or_encoded_rooted_local_targets(string link)
    {
        Assert.Throws<FormatException>(() => service.Route(
            link,
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default));
    }

    [Theory]
    [InlineData("chapter.md?x=1")]
    [InlineData("chapter.md%3Fx=1")]
    public void Route_rejects_raw_or_encoded_local_queries(string link)
    {
        Assert.Throws<FormatException>(() => service.Route(
            link,
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default));
    }

    [Theory]
    [InlineData("chapter.md", @"C:\Docs\chapter.md", null, null)]
    [InlineData("chapter%20one.md", @"C:\Docs\chapter one.md", null, null)]
    [InlineData("chapter%23one.md", @"C:\Docs\chapter#one.md", null, null)]
    [InlineData("chapter.md:17#install", @"C:\Docs\chapter.md", 17, "install")]
    [InlineData("chapter%252Fone.md", @"C:\Docs\chapter%2Fone.md", null, null)]
    public void Route_preserves_approved_relative_markdown_after_exactly_one_decode(
        string link,
        string expectedPath,
        int? expectedLine,
        string? expectedAnchor)
    {
        var route = service.Route(link, @"C:\Docs\guide.md", LinkOpenDisposition.Default);

        Assert.Equal(LinkRouteKind.InternalCurrentTab, route.Kind);
        Assert.Equal(expectedPath, route.Target);
        Assert.Equal(expectedLine, route.Line);
        Assert.Equal(expectedAnchor, route.Anchor);
    }

    [Theory]
    [InlineData("chapter%00.md")]
    [InlineData("chapter.md#part%00")]
    public void Route_rejects_nul_even_when_percent_encoded(string link)
    {
        Assert.Throws<FormatException>(() => service.Route(
            link,
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default));
    }

    [Fact]
    public void Route_rejects_a_relative_target_without_an_absolute_current_document()
    {
        Assert.Throws<FormatException>(() => service.Route(
            "chapter.md",
            "guide.md",
            LinkOpenDisposition.Default));
    }

    [Fact]
    public void Route_rejects_malformed_percent_encoding()
    {
        Assert.Throws<FormatException>(() => service.Route(
            "chapter%2.md",
            @"C:\Docs\guide.md",
            LinkOpenDisposition.Default));
    }
}
