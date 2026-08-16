using System.Text.Json;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class DocumentChangeMessageTests
{
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid WindowId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid TabId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid BatchId = Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Fact]
    public void Ordinary_change_parses_exact_typed_utf16_ranges_and_owner()
    {
        // Break caught: raw JSON, post-edit/code-point offsets, or an uncorrelated edit crosses the surface.
        var envelope = Envelope(
            "document.changed",
            new
            {
                expectedRevision = 4,
                changes = new[]
                {
                    new { from = 1, to = 3, insertedText = "x🙂" },
                    new { from = 5, to = 5, insertedText = "\r\n" },
                },
            });

        var message = WebMessageParser.ParseDocumentChanged(envelope);

        Assert.Equal(new WebMessageOwner(RequestId, WindowId, TabId, 4), message.Owner);
        Assert.Equal(4, message.Edit.ExpectedRevision);
        Assert.Equal(
            [new TextChange(1, 3, "x🙂"), new TextChange(5, 5, "\r\n")],
            message.Edit.Changes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expectedRevision\":4,\"changes\":[],\"extra\":true}")]
    [InlineData("{\"expectedRevision\":3,\"changes\":[{\"from\":0,\"to\":0,\"insertedText\":\"x\"}]}")]
    [InlineData("{\"expectedRevision\":4,\"changes\":[]}")]
    [InlineData("{\"expectedRevision\":4,\"changes\":[{\"from\":-1,\"to\":0,\"insertedText\":\"x\"}]}")]
    [InlineData("{\"expectedRevision\":4,\"changes\":[{\"from\":2,\"to\":1,\"insertedText\":\"x\"}]}")]
    [InlineData("{\"expectedRevision\":4,\"changes\":[{\"from\":0,\"to\":2,\"insertedText\":\"x\"},{\"from\":1,\"to\":3,\"insertedText\":\"y\"}]}")]
    [InlineData("{\"expectedRevision\":4,\"changes\":[{\"from\":2147483648,\"to\":2147483648,\"insertedText\":\"x\"}]}")]
    [InlineData("{\"expectedRevision\":4,\"changes\":[{\"from\":0,\"to\":0,\"insertedText\":\"x\",\"extra\":true}]}")]
    public void Ordinary_change_rejects_non_exact_stale_or_invalid_ranges(string payload)
    {
        // Break caught: malformed or stale range metadata reaches DocumentBuffer.Apply.
        var envelope = WebMessageParser.Parse(MessageJson("document.changed", payload));

        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentChanged(envelope));
    }

    [Fact]
    public void Ordinary_change_enforces_range_and_inserted_code_unit_limits()
    {
        // Break caught: an ordinary message bypasses the 10,000-range or 16-Mi UTF-16 allocation bound.
        var tooMany = Enumerable.Range(0, 10_001)
            .Select(index => new { from = index, to = index, insertedText = string.Empty })
            .ToArray();
        var tooLarge = new string('x', 16 * 1024 * 1024 + 1);

        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentChanged(Envelope(
                "document.changed",
                new { expectedRevision = 4, changes = tooMany })));
        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentChanged(Envelope(
                "document.changed",
                new
                {
                    expectedRevision = 4,
                    changes = new[] { new { from = 0, to = 0, insertedText = tooLarge } },
                })));
    }

    [Fact]
    public void Edit_messages_require_the_exact_activation_request_and_owner()
    {
        // Break caught: a background tab, stale activation, or duplicate expected revision mutates the current buffer.
        var response = new WebResponseContext(RequestId, TabId, 4);
        var current = Envelope(
            "document.changed",
            new
            {
                expectedRevision = 4,
                changes = new[] { new { from = 0, to = 0, insertedText = "x" } },
            });

        Assert.True(WebViewPolicy.IsCurrentDocumentMessage(current, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            current with { RequestId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            current with { WindowId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            current with { TabId = Guid.NewGuid() }, response, WindowId));
        Assert.False(WebViewPolicy.IsCurrentDocumentMessage(
            current with { DocumentRevision = 3 }, response, WindowId));
    }

    [Fact]
    public void Batch_parser_enforces_exact_shapes_chunk_bound_and_declared_total()
    {
        // Break caught: batch declarations/chunks can expand allocations beyond the atomic protocol limits.
        var start = WebMessageParser.ParseDocumentChangeBatchStart(Envelope(
            "document.changeBatchStart",
            new
            {
                batchId = BatchId,
                expectedRevision = 4,
                changes = new[]
                {
                    new { from = 0, to = 1, insertedLength = 32 * 1024 * 1024 },
                    new { from = 2, to = 2, insertedLength = 32 * 1024 * 1024 },
                },
            }));
        Assert.Equal(64 * 1024 * 1024, start.Changes.Sum(change => (long)change.InsertedLength));

        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentChangeBatchStart(Envelope(
                "document.changeBatchStart",
                new
                {
                    batchId = BatchId,
                    expectedRevision = 4,
                    changes = new[]
                    {
                        new { from = 0, to = 0, insertedLength = 64 * 1024 * 1024 },
                        new { from = 0, to = 0, insertedLength = 1 },
                    },
                })));
        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentChangeBatchChunk(Envelope(
                "document.changeBatchChunk",
                new { batchId = BatchId, changeIndex = 0, offset = 0, text = new string('x', 1024 * 1024 + 1) })));
    }

    [Fact]
    public void Batch_commit_is_atomic_and_rejects_out_of_order_duplicate_or_missing_chunks()
    {
        // Break caught: a partial or reordered large edit becomes visible as a document mutation.
        using var assembler = new DocumentChangeBatchAssembler();
        var start = BatchStart(
            new DocumentChangeDeclaration(0, 1, 3),
            new DocumentChangeDeclaration(2, 2, 2));
        Assert.True(assembler.Start(start));
        Assert.False(assembler.Append(BatchChunk(1, 0, "yz")));
        Assert.Null(assembler.Commit(BatchCommit()));

        Assert.True(assembler.Start(start));
        Assert.True(assembler.Append(BatchChunk(0, 0, "ab")));
        Assert.False(assembler.Append(BatchChunk(0, 0, "ab")));
        Assert.Null(assembler.Commit(BatchCommit()));

        Assert.True(assembler.Start(start));
        Assert.True(assembler.Append(BatchChunk(0, 0, "ab")));
        Assert.Null(assembler.Commit(BatchCommit()));

        Assert.True(assembler.Start(start));
        Assert.True(assembler.Append(BatchChunk(0, 0, "ab")));
        Assert.True(assembler.Append(BatchChunk(0, 2, "c")));
        Assert.True(assembler.Append(BatchChunk(1, 0, "yz")));
        var completed = assembler.Commit(BatchCommit());
        Assert.NotNull(completed);
        Assert.Equal(
            [new TextChange(0, 1, "abc"), new TextChange(2, 2, "yz")],
            completed.Edit.Changes);
    }

    [Fact]
    public void Batch_is_dropped_on_owner_change_supersession_expiry_and_disposal()
    {
        // Break caught: temporary batch memory survives activation changes, timeout, supersession, or surface disposal.
        var now = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
        var assembler = new DocumentChangeBatchAssembler(() => now);
        var first = BatchStart(new DocumentChangeDeclaration(0, 0, 1));
        Assert.True(assembler.Start(first));
        Assert.False(assembler.Append(BatchChunk(0, 0, "x") with
        {
            Owner = first.Owner with { RequestId = Guid.NewGuid() },
        }));
        Assert.Null(assembler.Commit(BatchCommit()));

        Assert.True(assembler.Start(first));
        var second = first with { BatchId = Guid.NewGuid() };
        Assert.True(assembler.Start(second));
        Assert.Null(assembler.Commit(BatchCommit()));

        Assert.True(assembler.Start(first));
        now += TimeSpan.FromSeconds(30);
        Assert.False(assembler.Append(BatchChunk(0, 0, "x")));
        Assert.Null(assembler.Commit(BatchCommit()));

        now -= TimeSpan.FromSeconds(30);
        Assert.True(assembler.Start(first));
        assembler.Dispose();
        Assert.False(assembler.Append(BatchChunk(0, 0, "x")));
        Assert.Null(assembler.Commit(BatchCommit()));
    }

    [Fact]
    public async Task Batch_timer_releases_an_idle_partial_assembly()
    {
        // Break caught: a timed-out large partial batch remains retained until another web message arrives.
        using var assembler = new DocumentChangeBatchAssembler(
            batchLifetime: TimeSpan.FromMilliseconds(20));
        Assert.True(assembler.Start(BatchStart(new DocumentChangeDeclaration(0, 0, 1))));

        await Task.Delay(100);

        Assert.False(assembler.Append(BatchChunk(0, 0, "x")));
        Assert.Null(assembler.Commit(BatchCommit()));
    }

    [Fact]
    public void Repeated_stale_batch_frame_rejections_keep_one_pending_resync_request()
    {
        // Break caught: stale Start/Chunk/Commit each replace the ID and invalidate the first web resync request.
        var tracker = new DocumentResyncTracker();
        var current = new WebResponseContext(RequestId, TabId, 9);

        var staleStart = tracker.GetOrBegin(current, 9);
        var staleChunk = tracker.GetOrBegin(current, 9);
        var staleCommit = tracker.GetOrBegin(current, 9);

        Assert.NotEqual(Guid.Empty, staleStart.RequestId);
        Assert.Equal(staleStart, staleChunk);
        Assert.Equal(staleStart, staleCommit);
    }

    [Fact]
    public void Change_responses_expose_revisions_and_resync_id_without_document_body()
    {
        // Break caught: stale rejection leaks the authoritative document through the bridge.
        var response = new WebResponseContext(RequestId, TabId, 4);
        var resyncRequestId = Guid.Parse("55555555-5555-4555-8555-555555555555");

        var accepted = WebViewPolicy.CreateDocumentChangeAcceptedMessage(response, WindowId, 5);
        var rejected = WebViewPolicy.CreateDocumentChangeRejectedMessage(
            response, WindowId, 6, resyncRequestId);
        using var acceptedJson = JsonDocument.Parse(accepted);
        using var rejectedJson = JsonDocument.Parse(rejected);

        Assert.Equal("document.changeAccepted", acceptedJson.RootElement.GetProperty("type").GetString());
        Assert.Equal(5, acceptedJson.RootElement.GetProperty("documentRevision").GetInt64());
        Assert.Empty(acceptedJson.RootElement.GetProperty("payload").EnumerateObject());
        Assert.Equal("document.changeRejected", rejectedJson.RootElement.GetProperty("type").GetString());
        Assert.Equal(6, rejectedJson.RootElement.GetProperty("documentRevision").GetInt64());
        Assert.Equal(
            resyncRequestId.ToString("D"),
            rejectedJson.RootElement.GetProperty("payload").GetProperty("resyncRequestId").GetString());
        Assert.DoesNotContain("text", rejected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", rejected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Authoritative_tab_accepts_one_expected_revision_and_rejects_its_duplicate()
    {
        // Break caught: duplicate speculative messages both mutate C# or tab projections lag the buffer.
        var tab = new DocumentTabViewModel(new DocumentTarget(@"C:\docs\readme.md", null, null));
        tab.ApplyLoaded(new LoadedDocument(
            "base",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(4, DateTime.UnixEpoch, "baseline")));
        var edit = new DocumentEdit(1, [new TextChange(4, 4, " accepted")]);

        Assert.Equal(2, tab.ApplyEdit(edit));
        Assert.Throws<StaleDocumentRevisionException>(() => tab.ApplyEdit(edit));
        Assert.Equal("base accepted", tab.Text);
        Assert.Equal(2, tab.Revision);
        Assert.True(tab.IsDirty);
        Assert.Equal("readme.md *", tab.DisplayTitle);
    }

    [Fact]
    public void Mode_acknowledgement_parses_as_a_typed_current_owner_event()
    {
        // Break caught: raw mode JSON or an unsupported mode crosses WebDocumentSurface.
        var message = WebMessageParser.ParseDocumentModeChanged(Envelope(
            "document.modeChanged",
            new { mode = "edit" }));

        Assert.Equal(DocumentMode.Edit, message.Mode);
        Assert.Equal(TabId, message.Owner.TabId);
        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentModeChanged(Envelope(
                "document.modeChanged",
                new { mode = "preview" })));
    }

    [Fact]
    public void Ui_hints_require_bounded_selection_and_scroll_payload()
    {
        var message = WebMessageParser.ParseDocumentUiHintsChanged(Envelope(
            "document.uiHintsChanged",
            new
            {
                selection = new { anchor = 7, head = 3 },
                scrollTop = 48.5,
                splitRatio = 0.37,
                find = new { matchCase = true, wholeWord = true, useRegex = false },
            }));

        Assert.Equal(new DocumentUiHints(7, 3, 48.5, 0.37, true, true, false), message.Hints);
        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentUiHintsChanged(Envelope(
                "document.uiHintsChanged",
                new
                {
                    selection = new { anchor = -1, head = 0 },
                    scrollTop = 0,
                    splitRatio = 0.5,
                    find = new { matchCase = false, wholeWord = false, useRegex = false },
                })));
        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentUiHintsChanged(WebMessageParser.Parse(MessageJson(
                "document.uiHintsChanged",
                "{\"selection\":{\"anchor\":0,\"head\":0},\"scrollTop\":1e400,\"splitRatio\":0.5,\"find\":{\"matchCase\":false,\"wholeWord\":false,\"useRegex\":false}}"))));
        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseDocumentUiHintsChanged(Envelope(
                "document.uiHintsChanged",
                new
                {
                    selection = new { anchor = 0, head = 0 },
                    scrollTop = 0,
                    splitRatio = 0.05,
                    find = new { matchCase = false, wholeWord = false, useRegex = false },
                })));

        foreach (var boundary in new[] { 0.1, 0.9 })
        {
            var accepted = WebMessageParser.ParseDocumentUiHintsChanged(Envelope(
                "document.uiHintsChanged",
                new
                {
                    selection = new { anchor = 0, head = 0 },
                    scrollTop = 0,
                    splitRatio = boundary,
                    find = new { matchCase = false, wholeWord = false, useRegex = false },
                }));
            Assert.Equal(boundary, accepted.Hints.SplitRatio);
        }
    }

    [Fact]
    public void Activation_payload_serializes_exact_tab_owned_ui_hints()
    {
        var tab = new DocumentTabViewModel(new DocumentTarget(Path.GetFullPath("hints.md"), null, null));
        tab.ApplyLoaded(new LoadedDocument(
            "0123456789",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(10, DateTime.UnixEpoch, new string('a', 64))),
            new MarkdownDocumentProvider());
        tab.ApplyUiHints(new DocumentUiHints(7, 3, 48.5, 0.37, true, false, true));

        using var json = JsonDocument.Parse(WebViewPolicy.CreateDocumentActivationMessage(
            tab,
            RequestId,
            WindowId));
        var payload = json.RootElement.GetProperty("payload");

        Assert.Equal(7, payload.GetProperty("selection").GetProperty("anchor").GetInt32());
        Assert.Equal(3, payload.GetProperty("selection").GetProperty("head").GetInt32());
        Assert.Equal(48.5, payload.GetProperty("scrollTop").GetDouble());
        Assert.Equal(0.37, payload.GetProperty("splitRatio").GetDouble());
        Assert.True(payload.GetProperty("find").GetProperty("matchCase").GetBoolean());
        Assert.False(payload.GetProperty("find").GetProperty("wholeWord").GetBoolean());
        Assert.True(payload.GetProperty("find").GetProperty("useRegex").GetBoolean());
        Assert.False(payload.GetProperty("find").TryGetProperty("query", out _));
        Assert.Equal(3, payload.GetProperty("find").EnumerateObject().Count());

        using var preferences = JsonDocument.Parse(WebViewPolicy.CreateSetEditorPreferencesMessage(
            new WebResponseContext(RequestId, tab.Id, tab.Revision),
            WindowId,
            tab.UiHints));
        Assert.Equal("document.setEditorPreferences", preferences.RootElement.GetProperty("type").GetString());
        Assert.Equal(0.37, preferences.RootElement.GetProperty("payload").GetProperty("splitRatio").GetDouble());
    }

    private static WebMessageEnvelope Envelope(string type, object payload) =>
        WebMessageParser.Parse(JsonSerializer.Serialize(new
        {
            version = 1,
            type,
            requestId = RequestId,
            windowId = WindowId,
            tabId = TabId,
            documentRevision = 4,
            payload,
        }));

    private static string MessageJson(string type, string payload) =>
        $"{{\"version\":1,\"type\":\"{type}\",\"requestId\":\"{RequestId:D}\",\"windowId\":\"{WindowId:D}\",\"tabId\":\"{TabId:D}\",\"documentRevision\":4,\"payload\":{payload}}}";

    private static DocumentChangeBatchStartMessage BatchStart(params DocumentChangeDeclaration[] changes) =>
        new(new WebMessageOwner(RequestId, WindowId, TabId, 4), BatchId, 4, changes);

    private static DocumentChangeBatchChunkMessage BatchChunk(int changeIndex, int offset, string text) =>
        new(new WebMessageOwner(RequestId, WindowId, TabId, 4), BatchId, changeIndex, offset, text);

    private static DocumentChangeBatchCommitMessage BatchCommit() =>
        new(new WebMessageOwner(RequestId, WindowId, TabId, 4), BatchId);
}
