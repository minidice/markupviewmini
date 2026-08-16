using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Mermaid;

namespace MarkUpViewMini.App.Mermaid;

internal sealed record MermaidPreparedEdit(
    MermaidApplyResult Result,
    DocumentEdit? Edit,
    string? AcceptedText,
    long? AcceptedRevision);

internal static class MermaidEditTransaction
{
    public static MermaidPreparedEdit Prepare(
        DocumentBuffer liveBuffer,
        MermaidBlockSnapshot snapshot,
        string replacement)
    {
        ArgumentNullException.ThrowIfNull(liveBuffer);
        ArgumentNullException.ThrowIfNull(snapshot);

        var before = liveBuffer.CaptureSnapshot();
        var staged = liveBuffer.Clone();
        var result = MermaidBlockUpdater.TryApply(staged, snapshot, replacement);
        if (result != MermaidApplyResult.Applied)
        {
            return new MermaidPreparedEdit(result, null, null, null);
        }

        var accepted = staged.CaptureSnapshot();
        return new MermaidPreparedEdit(
            result,
            new DocumentEdit(
                before.Revision,
                [new TextChange(0, before.Text.Length, accepted.Text)]),
            accepted.Text,
            accepted.Revision);
    }
}
