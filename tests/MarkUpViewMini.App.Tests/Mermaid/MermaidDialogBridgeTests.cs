using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarkUpViewMini.App.Mermaid;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Mermaid;
using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Mermaid;

public sealed class MermaidDialogBridgeTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid TabId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid WindowId = Guid.Parse("99999999-8888-4777-8666-555555555555");
    private const string OriginalSource = "flowchart LR\nA --> B";
    private static readonly DiskFileVersion InitialVersion =
        new(1, DateTime.UnixEpoch, new string('a', 64));

    [Fact]
    public void PreparedApplyUsesOneAuthoritativeFullTextEditWithoutMutatingTheLiveBuffer()
    {
        // Break caught: modal confirmation mutates live Markdown before its owned edit is accepted.
        var buffer = CreateBuffer("before\nflowchart LR\nA --> B\nafter");
        var snapshot = SnapshotFor(buffer, OriginalSource);

        var prepared = MermaidEditTransaction.Prepare(
            buffer,
            snapshot,
            "flowchart LR\nA --> C");

        Assert.Equal(MermaidApplyResult.Applied, prepared.Result);
        Assert.Equal(0, buffer.Revision);
        Assert.Equal("before\nflowchart LR\nA --> B\nafter", buffer.Text);
        Assert.Equal(1L, prepared.AcceptedRevision);
        Assert.Equal("before\nflowchart LR\nA --> C\nafter", prepared.AcceptedText);
        var edit = Assert.IsType<DocumentEdit>(prepared.Edit);
        Assert.Equal(0, edit.ExpectedRevision);
        var change = Assert.Single(edit.Changes);
        Assert.Equal(0, change.From);
        Assert.Equal(buffer.Text.Length, change.To);
        Assert.Equal(prepared.AcceptedText, change.InsertedText);
    }

    [Fact]
    public void PreparedConflictNeverCreatesAnEditOrMutatesTheLiveBuffer()
    {
        // Break caught: a stale dialog stages an edit that can be applied despite the conflict.
        var buffer = CreateBuffer("before\nflowchart LR\nA --> B\nafter");
        var snapshot = SnapshotFor(buffer, OriginalSource);
        buffer.Apply(new DocumentEdit(0, [new TextChange(0, 0, "changed\n")]));

        var prepared = MermaidEditTransaction.Prepare(
            buffer,
            snapshot,
            "flowchart LR\nA --> C");

        Assert.Equal(MermaidApplyResult.StaleRevision, prepared.Result);
        Assert.Null(prepared.Edit);
        Assert.Null(prepared.AcceptedText);
        Assert.Equal("changed\nbefore\nflowchart LR\nA --> B\nafter", buffer.Text);
        Assert.Equal(1, buffer.Revision);
    }

    [Fact]
    public void ReadyPostsExactOpenMessageOnlyAfterReady()
    {
        // Break caught: the host posts mermaid.open before the editor installs its listener.
        var posted = new List<string>();
        using var bridge = CreateBridge(posted.Add);

        Assert.Empty(posted);
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));

        var message = Parse(posted.Single());
        Assert.Equal(1, message.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("mermaid.open", message.RootElement.GetProperty("type").GetString());
        var payload = message.RootElement.GetProperty("payload");
        Assert.Equal(SessionId.ToString(), payload.GetProperty("sessionId").GetString());
        Assert.Equal(OriginalSource, payload.GetProperty("source").GetString());
        Assert.Equal("mermaid", payload.GetProperty("language").GetString());
        Assert.Equal(3, payload.EnumerateObject().Count());

        Assert.False(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.Single(posted);
    }

    [Fact]
    public async Task ConfirmAcceptsOnlyTheExactSessionAndReturnsReplacement()
    {
        // Break caught: a message from another or stale dialog owns this session.
        using var bridge = CreateBridge(_ => { });
        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "too early",
            language = "mermaid",
            sourceVersion = 0,
        })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.changed", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> C",
            language = "mermaid",
            sourceVersion = 1,
        })));
        Assert.True(bridge.TryHandleMessage(Validity(
            "flowchart LR\nA --> C", supported: true, sourceVersion: 1)));
        var foreign = Message("mermaid.confirm", new
        {
            sessionId = Guid.NewGuid(),
            source = "flowchart LR\nX --> Y",
            language = "mermaid",
            sourceVersion = 1,
        });

        Assert.False(bridge.TryHandleMessage(foreign));
        Assert.False(bridge.Completion.IsCompleted);

        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> C",
            language = "mermaid",
            sourceVersion = 1,
        })));

        var result = await bridge.Completion;
        Assert.True(result.IsConfirmed);
        Assert.Equal("flowchart LR\nA --> C", result.Source);
    }

    [Fact]
    public void SupportedSourceCannotAuthorizeAConfirmForAnotherSourceInTheSameSession()
    {
        // Break caught: a supported result for A is reused to authorize a forged confirm for B.
        using var bridge = CreateBridge(_ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Validity("flowchart LR\nA --> B", supported: true)));

        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> C",
            language = "mermaid",
            sourceVersion = 0,
        })));
        Assert.False(bridge.Completion.IsCompleted);
    }

    [Fact]
    public void SourceChangeInvalidatesEarlierSupportUntilThatExactSourceIsValidated()
    {
        // Break caught: changed(B) can race behind validity(A), leaving Confirm enabled for stale A.
        using var bridge = CreateBridge(_ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Validity("flowchart LR\nA --> B", supported: true)));

        Assert.True(bridge.TryHandleMessage(Message("mermaid.changed", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> C",
            language = "mermaid",
            sourceVersion = 1,
        })));

        Assert.False(bridge.CurrentSourceSupported);
        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> B",
            language = "mermaid",
            sourceVersion = 0,
        })));
        Assert.False(bridge.TryHandleMessage(Validity(
            "flowchart LR\nA --> B", supported: true, sourceVersion: 0)));
        Assert.True(bridge.TryHandleMessage(Validity(
            "flowchart LR\nA --> C", supported: true, sourceVersion: 1)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> C",
            language = "mermaid",
            sourceVersion = 1,
        })));
        Assert.True(bridge.Completion.IsCompleted);
    }

    [Fact]
    public void StaleValidityCannotReauthorizeAnOlderSourceAfterAMonotonicChange()
    {
        // Break caught: validity(A) delivered after changed(B) can re-enable Confirm for stale A.
        using var bridge = CreateBridge(_ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(VersionedValidity(
            "flowchart LR\nA --> B", sourceVersion: 0, supported: true)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.changed", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> C",
            language = "mermaid",
            sourceVersion = 1,
        })));

        Assert.False(bridge.TryHandleMessage(VersionedValidity(
            "flowchart LR\nA --> B", sourceVersion: 0, supported: true)));
        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "flowchart LR\nA --> B",
            language = "mermaid",
            sourceVersion = 0,
        })));
        Assert.False(bridge.Completion.IsCompleted);
    }

    [Fact]
    public void ChangedVersionMustAdvanceExactlyOnceAndDuplicateValidityIsRejected()
    {
        // Break caught: a version jump or duplicate change can bypass the current-source challenge.
        using var bridge = CreateBridge(_ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.False(bridge.TryHandleMessage(Message("mermaid.changed", new
        {
            sessionId = SessionId,
            source = "jump",
            language = "mermaid",
            sourceVersion = 2,
        })));
        Assert.True(bridge.TryHandleMessage(VersionedValidity(
            OriginalSource, sourceVersion: 0, supported: true)));
        Assert.False(bridge.TryHandleMessage(VersionedValidity(
            OriginalSource, sourceVersion: 0, supported: true)));
        Assert.True(bridge.CurrentSourceSupported);
    }

    [Fact]
    public async Task CancelReturnsTheUnchangedOriginalSource()
    {
        // Break caught: cancel leaks an in-progress editor change into the document.
        using var bridge = CreateBridge(_ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.changed", new
        {
            sessionId = SessionId,
            source = "first",
            language = "mermaid",
            sourceVersion = 1,
        })));
        Assert.True(bridge.TryHandleMessage(Validity("first", supported: true, sourceVersion: 1)));

        Assert.True(bridge.TryHandleMessage(Message("mermaid.cancel", new
        {
            sessionId = SessionId,
        })));

        var result = await bridge.Completion;
        Assert.False(result.IsConfirmed);
        Assert.Equal(OriginalSource, result.Source);
    }

    [Fact]
    public async Task DuplicateAndLateTerminalMessagesAreIgnored()
    {
        // Break caught: a duplicate WebView event overwrites the first terminal choice.
        using var bridge = CreateBridge(_ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.changed", new
        {
            sessionId = SessionId,
            source = "first",
            language = "mermaid",
            sourceVersion = 1,
        })));
        Assert.True(bridge.TryHandleMessage(Validity("first", supported: true, sourceVersion: 1)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "first",
            language = "mermaid",
            sourceVersion = 1,
        })));

        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "second",
            language = "mermaid",
            sourceVersion = 2,
        })));
        Assert.False(bridge.TryCancel());
        Assert.Equal("first", (await bridge.Completion).Source);
    }

    [Fact]
    public async Task WindowCancelReturnsTheUnchangedOriginalSource()
    {
        // Break caught: closing the WPF window leaves the modal completion unresolved.
        using var bridge = CreateBridge(_ => { });

        Assert.True(bridge.TryCancel());

        var result = await bridge.Completion;
        Assert.False(result.IsConfirmed);
        Assert.Equal(OriginalSource, result.Source);
    }

    [Fact]
    public async Task DisposedBridgeIgnoresMessagesAndResolvesAsCancel()
    {
        // Break caught: a late WebView callback can complete a disposed dialog as confirmed.
        var bridge = CreateBridge(_ => { });
        bridge.Dispose();

        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = SessionId,
            source = "late",
            language = "mermaid",
            sourceVersion = 0,
        })));
        var result = await bridge.Completion;
        Assert.False(result.IsConfirmed);
        Assert.Equal(OriginalSource, result.Source);
    }

    [Fact]
    public void ConflictReopenReturnsATypedActionAndCreatesOnlyACurrentFreshSnapshotRequest()
    {
        // Break caught: 다시 열기 closed as cancel, so the document surface never requested a fresh block session.
        var snapshot = new MermaidBlockSnapshot(
            SessionId,
            TabId,
            7,
            10,
            30,
            OriginalSource,
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var current = new WebResponseContext(Guid.NewGuid(), TabId, 8);
        var result = MermaidDialogResult.ReopenRequested(OriginalSource);

        Assert.Equal(MermaidDialogAction.ReopenRequested, result.Action);
        Assert.False(result.IsConfirmed);
        Assert.True(MermaidReopenRequest.TryCreate(
            result,
            snapshot,
            dialogGeneration: 3,
            currentDialogGeneration: 3,
            current,
            WindowId,
            activeTabId: TabId,
            activeRevision: 8,
            out var json));

        using var message = JsonDocument.Parse(json);
        var root = message.RootElement;
        Assert.Equal("mermaid.reopenRequested", root.GetProperty("type").GetString());
        Assert.Equal(current.RequestId.ToString(), root.GetProperty("requestId").GetString());
        Assert.Equal(8, root.GetProperty("documentRevision").GetInt64());
        var payload = root.GetProperty("payload");
        Assert.Equal(2, payload.EnumerateObject().Count());
        Assert.Equal(snapshot.From, payload.GetProperty("from").GetInt32());
        Assert.Equal(snapshot.SourceHash, payload.GetProperty("sourceHash").GetString());
        Assert.False(payload.TryGetProperty("source", out _));
        Assert.False(payload.TryGetProperty("replacement", out _));

        Assert.False(MermaidReopenRequest.TryCreate(
            result,
            snapshot,
            3,
            4,
            current,
            WindowId,
            TabId,
            8,
            out _));
        Assert.False(MermaidReopenRequest.TryCreate(
            result,
            snapshot,
            3,
            3,
            current,
            WindowId,
            Guid.NewGuid(),
            8,
            out _));
        Assert.False(MermaidReopenRequest.TryCreate(
            result,
            snapshot,
            3,
            3,
            current,
            WindowId,
            TabId,
            7,
            out _));
    }

    private static MermaidDialogBridge CreateBridge(Action<string> post) =>
        new(new MermaidEditRequest(new MermaidBlockSnapshot(
            SessionId,
            TabId,
            7,
            10,
            30,
            OriginalSource,
            "source-hash")), post);

    private static string Message(string type, object payload) =>
        JsonSerializer.Serialize(new { version = 1, type, payload });

    private static string Validity(string source, bool supported, int sourceVersion = 0) =>
        Message("mermaid.validityChanged", new
        {
            sessionId = SessionId,
            source,
            language = "mermaid",
            sourceVersion,
            supported,
            reason = supported ? string.Empty : "unsupported",
        });

    private static string VersionedValidity(string source, int sourceVersion, bool supported) =>
        Message("mermaid.validityChanged", new
        {
            sessionId = SessionId,
            source,
            language = "mermaid",
            sourceVersion,
            supported,
            reason = supported ? string.Empty : "unsupported",
        });

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static DocumentBuffer CreateBuffer(string text) =>
        DocumentBuffer.Create(
            TabId,
            "document.md",
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Mixed,
            "\n",
            InitialVersion);

    private static MermaidBlockSnapshot SnapshotFor(DocumentBuffer buffer, string source)
    {
        var from = buffer.Text.IndexOf(source, StringComparison.Ordinal);
        return new MermaidBlockSnapshot(
            SessionId,
            buffer.TabId,
            buffer.Revision,
            from,
            from + source.Length,
            source,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
    }
}
