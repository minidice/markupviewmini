using System.Text.Json;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Mermaid;

namespace MarkUpViewMini.App.Mermaid;

public sealed record MermaidEditRequest
{
    public MermaidEditRequest(MermaidBlockSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.SessionId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty Mermaid session ID is required.", nameof(snapshot));
        }

        if (snapshot.TabId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty tab ID is required.", nameof(snapshot));
        }
    }

    public MermaidBlockSnapshot Snapshot { get; }
}

public enum MermaidDialogAction
{
    Canceled,
    Confirmed,
    ReopenRequested,
}

public sealed record MermaidDialogResult(MermaidDialogAction Action, string Source)
{
    public bool IsConfirmed => Action == MermaidDialogAction.Confirmed;

    internal static MermaidDialogResult Confirmed(string source) =>
        new(MermaidDialogAction.Confirmed, source);

    internal static MermaidDialogResult Canceled(string originalSource) =>
        new(MermaidDialogAction.Canceled, originalSource);

    internal static MermaidDialogResult ReopenRequested(string originalSource) =>
        new(MermaidDialogAction.ReopenRequested, originalSource);
}

internal static class MermaidReopenRequest
{
    public static bool TryCreate(
        MermaidDialogResult result,
        MermaidBlockSnapshot snapshot,
        long dialogGeneration,
        long currentDialogGeneration,
        WebResponseContext current,
        Guid windowId,
        Guid activeTabId,
        long activeRevision,
        out string json)
    {
        json = string.Empty;
        if (result.Action != MermaidDialogAction.ReopenRequested ||
            dialogGeneration != currentDialogGeneration ||
            current.RequestId == Guid.Empty ||
            current.TabId == Guid.Empty ||
            current.Revision < 0 ||
            windowId == Guid.Empty ||
            snapshot.TabId != current.TabId ||
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
                from = snapshot.From,
                sourceHash = snapshot.SourceHash,
            },
        });
        return true;
    }
}
