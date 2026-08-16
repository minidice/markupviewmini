using System.Text.Json;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Mermaid;

namespace MarkUpViewMini.App.Mermaid;

internal sealed class MermaidDocumentEditSession
{
    private readonly WebMessageOwner owner;
    private readonly Guid actionId;
    private readonly string actionOrigin;
    private readonly long dialogGeneration;
    private readonly object surfaceOwner;

    private MermaidDocumentEditSession(
        MermaidBlockSnapshot snapshot,
        WebMessageOwner owner,
        Guid actionId,
        string actionOrigin,
        long dialogGeneration,
        object surfaceOwner)
    {
        Snapshot = snapshot;
        DialogRequest = new MermaidEditRequest(snapshot);
        this.owner = owner;
        this.actionId = actionId;
        this.actionOrigin = actionOrigin;
        this.dialogGeneration = dialogGeneration;
        this.surfaceOwner = surfaceOwner;
    }

    internal MermaidBlockSnapshot Snapshot { get; }

    internal MermaidEditRequest DialogRequest { get; }

    internal Guid OwnerRequestId => owner.RequestId;

    internal static bool TryCreate(
        WebMessageEnvelope message,
        long dialogGeneration,
        object surfaceOwner,
        out MermaidDocumentEditSession session)
    {
        session = null!;
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(surfaceOwner);
        var payload = message.Payload;
        if (message.Version != 1 ||
            !string.Equals(message.Type, "mermaid.editRequested", StringComparison.Ordinal) ||
            message.RequestId == Guid.Empty ||
            message.WindowId == Guid.Empty ||
            message.TabId == Guid.Empty ||
            message.DocumentRevision < 0 ||
            dialogGeneration < 0 ||
            payload.ValueKind != JsonValueKind.Object ||
            payload.EnumerateObject().Count() != 6 ||
            !payload.TryGetProperty("from", out var fromElement) ||
            !fromElement.TryGetInt32(out var from) ||
            !payload.TryGetProperty("to", out var toElement) ||
            !toElement.TryGetInt32(out var to) ||
            !payload.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("sourceHash", out var hashElement) ||
            hashElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("actionId", out var actionIdElement) ||
            actionIdElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(actionIdElement.GetString(), out var actionId) ||
            actionId == Guid.Empty ||
            !payload.TryGetProperty("actionOrigin", out var originElement) ||
            originElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var source = sourceElement.GetString();
        var sourceHash = hashElement.GetString();
        var actionOrigin = originElement.GetString();
        if (from < 0 ||
            to < from ||
            source is null ||
            source.Length != to - from ||
            sourceHash is null ||
            sourceHash.Length != 64 ||
            sourceHash.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            actionOrigin is not ("rendered" or "editor"))
        {
            return false;
        }

        var snapshot = new MermaidBlockSnapshot(
            Guid.NewGuid(),
            message.TabId,
            message.DocumentRevision,
            from,
            to,
            source,
            sourceHash);
        session = new MermaidDocumentEditSession(
            snapshot,
            new WebMessageOwner(
                message.RequestId,
                message.WindowId,
                message.TabId,
                message.DocumentRevision),
            actionId,
            actionOrigin,
            dialogGeneration,
            surfaceOwner);
        return true;
    }

    internal MermaidApplyResult TryApply(
        long currentDialogGeneration,
        WebResponseContext? current,
        Guid windowId,
        DocumentBuffer buffer,
        Action<DocumentChangedMessage> applyAuthoritatively,
        Func<WebResponseContext, Guid, long, bool> updateCurrentResponse,
        Action postActivation,
        string replacement)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(applyAuthoritatively);
        ArgumentNullException.ThrowIfNull(updateCurrentResponse);
        ArgumentNullException.ThrowIfNull(postActivation);
        if (!IsCurrent(currentDialogGeneration, current, windowId, buffer.TabId, buffer.Revision))
        {
            return MermaidApplyResult.StaleRevision;
        }

        var prepared = MermaidEditTransaction.Prepare(buffer, Snapshot, replacement);
        if (prepared.Result != MermaidApplyResult.Applied ||
            prepared.Edit is null ||
            prepared.AcceptedText is null ||
            prepared.AcceptedRevision is null)
        {
            return prepared.Result;
        }

        applyAuthoritatively(new DocumentChangedMessage(owner, prepared.Edit));
        var authoritative = buffer.CaptureSnapshot();
        var response = current!.Value;
        if (authoritative.Revision != prepared.AcceptedRevision.Value ||
            !string.Equals(authoritative.Text, prepared.AcceptedText, StringComparison.Ordinal) ||
            !updateCurrentResponse(response, owner.RequestId, authoritative.Revision))
        {
            return MermaidApplyResult.StaleRevision;
        }

        postActivation();
        return MermaidApplyResult.Applied;
    }

    internal bool TryCreateFocusMessage(
        long currentDialogGeneration,
        WebResponseContext? current,
        Guid windowId,
        Guid activeTabId,
        long activeRevision,
        object currentSurface,
        out string json)
    {
        if (!TryCreateFocusRequest(
                currentDialogGeneration,
                current,
                windowId,
                activeTabId,
                activeRevision,
                currentSurface,
                out var request))
        {
            json = string.Empty;
            return false;
        }

        json = request.Json;
        return true;
    }

    internal bool TryCreateFocusRequest(
        long currentDialogGeneration,
        WebResponseContext? current,
        Guid windowId,
        Guid activeTabId,
        long activeRevision,
        object currentSurface,
        out MermaidFocusRequest request)
    {
        request = null!;
        if (!ReferenceEquals(surfaceOwner, currentSurface) ||
            !IsCurrent(currentDialogGeneration, current, windowId, activeTabId, activeRevision))
        {
            return false;
        }

        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type = "mermaid.focusRequested",
            requestId = owner.RequestId.ToString("D"),
            windowId = owner.WindowId.ToString("D"),
            tabId = owner.TabId.ToString("D"),
            documentRevision = owner.DocumentRevision,
            payload = new
            {
                actionId = actionId.ToString("D"),
                actionOrigin,
            },
        });
        request = new MermaidFocusRequest(
            owner,
            new MermaidActionIdentity(actionId, actionOrigin),
            surfaceOwner,
            json);
        return true;
    }

    internal bool TryCreateReopenMessage(
        MermaidDialogResult result,
        long expectedDialogGeneration,
        long currentDialogGeneration,
        WebResponseContext current,
        Guid windowId,
        Guid activeTabId,
        long activeRevision,
        out string json)
    {
        json = string.Empty;
        if (result.Action != MermaidDialogAction.ReopenRequested ||
            expectedDialogGeneration != dialogGeneration ||
            currentDialogGeneration != dialogGeneration ||
            current.RequestId == Guid.Empty ||
            current.TabId != owner.TabId ||
            current.Revision < 0 ||
            windowId != owner.WindowId ||
            activeTabId != current.TabId ||
            activeRevision != current.Revision)
        {
            return false;
        }

        json = JsonSerializer.Serialize(new
        {
            version = 1,
            type = "mermaid.reopenRequested",
            requestId = current.RequestId.ToString("D"),
            windowId = windowId.ToString("D"),
            tabId = current.TabId.ToString("D"),
            documentRevision = current.Revision,
            payload = new
            {
                from = Snapshot.From,
                sourceHash = Snapshot.SourceHash,
                actionId = actionId.ToString("D"),
                actionOrigin,
            },
        });
        return true;
    }

    private bool IsCurrent(
        long currentDialogGeneration,
        WebResponseContext? current,
        Guid windowId,
        Guid activeTabId,
        long activeRevision) =>
        currentDialogGeneration == dialogGeneration &&
        current is { } response &&
        response.RequestId == owner.RequestId &&
        response.TabId == owner.TabId &&
        response.Revision == owner.DocumentRevision &&
        windowId == owner.WindowId &&
        activeTabId == owner.TabId &&
        activeRevision == owner.DocumentRevision;
}
