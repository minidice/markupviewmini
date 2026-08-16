using System.Text.Json;
using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebSurfaceNavigationTests
{
    private static readonly Guid RequestId = Guid.Parse("7f57288a-f8a1-43af-a934-0c1c59aaf8d1");
    private static readonly Guid WindowId = Guid.Parse("647963f8-c06c-4902-b8b3-2d6bb2ae4cc5");
    private static readonly Guid TabId = Guid.Parse("38f3ad74-c77b-498e-9211-bf728212c4a9");
    private static readonly WebResponseContext Response = new(RequestId, TabId, 7);

    [Fact]
    public void Outline_accepts_only_the_exact_activation_response_owner()
    {
        // Break caught: a stale activation response can replace the current document outline.
        var message = Envelope("document.outline");

        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(message, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { RequestId = Guid.NewGuid() }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { WindowId = Guid.NewGuid() }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { TabId = Guid.NewGuid() }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { DocumentRevision = 8 }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { Type = "unknown" }, Response, WindowId));
    }

    [Theory]
    [InlineData("link.open")]
    [InlineData("link.contextMenu")]
    public void Interactive_links_accept_a_fresh_request_only_for_the_exact_document_owner(string type)
    {
        // Break caught: requiring the activation request ID drops real interactions, while weak owner checks admit stale links.
        var message = Envelope(type) with { RequestId = Guid.NewGuid() };

        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(message, Response, WindowId));
        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(message with { RequestId = Guid.NewGuid() }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { WindowId = Guid.NewGuid() }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { TabId = Guid.NewGuid() }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { DocumentRevision = 8 }, Response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(message with { Type = "unknown" }, Response, WindowId));
    }

    [Fact]
    public void CreateGoToLineMessage_serializes_one_positive_line_with_current_owner()
    {
        // Break caught: line navigation can omit correlation, include extra fields, or accept non-positive lines.
        var json = WebViewPolicy.CreateGoToLineMessage(Response, WindowId, 18);

        Assert.Equal(
            "{\"version\":1,\"type\":\"navigation.goToLine\",\"requestId\":\"7f57288a-f8a1-43af-a934-0c1c59aaf8d1\",\"windowId\":\"647963f8-c06c-4902-b8b3-2d6bb2ae4cc5\",\"tabId\":\"38f3ad74-c77b-498e-9211-bf728212c4a9\",\"documentRevision\":7,\"payload\":{\"line\":18}}",
            json);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebViewPolicy.CreateGoToLineMessage(Response, WindowId, 0));
    }

    [Fact]
    public void CreateGoToAnchorMessage_serializes_one_nonempty_anchor_with_current_owner()
    {
        // Break caught: anchor navigation can lose exact characters or accept an empty target.
        var json = WebViewPolicy.CreateGoToAnchorMessage(Response, WindowId, "install & setup");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("navigation.goToAnchor", root.GetProperty("type").GetString());
        Assert.Equal("install & setup", root.GetProperty("payload").GetProperty("anchor").GetString());
        Assert.Single(root.GetProperty("payload").EnumerateObject());
        Assert.Throws<ArgumentException>(() =>
            WebViewPolicy.CreateGoToAnchorMessage(Response, WindowId, " "));
    }

    [Theory]
    [InlineData("find.open")]
    [InlineData("find.next")]
    [InlineData("find.previous")]
    [InlineData("find.close")]
    public void CreateFindMessage_serializes_only_approved_empty_payload_commands(string type)
    {
        // Break caught: find commands can use an unapproved type or carry unexpected payload data.
        var json = WebViewPolicy.CreateFindMessage(Response, WindowId, type);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
        Assert.Empty(document.RootElement.GetProperty("payload").EnumerateObject());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebViewPolicy.CreateFindMessage(Response, WindowId, "find.replace"));
    }

    private static WebMessageEnvelope Envelope(string type)
    {
        using var document = JsonDocument.Parse("{}");
        return new WebMessageEnvelope(
            1,
            type,
            RequestId,
            WindowId,
            TabId,
            7,
            document.RootElement.Clone());
    }
}
