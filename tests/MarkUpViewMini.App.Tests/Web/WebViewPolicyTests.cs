using System.Text.Json;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebViewPolicyTests
{
    private static readonly Guid RequestId = Guid.Parse("7f57288a-f8a1-43af-a934-0c1c59aaf8d1");
    private static readonly Guid WindowId = Guid.Parse("647963f8-c06c-4902-b8b3-2d6bb2ae4cc5");
    private static readonly Guid TabId = Guid.Parse("38f3ad74-c77b-498e-9211-bf728212c4a9");

    [Fact]
    public void Recovery_activation_contains_the_exact_authoritative_buffer_and_serializable_hints()
    {
        var snapshot = new WebViewRecoveryTabSnapshot(
            TabId,
            @"D:\Docs\recovery.md",
            "authoritative\r\nbody",
            12,
            true,
            DocumentMode.Edit,
            new DocumentUiHints(3, 7, 42, 0.35, true, false, true),
            "\r\n");

        var json = WebViewPolicy.CreateDocumentRecoveryMessage(snapshot, RequestId, WindowId);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var payload = root.GetProperty("payload");
        Assert.Equal("document.activate", root.GetProperty("type").GetString());
        Assert.Equal(12, root.GetProperty("documentRevision").GetInt64());
        Assert.Equal("authoritative\r\nbody", payload.GetProperty("text").GetString());
        Assert.True(payload.GetProperty("dirty").GetBoolean());
        Assert.Equal("edit", payload.GetProperty("mode").GetString());
        Assert.Equal(3, payload.GetProperty("selection").GetProperty("anchor").GetInt32());
        Assert.Equal(7, payload.GetProperty("selection").GetProperty("head").GetInt32());
        Assert.Equal(42, payload.GetProperty("scrollTop").GetDouble());
        Assert.Equal(0.35, payload.GetProperty("splitRatio").GetDouble());
        Assert.True(payload.GetProperty("find").GetProperty("matchCase").GetBoolean());
        Assert.False(payload.GetProperty("find").GetProperty("wholeWord").GetBoolean());
        Assert.True(payload.GetProperty("find").GetProperty("useRegex").GetBoolean());
        Assert.Equal("\r\n", payload.GetProperty("preferredNewline").GetString());
    }

    [Fact]
    public void BuildBootstrapUri_uses_canonical_window_and_tab_context()
    {
        var windowId = Guid.Parse("A4DDFB62-0EAD-4BA4-8508-7AB329D784D6");
        var tabId = Guid.Parse("CE624D5E-1B98-4B9F-915C-2B7007872394");

        var uri = WebViewPolicy.BuildBootstrapUri(windowId, tabId);

        Assert.Equal(
            "https://app.markupviewmini.local/index.html?windowId=a4ddfb62-0ead-4ba4-8508-7ab329d784d6&tabId=ce624d5e-1b98-4b9f-915c-2b7007872394",
            uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://app.markupviewmini.local/index.html")]
    [InlineData("https://app.markupviewmini.local/dist/editor.js")]
    public void IsAllowedTopLevelNavigation_accepts_only_the_app_origin(string address)
    {
        Assert.True(WebViewPolicy.IsAllowedTopLevelNavigation(new Uri(address)));
    }

    [Theory]
    [InlineData("http://app.markupviewmini.local/index.html")]
    [InlineData("https://app.markupviewmini.local.evil.example/index.html")]
    [InlineData("https://example.com/index.html")]
    [InlineData("file:///C:/document.md")]
    [InlineData("about:blank")]
    [InlineData("https://app.markupviewmini.local:444/index.html")]
    [InlineData("https://user@app.markupviewmini.local/index.html")]
    public void IsAllowedTopLevelNavigation_rejects_non_app_origins(string address)
    {
        Assert.False(WebViewPolicy.IsAllowedTopLevelNavigation(new Uri(address)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BuildBootstrapUri_rejects_empty_context(bool emptyWindow, bool emptyTab)
    {
        var windowId = emptyWindow ? Guid.Empty : Guid.NewGuid();
        var tabId = emptyTab ? Guid.Empty : Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => WebViewPolicy.BuildBootstrapUri(windowId, tabId));
    }

    [Fact]
    public void IsMatchingReady_requires_ready_type_window_tab_and_bootstrap_revision()
    {
        var ready = Envelope("surface.ready", RequestId, WindowId, TabId, 0);

        Assert.True(WebViewPolicy.IsMatchingReady(ready, WindowId, TabId));
        Assert.False(WebViewPolicy.IsMatchingReady(ready with { Type = "document.rendered" }, WindowId, TabId));
        Assert.False(WebViewPolicy.IsMatchingReady(ready with { WindowId = Guid.NewGuid() }, WindowId, TabId));
        Assert.False(WebViewPolicy.IsMatchingReady(ready with { TabId = Guid.NewGuid() }, WindowId, TabId));
        Assert.False(WebViewPolicy.IsMatchingReady(ready with { DocumentRevision = 1 }, WindowId, TabId));
    }

    [Fact]
    public void IsCurrentActivationResponse_suppresses_stale_request_context_and_unknown_types()
    {
        var response = Envelope("document.rendered", RequestId, WindowId, TabId, 7);

        Assert.True(WebViewPolicy.IsCurrentActivationResponse(response, RequestId, WindowId, TabId, 7));
        Assert.True(WebViewPolicy.IsCurrentActivationResponse(response with { Type = "surface.error" }, RequestId, WindowId, TabId, 7));
        Assert.False(WebViewPolicy.IsCurrentActivationResponse(response with { Type = "link.open" }, RequestId, WindowId, TabId, 7));
        Assert.False(WebViewPolicy.IsCurrentActivationResponse(response with { RequestId = Guid.NewGuid() }, RequestId, WindowId, TabId, 7));
        Assert.False(WebViewPolicy.IsCurrentActivationResponse(response with { WindowId = Guid.NewGuid() }, RequestId, WindowId, TabId, 7));
        Assert.False(WebViewPolicy.IsCurrentActivationResponse(response with { TabId = Guid.NewGuid() }, RequestId, WindowId, TabId, 7));
        Assert.False(WebViewPolicy.IsCurrentActivationResponse(response with { DocumentRevision = 6 }, RequestId, WindowId, TabId, 7));
    }

    [Fact]
    public void Interactive_link_requests_accept_a_fresh_request_id_only_for_the_current_document_owner()
    {
        // Break caught: requiring the activation request ID silently drops every real click/context-menu envelope, which has a fresh ID.
        var response = new WebResponseContext(RequestId, TabId, 7);
        var interactiveRequest = Envelope("link.contextMenu", Guid.NewGuid(), WindowId, TabId, 7);

        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(interactiveRequest, response, WindowId));
        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(interactiveRequest with { Type = "link.open" }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(interactiveRequest with { WindowId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(interactiveRequest with { TabId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(interactiveRequest with { DocumentRevision = 8 }, response, WindowId));
    }

    [Fact]
    public void Mermaid_edit_request_dispatches_only_for_the_exact_current_document_owner()
    {
        // Break caught: the surface's correlation gate silently returned before its Mermaid dispatch branch.
        var response = new WebResponseContext(RequestId, TabId, 7);
        var request = Envelope("mermaid.editRequested", RequestId, WindowId, TabId, 7);

        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(request, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(request with { RequestId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(request with { WindowId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(request with { TabId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(request with { DocumentRevision = 8 }, response, WindowId));
    }

    [Fact]
    public void Mermaid_focus_completed_dispatches_only_for_the_exact_current_response()
    {
        // Break caught: native drops every DOM focus ack or admits an ack from a stale response.
        var response = new WebResponseContext(RequestId, TabId, 7);
        var ack = Envelope("mermaid.focusCompleted", RequestId, WindowId, TabId, 7);

        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(ack, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            ack with { RequestId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            ack with { WindowId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            ack with { TabId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            ack with { DocumentRevision = 8 }, response, WindowId));
    }

    [Fact]
    public void Document_asset_mapping_uses_only_the_canonical_document_directory()
    {
        var directory = WebViewPolicy.GetDocumentAssetsDirectory(@"C:\docs\guide\readme.md");

        Assert.Equal(@"C:\docs\guide", directory);
        Assert.Equal(
            "https://document-assets.local/",
            WebViewPolicy.DocumentAssetsBaseUri.AbsoluteUri);
        Assert.Throws<ArgumentException>(() =>
            WebViewPolicy.GetDocumentAssetsDirectory(@"relative\readme.md"));
    }

    [Theory]
    [InlineData("images/logo.png", @"C:\docs\guide\images\logo.png")]
    [InlineData("images/my%20logo.png", @"C:\docs\guide\images\my logo.png")]
    [InlineData("images/%ED%95%9C%EA%B8%80.png", @"C:\docs\guide\images\한글.png")]
    public void TryResolveDocumentAssetPath_accepts_only_canonically_contained_references(
        string reference,
        string expected)
    {
        Assert.True(WebViewPolicy.TryResolveDocumentAssetPath(
            @"C:\docs\guide\readme.md",
            reference,
            out var resolved));
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("%2e%2e/secret.png")]
    [InlineData("%252e%252e/secret.png")]
    [InlineData("images/../secret.png")]
    [InlineData("%5c%5cserver/share.png")]
    [InlineData("%255c%255cserver/share.png")]
    [InlineData("%2fsecret.png")]
    [InlineData("%252fsecret.png")]
    [InlineData("%252f%252fserver/share.png")]
    [InlineData("images%2flogo.png")]
    [InlineData("images%2Flogo.png")]
    [InlineData("images%5clogo.png")]
    [InlineData("images/my%2520logo.png")]
    [InlineData("images/%25ED%2595%259C%25EA%25B8%2580.png")]
    [InlineData("images/percent%2525name.png")]
    [InlineData("%43%3a/secret.png")]
    [InlineData("%2543%253a/secret.png")]
    [InlineData("images/%zz.png")]
    [InlineData("images/%C0%AF.png")]
    [InlineData("images/%2500.png")]
    [InlineData("images/%250a.png")]
    [InlineData(@"C:\secret.png")]
    [InlineData("/secret.png")]
    [InlineData("https://example.com/tracker.png")]
    [InlineData("//example.com/tracker.png")]
    public void TryResolveDocumentAssetPath_rejects_escape_absolute_and_untrusted_references(
        string reference)
    {
        Assert.False(WebViewPolicy.TryResolveDocumentAssetPath(
            @"C:\docs\guide\readme.md",
            reference,
            out var resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void Document_asset_request_policy_allows_a_valid_encoded_contained_image()
    {
        Assert.True(WebViewPolicy.TryResolveDocumentAssetRequest(
            @"C:\docs\guide\readme.md",
            "https://document-assets.local/images/my%20logo.png",
            out var resolved));
        Assert.Equal(@"C:\docs\guide\images\my logo.png", resolved);
    }

    [Fact]
    public void Document_asset_request_policy_allows_a_single_encoded_unicode_filename()
    {
        Assert.True(WebViewPolicy.TryResolveDocumentAssetRequest(
            @"C:\docs\guide\readme.md",
            "https://document-assets.local/images/%ED%95%9C%EA%B8%80.png",
            out var resolved));
        Assert.Equal(@"C:\docs\guide\images\한글.png", resolved);
    }

    [Theory]
    [InlineData("https://document-assets.local/%2e%2e/secret.png")]
    [InlineData("https://document-assets.local/%252e%252e/secret.png")]
    [InlineData("https://document-assets.local/%255c%255cserver/share.png")]
    [InlineData("https://document-assets.local/%252fsecret.png")]
    [InlineData("https://document-assets.local/%252f%252fserver/share.png")]
    [InlineData("https://document-assets.local/images%2flogo.png")]
    [InlineData("https://document-assets.local/images%2Flogo.png")]
    [InlineData("https://document-assets.local/images%5clogo.png")]
    [InlineData("https://document-assets.local/images/my%2520logo.png")]
    [InlineData("https://document-assets.local/images/%25ED%2595%259C%25EA%25B8%2580.png")]
    [InlineData("https://document-assets.local/images/percent%2525name.png")]
    [InlineData("https://document-assets.local/%2543%253a/secret.png")]
    [InlineData("https://document-assets.local/images/%zz.png")]
    [InlineData("https://document-assets.local/images/%C0%AF.png")]
    [InlineData("https://document-assets.local/images/%2500.png")]
    [InlineData("https://document-assets.local/images/../secret.png")]
    [InlineData("https://document-assets.local/images/logo.png?outside=1")]
    [InlineData("https://document-assets.local.evil.example/images/logo.png")]
    public void Document_asset_request_policy_denies_encoded_escape_and_untrusted_requests(
        string address)
    {
        Assert.False(WebViewPolicy.TryResolveDocumentAssetRequest(
            @"C:\docs\guide\readme.md",
            address,
            out var resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void Document_asset_origin_is_allowed_for_resources_but_not_top_level_navigation()
    {
        var asset = new Uri("https://document-assets.local/images/logo.png");

        Assert.True(WebViewPolicy.IsAllowedDocumentAssetUri(asset));
        Assert.False(WebViewPolicy.IsAllowedTopLevelNavigation(asset));
        Assert.False(WebViewPolicy.IsAllowedDocumentAssetUri(
            new Uri("https://document-assets.local.evil.example/logo.png")));
    }

    private static WebMessageEnvelope Envelope(
        string type,
        Guid requestId,
        Guid windowId,
        Guid tabId,
        long revision)
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return new WebMessageEnvelope(
            1,
            type,
            requestId,
            windowId,
            tabId,
            revision,
            document.RootElement.Clone());
    }
}
