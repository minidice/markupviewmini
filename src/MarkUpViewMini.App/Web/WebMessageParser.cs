using System.Text.Json;
using MarkUpViewMini.App.Mermaid;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.App.Web;

public static class WebMessageParser
{
    private const int MaximumOutlineItems = 10_000;
    private const int MaximumChanges = 10_000;
    private const int MaximumOrdinaryInsertedLength = 16 * 1024 * 1024;
    private const int MaximumBatchChunkLength = 1024 * 1024;
    private const long MaximumBatchInsertedLength = 64L * 1024 * 1024;

    public static WebMessageEnvelope Parse(string json)
    {
        if (json is null)
        {
            throw new WebMessageValidationException("Web message JSON is required.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new WebMessageValidationException("Web message is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("root");
            }

            RequireExactProperties(
                root,
                "version",
                "type",
                "requestId",
                "windowId",
                "tabId",
                "documentRevision",
                "payload");

            var version = ReadInt32(root, "version");
            if (version != 1)
            {
                throw Invalid("version");
            }

            var type = ReadString(root, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                throw Invalid("type");
            }

            var requestId = ReadGuid(root, "requestId");
            var windowId = ReadGuid(root, "windowId");
            var tabId = ReadGuid(root, "tabId");
            var revision = ReadInt64(root, "documentRevision");
            if (revision < 0)
            {
                throw Invalid("documentRevision");
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("payload");
            }

            return new WebMessageEnvelope(
                version,
                type,
                requestId,
                windowId,
                tabId,
                revision,
                payload.Clone());
        }
    }

    public static DocumentOutlineMessage ParseDocumentOutline(WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Type, "document.outline", StringComparison.Ordinal))
        {
            throw Invalid("type");
        }

        RequireExactProperties(message.Payload, "items");
        if (!message.Payload.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() > MaximumOutlineItems)
        {
            throw Invalid("payload.items");
        }

        var parsed = new List<WebOutlineItem>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("payload.items");
            }

            RequireExactProperties(item, "level", "text", "anchor", "sourceLine");
            var level = ReadInt32(item, "level");
            var text = ReadString(item, "text");
            var anchor = ReadString(item, "anchor");
            var sourceLine = ReadInt32(item, "sourceLine");
            if (level is < 1 or > 6 ||
                string.IsNullOrWhiteSpace(text) ||
                string.IsNullOrWhiteSpace(anchor) ||
                sourceLine <= 0)
            {
                throw Invalid("payload.items");
            }

            parsed.Add(new WebOutlineItem(level, text, anchor, sourceLine));
        }

        return new DocumentOutlineMessage(Owner(message), parsed.AsReadOnly());
    }

    public static LinkOpenMessage ParseLinkOpen(WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Type, "link.open", StringComparison.Ordinal))
        {
            throw Invalid("type");
        }

        RequireExactProperties(message.Payload, "href", "disposition");
        var target = ReadString(message.Payload, "href");
        var dispositionText = ReadString(message.Payload, "disposition");
        if (string.IsNullOrWhiteSpace(target))
        {
            throw Invalid("payload.href");
        }

        var disposition = dispositionText switch
        {
            "default" => LinkOpenDisposition.Default,
            "newTab" => LinkOpenDisposition.NewTab,
            _ => throw Invalid("payload.disposition"),
        };

        return new LinkOpenMessage(Owner(message), target, disposition);
    }

    public static LinkContextMenuMessage ParseLinkContextMenu(WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Type, "link.contextMenu", StringComparison.Ordinal))
        {
            throw Invalid("type");
        }

        RequireExactProperties(message.Payload, "href");
        var target = ReadString(message.Payload, "href");
        if (string.IsNullOrWhiteSpace(target))
        {
            throw Invalid("payload.href");
        }

        return new LinkContextMenuMessage(Owner(message), target);
    }

    public static DocumentChangedMessage ParseDocumentChanged(WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireType(message, "document.changed");
        RequireExactProperties(message.Payload, "expectedRevision", "changes");
        var expectedRevision = ReadExpectedRevision(message);
        var changes = ReadChanges(message.Payload, "insertedText", MaximumOrdinaryInsertedLength);
        return new DocumentChangedMessage(
            Owner(message),
            new DocumentEdit(expectedRevision, changes));
    }

    internal static DocumentChangeBatchStartMessage ParseDocumentChangeBatchStart(
        WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireType(message, "document.changeBatchStart");
        RequireExactProperties(message.Payload, "batchId", "expectedRevision", "changes");
        var batchId = ReadGuid(message.Payload, "batchId");
        var expectedRevision = ReadExpectedRevision(message);
        var changes = ReadChangeDeclarations(message.Payload);
        return new DocumentChangeBatchStartMessage(
            Owner(message),
            batchId,
            expectedRevision,
            changes);
    }

    internal static DocumentChangeBatchChunkMessage ParseDocumentChangeBatchChunk(
        WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireType(message, "document.changeBatchChunk");
        RequireExactProperties(message.Payload, "batchId", "changeIndex", "offset", "text");
        var batchId = ReadGuid(message.Payload, "batchId");
        var changeIndex = ReadInt32(message.Payload, "changeIndex");
        var offset = ReadInt32(message.Payload, "offset");
        var text = ReadString(message.Payload, "text");
        if (changeIndex < 0 || offset < 0 || text.Length is 0 or > MaximumBatchChunkLength)
        {
            throw Invalid("payload");
        }

        return new DocumentChangeBatchChunkMessage(
            Owner(message), batchId, changeIndex, offset, text);
    }

    internal static DocumentChangeBatchCommitMessage ParseDocumentChangeBatchCommit(
        WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireType(message, "document.changeBatchCommit");
        RequireExactProperties(message.Payload, "batchId");
        return new DocumentChangeBatchCommitMessage(
            Owner(message),
            ReadGuid(message.Payload, "batchId"));
    }

    public static DocumentModeChangedMessage ParseDocumentModeChanged(WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireType(message, "document.modeChanged");
        RequireExactProperties(message.Payload, "mode");
        var mode = ReadString(message.Payload, "mode") switch
        {
            "read" => DocumentMode.Read,
            "edit" => DocumentMode.Edit,
            _ => throw Invalid("payload.mode"),
        };
        return new DocumentModeChangedMessage(Owner(message), mode);
    }

    internal static MermaidFocusCompletedMessage ParseMermaidFocusCompleted(
        WebMessageEnvelope message)
    {
        if (!string.Equals(message.Type, "mermaid.focusCompleted", StringComparison.Ordinal))
        {
            throw Invalid("type");
        }

        RequireExactProperties(message.Payload, "actionId", "actionOrigin");
        var actionOrigin = ReadString(message.Payload, "actionOrigin");
        if (actionOrigin is not ("rendered" or "editor"))
        {
            throw Invalid("payload.actionOrigin");
        }

        return new MermaidFocusCompletedMessage(
            new WebMessageOwner(
                message.RequestId,
                message.WindowId,
                message.TabId,
                message.DocumentRevision),
            new MermaidActionIdentity(
                ReadGuid(message.Payload, "actionId"),
                actionOrigin));
    }

    public static DocumentUiHintsChangedMessage ParseDocumentUiHintsChanged(WebMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireType(message, "document.uiHintsChanged");
        RequireExactProperties(message.Payload, "selection", "scrollTop", "splitRatio", "find");
        if (!message.Payload.TryGetProperty("selection", out var selection) ||
            selection.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("payload.selection");
        }

        RequireExactProperties(selection, "anchor", "head");
        var anchor = ReadInt32(selection, "anchor");
        var head = ReadInt32(selection, "head");
        var scrollTop = ReadDouble(message.Payload, "scrollTop");
        var splitRatio = ReadDouble(message.Payload, "splitRatio");
        if (!message.Payload.TryGetProperty("find", out var find) ||
            find.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("payload.find");
        }

        RequireExactProperties(find, "matchCase", "wholeWord", "useRegex");
        var matchCase = ReadBoolean(find, "matchCase");
        var wholeWord = ReadBoolean(find, "wholeWord");
        var useRegex = ReadBoolean(find, "useRegex");
        if (anchor < 0 || head < 0 || scrollTop < 0 || scrollTop > 1_000_000_000 ||
            splitRatio is < 0.1 or > 0.9)
        {
            throw Invalid("payload");
        }

        return new DocumentUiHintsChangedMessage(
            Owner(message),
            new DocumentUiHints(anchor, head, scrollTop, splitRatio, matchCase, wholeWord, useRegex));
    }

    private static WebMessageOwner Owner(WebMessageEnvelope message) =>
        new(
            message.RequestId,
            message.WindowId,
            message.TabId,
            message.DocumentRevision);

    private static void RequireType(WebMessageEnvelope message, string expected)
    {
        if (!string.Equals(message.Type, expected, StringComparison.Ordinal))
        {
            throw Invalid("type");
        }
    }

    private static long ReadExpectedRevision(WebMessageEnvelope message)
    {
        var expectedRevision = ReadInt64(message.Payload, "expectedRevision");
        if (expectedRevision < 0 || expectedRevision != message.DocumentRevision)
        {
            throw Invalid("payload.expectedRevision");
        }

        return expectedRevision;
    }

    private static IReadOnlyList<TextChange> ReadChanges(
        JsonElement payload,
        string insertedProperty,
        int maximumInsertedLength)
    {
        if (!payload.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Array ||
            changes.GetArrayLength() is 0 or > MaximumChanges)
        {
            throw Invalid("payload.changes");
        }

        var parsed = new List<TextChange>(changes.GetArrayLength());
        long insertedLength = 0;
        var previousTo = 0;
        foreach (var change in changes.EnumerateArray())
        {
            RequireExactProperties(change, "from", "to", insertedProperty);
            var from = ReadInt32(change, "from");
            var to = ReadInt32(change, "to");
            var insertedText = ReadString(change, insertedProperty);
            if (from < 0 || to < from || (parsed.Count > 0 && from < previousTo))
            {
                throw Invalid("payload.changes");
            }

            insertedLength += insertedText.Length;
            if (insertedLength > maximumInsertedLength)
            {
                throw Invalid("payload.changes");
            }

            parsed.Add(new TextChange(from, to, insertedText));
            previousTo = to;
        }

        return parsed.AsReadOnly();
    }

    private static IReadOnlyList<DocumentChangeDeclaration> ReadChangeDeclarations(JsonElement payload)
    {
        if (!payload.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Array ||
            changes.GetArrayLength() is 0 or > MaximumChanges)
        {
            throw Invalid("payload.changes");
        }

        var parsed = new List<DocumentChangeDeclaration>(changes.GetArrayLength());
        long total = 0;
        var previousTo = 0;
        foreach (var change in changes.EnumerateArray())
        {
            RequireExactProperties(change, "from", "to", "insertedLength");
            var from = ReadInt32(change, "from");
            var to = ReadInt32(change, "to");
            var insertedLength = ReadInt32(change, "insertedLength");
            if (from < 0 || to < from || insertedLength < 0 ||
                (parsed.Count > 0 && from < previousTo))
            {
                throw Invalid("payload.changes");
            }

            total += insertedLength;
            if (total > MaximumBatchInsertedLength)
            {
                throw Invalid("payload.changes");
            }

            parsed.Add(new DocumentChangeDeclaration(from, to, insertedLength));
            previousTo = to;
        }

        return parsed.AsReadOnly();
    }

    private static void RequireExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("payload");
        }

        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (!expected.Contains(property.Name))
            {
                throw Invalid("payload");
            }
        }

        if (count != expected.Count)
        {
            throw Invalid("payload");
        }
    }

    private static int ReadInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw Invalid(propertyName);
        }

        return result;
    }

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result))
        {
            throw Invalid(propertyName);
        }

        return result;
    }

    private static double ReadDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var result) ||
            !double.IsFinite(result))
        {
            throw Invalid(propertyName);
        }

        return result;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(propertyName);
        }

        return value.GetString()!;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(propertyName);
        }

        return value.GetBoolean();
    }

    private static Guid ReadGuid(JsonElement root, string propertyName)
    {
        var value = ReadString(root, propertyName);
        if (!Guid.TryParseExact(value, "D", out var result) || result == Guid.Empty)
        {
            throw Invalid(propertyName);
        }

        return result;
    }

    private static WebMessageValidationException Invalid(string field) =>
        new($"Web message field '{field}' is invalid.");
}
