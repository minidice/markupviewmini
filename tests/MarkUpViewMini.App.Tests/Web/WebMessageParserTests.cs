using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Navigation;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebMessageParserTests
{
    private const string ValidMessage =
        """{"version":1,"type":"surface.ready","requestId":"00000000-0000-0000-0000-000000000001","windowId":"00000000-0000-0000-0000-000000000002","tabId":"00000000-0000-0000-0000-000000000003","documentRevision":0,"payload":{}}""";

    [Fact]
    public void Parse_accepts_valid_surface_ready()
    {
        var message = WebMessageParser.Parse(ValidMessage);

        Assert.Equal(1, message.Version);
        Assert.Equal("surface.ready", message.Type);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), message.RequestId);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), message.WindowId);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000003"), message.TabId);
        Assert.Equal(0, message.DocumentRevision);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, message.Payload.ValueKind);
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void Parse_rejects_every_invalid_envelope_shape(string json)
    {
        Assert.Throws<WebMessageValidationException>(() => WebMessageParser.Parse(json));
    }

    [Fact]
    public void Parse_does_not_leak_body_or_payload_in_validation_exception()
    {
        const string secret = "TOP-SECRET-DOCUMENT-BODY";
        var json =
            "{\"version\":2,\"type\":\"surface.ready\",\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"windowId\":\"00000000-0000-0000-0000-000000000002\",\"tabId\":\"00000000-0000-0000-0000-000000000003\",\"documentRevision\":0,\"payload\":{\"text\":\"" +
            secret +
            "\"}}";

        var exception = Assert.Throws<WebMessageValidationException>(() => WebMessageParser.Parse(json));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDocumentOutline_accepts_exact_typed_items_and_owner_context()
    {
        // Break caught: valid outline metadata can remain raw JSON or lose its correlation owner.
        var envelope = WebMessageParser.Parse(Message(
            "document.outline",
            "{\"items\":[{\"level\":2,\"text\":\"Install\",\"anchor\":\"install\",\"sourceLine\":18}]}"));

        var outline = WebMessageParser.ParseDocumentOutline(envelope);

        Assert.Equal(envelope.RequestId, outline.Owner.RequestId);
        Assert.Equal(envelope.WindowId, outline.Owner.WindowId);
        Assert.Equal(envelope.TabId, outline.Owner.TabId);
        Assert.Equal(7, outline.Owner.DocumentRevision);
        Assert.Equal(new WebOutlineItem(2, "Install", "install", 18), Assert.Single(outline.Items));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":[],\"extra\":true}")]
    [InlineData("{\"items\":[{\"level\":0,\"text\":\"Title\",\"anchor\":\"title\",\"sourceLine\":1}]}")]
    [InlineData("{\"items\":[{\"level\":7,\"text\":\"Title\",\"anchor\":\"title\",\"sourceLine\":1}]}")]
    [InlineData("{\"items\":[{\"level\":1,\"text\":\"\",\"anchor\":\"title\",\"sourceLine\":1}]}")]
    [InlineData("{\"items\":[{\"level\":1,\"text\":\"Title\",\"anchor\":\" \",\"sourceLine\":1}]}")]
    [InlineData("{\"items\":[{\"level\":1,\"text\":\"Title\",\"anchor\":\"title\",\"sourceLine\":0}]}")]
    [InlineData("{\"items\":[{\"level\":1,\"text\":\"Title\",\"anchor\":\"title\",\"sourceLine\":1,\"extra\":true}]}")]
    public void ParseDocumentOutline_rejects_non_exact_or_invalid_payloads(string payload)
    {
        // Break caught: malformed or expanded outline payloads can cross the WebView trust boundary.
        var envelope = WebMessageParser.Parse(Message("document.outline", payload));

        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentOutline(envelope));
    }

    [Fact]
    public void ParseDocumentOutline_rejects_more_than_ten_thousand_items()
    {
        // Break caught: an unbounded outline can allocate or dispatch attacker-controlled item counts.
        var item = "{\"level\":1,\"text\":\"T\",\"anchor\":\"t\",\"sourceLine\":1}";
        var envelope = WebMessageParser.Parse(Message(
            "document.outline",
            $"{{\"items\":[{string.Join(',', Enumerable.Repeat(item, 10_001))}]}}"));

        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentOutline(envelope));
    }

    [Fact]
    public void ParseLinkOpen_accepts_only_the_exact_approved_disposition()
    {
        // Break caught: link requests can retain raw JSON or map Ctrl-click to the wrong disposition.
        var envelope = WebMessageParser.Parse(Message(
            "link.open",
            "{\"href\":\"chapter.md#install\",\"disposition\":\"newTab\"}"));

        var link = WebMessageParser.ParseLinkOpen(envelope);

        Assert.Equal("chapter.md#install", link.Target);
        Assert.Equal(LinkOpenDisposition.NewTab, link.Disposition);
        Assert.Equal(envelope.TabId, link.Owner.TabId);
        Assert.Equal(7, link.Owner.DocumentRevision);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"href\":\"\",\"disposition\":\"default\"}")]
    [InlineData("{\"href\":\"chapter.md\",\"disposition\":\"internal\"}")]
    [InlineData("{\"href\":\"chapter.md\",\"disposition\":\"default\",\"extra\":true}")]
    public void ParseLinkOpen_rejects_missing_extra_or_unapproved_payload_fields(string payload)
    {
        // Break caught: malformed or policy-expanding link payloads can reach Core routing.
        var envelope = WebMessageParser.Parse(Message("link.open", payload));

        Assert.Throws<WebMessageValidationException>(() => WebMessageParser.ParseLinkOpen(envelope));
    }

    [Fact]
    public void ParseLinkContextMenu_accepts_only_one_nonempty_raw_href()
    {
        // Break caught: a context-menu request can lose its raw relative href or admit arbitrary payload fields.
        var envelope = WebMessageParser.Parse(Message(
            "link.contextMenu",
            "{\"href\":\"../chapter.md#part\"}"));

        var parsed = WebMessageParser.ParseLinkContextMenu(envelope);

        Assert.Equal("../chapter.md#part", parsed.Target);
        Assert.Equal(envelope.RequestId, parsed.Owner.RequestId);
        var expanded = WebMessageParser.Parse(Message(
            "link.contextMenu",
            "{\"href\":\"chapter.md\",\"extra\":true}"));
        Assert.Throws<WebMessageValidationException>(() => WebMessageParser.ParseLinkContextMenu(expanded));
    }

    [Fact]
    public void Parse_rejects_extra_top_level_fields()
    {
        // Break caught: an expanded unapproved envelope can cross the common WebView trust boundary.
        var json = ValidMessage[..^1] + ",\"extra\":true}";

        Assert.Throws<WebMessageValidationException>(() => WebMessageParser.Parse(json));
    }

    public static TheoryData<string> InvalidMessages => new()
    {
        "not-json",
        "[]",
        "{}",
        ValidMessage.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
        ValidMessage.Replace("\"version\":1,", string.Empty, StringComparison.Ordinal),
        ValidMessage.Replace("\"version\":1", "\"version\":\"1\"", StringComparison.Ordinal),
        ValidMessage.Replace("\"type\":\"surface.ready\",", string.Empty, StringComparison.Ordinal),
        ValidMessage.Replace("\"surface.ready\"", "\"\"", StringComparison.Ordinal),
        ValidMessage.Replace("\"surface.ready\"", "7", StringComparison.Ordinal),
        WithoutProperty("requestId", "00000000-0000-0000-0000-000000000001"),
        WithGuid("requestId", "not-a-guid", "00000000-0000-0000-0000-000000000001"),
        WithGuid("requestId", Guid.Empty.ToString(), "00000000-0000-0000-0000-000000000001"),
        WithoutProperty("windowId", "00000000-0000-0000-0000-000000000002"),
        WithGuid("windowId", "not-a-guid", "00000000-0000-0000-0000-000000000002"),
        WithGuid("windowId", Guid.Empty.ToString(), "00000000-0000-0000-0000-000000000002"),
        WithoutProperty("tabId", "00000000-0000-0000-0000-000000000003"),
        WithGuid("tabId", "not-a-guid", "00000000-0000-0000-0000-000000000003"),
        WithGuid("tabId", Guid.Empty.ToString(), "00000000-0000-0000-0000-000000000003"),
        ValidMessage.Replace("\"documentRevision\":0,", string.Empty, StringComparison.Ordinal),
        ValidMessage.Replace("\"documentRevision\":0", "\"documentRevision\":-1", StringComparison.Ordinal),
        ValidMessage.Replace("\"documentRevision\":0", "\"documentRevision\":1.5", StringComparison.Ordinal),
        ValidMessage.Replace("\"documentRevision\":0", "\"documentRevision\":\"0\"", StringComparison.Ordinal),
        ValidMessage.Replace(",\"payload\":{}", string.Empty, StringComparison.Ordinal),
        ValidMessage.Replace("\"payload\":{}", "\"payload\":null", StringComparison.Ordinal),
        ValidMessage.Replace("\"payload\":{}", "\"payload\":[]", StringComparison.Ordinal),
    };

    private static string WithoutProperty(string propertyName, string propertyValue) =>
        ValidMessage.Replace($"\"{propertyName}\":\"{propertyValue}\",", string.Empty, StringComparison.Ordinal);

    private static string WithGuid(string propertyName, string replacement, string current) =>
        ValidMessage.Replace(
            $"\"{propertyName}\":\"{current}\"",
            $"\"{propertyName}\":\"{replacement}\"",
            StringComparison.Ordinal);

    private static string Message(string type, string payload) =>
        $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"windowId\":\"00000000-0000-0000-0000-000000000002\",\"tabId\":\"00000000-0000-0000-0000-000000000003\",\"documentRevision\":7,\"payload\":{payload}}}";
}
