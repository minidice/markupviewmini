using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.App.Web;

public sealed record WebMessageOwner(
    Guid RequestId,
    Guid WindowId,
    Guid TabId,
    long DocumentRevision);

public sealed record WebOutlineItem(
    int Level,
    string Text,
    string Anchor,
    int SourceLine);

public sealed record DocumentOutlineMessage(
    WebMessageOwner Owner,
    IReadOnlyList<WebOutlineItem> Items);

public sealed record LinkOpenMessage(
    WebMessageOwner Owner,
    string Target,
    LinkOpenDisposition Disposition);

public sealed record LinkContextMenuMessage(
    WebMessageOwner Owner,
    string Target);

public sealed record DocumentChangedMessage(
    WebMessageOwner Owner,
    DocumentEdit Edit);

public sealed record DocumentModeChangedMessage(
    WebMessageOwner Owner,
    DocumentMode Mode);

public sealed record DocumentUiHints(
    int SelectionAnchor,
    int SelectionHead,
    double ScrollTop,
    double SplitRatio = 0.5,
    bool FindMatchCase = false,
    bool FindWholeWord = false,
    bool FindUseRegex = false);

public sealed record DocumentUiHintsChangedMessage(
    WebMessageOwner Owner,
    DocumentUiHints Hints);

internal sealed record DocumentChangeDeclaration(
    int From,
    int To,
    int InsertedLength);

internal sealed record DocumentChangeBatchStartMessage(
    WebMessageOwner Owner,
    Guid BatchId,
    long ExpectedRevision,
    IReadOnlyList<DocumentChangeDeclaration> Changes);

internal sealed record DocumentChangeBatchChunkMessage(
    WebMessageOwner Owner,
    Guid BatchId,
    int ChangeIndex,
    int Offset,
    string Text);

internal sealed record DocumentChangeBatchCommitMessage(
    WebMessageOwner Owner,
    Guid BatchId);
