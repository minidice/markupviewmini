using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using MarkUpViewMini.App.Mermaid;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Mermaid;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Infrastructure.Paths;
using Microsoft.Web.WebView2.Core;

namespace MarkUpViewMini.App.Tests.Mermaid;

public sealed class MermaidEditingAcceptanceTests
{
    private static readonly Guid RequestId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private const string FirstSource = "flowchart LR\nA --> B";
    private const string SecondSource = "flowchart TD\nX --> Y";
    private const string Replacement = "flowchart LR\nA --> C";
    private const string Markdown = "before\n```mermaid\n" + FirstSource +
        "\n```\nbetween\n```mermaid\n" + SecondSource + "\n```\nafter";

    [Fact]
    public async Task Cancel_keeps_both_blocks_revision_dirty_state_and_recovery_unchanged()
    {
        // Break caught: an in-progress modal source leaks through Cancel into the authoritative document.
        var scenario = await Scenario.OpenAsync();
        using var bridge = scenario.CreateBridge();
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Changed(scenario.Snapshot.SessionId, Replacement, 1)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
            supported = true,
            reason = "",
        })));

        Assert.True(bridge.TryHandleMessage(Message("mermaid.cancel", new
        {
            sessionId = scenario.Snapshot.SessionId,
        })));

        var result = await bridge.Completion;
        Assert.False(result.IsConfirmed);
        Assert.Equal(FirstSource, result.Source);
        Assert.Equal(Markdown, scenario.Tab.Text);
        Assert.Equal(scenario.InitialRevision, scenario.Tab.Revision);
        Assert.False(scenario.Tab.IsDirty);
        Assert.Empty(scenario.Recovery);
    }

    [Fact]
    public async Task Confirm_applies_one_revision_marks_dirty_updates_recovery_and_isolates_second_block()
    {
        // Break caught: modal confirmation creates multiple edits, bypasses recovery, or replaces another block.
        var scenario = await Scenario.OpenAsync();
        using var bridge = scenario.CreateBridge();
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Changed(scenario.Snapshot.SessionId, Replacement, 1)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
            supported = true,
            reason = "",
        })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
        })));

        scenario.Apply(await bridge.Completion);

        Assert.Equal(scenario.InitialRevision + 1, scenario.Tab.Revision);
        Assert.True(scenario.Tab.IsDirty);
        Assert.Contains(Replacement, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstSource, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.Contains(SecondSource, scenario.Tab.Text, StringComparison.Ordinal);
        var recovery = Assert.Single(scenario.Recovery);
        Assert.Equal(scenario.InitialRevision + 1, recovery.Revision);
        Assert.Equal(scenario.Tab.Text, recovery.Text);
    }

    [Fact]
    public async Task Confirm_applies_a_non_flowchart_replacement_source_verbatim()
    {
        // Break caught: opening a diagram (or typing a replacement) the strict flowchart
        // parser can't understand used to be a dead end for "시각 편집" - the safe parser gate
        // blocked the action from ever opening. The fix moved that decision entirely into the
        // web editor (it now opens a limited, text-only mode for such source instead of
        // refusing to open); this bridge never validated mermaid syntax itself, it always just
        // trusted whatever the editor reported as "supported". This locks that contract in
        // end-to-end: a non-flowchart replacement, confirmed as supported, must still apply
        // verbatim to the document.
        const string LimitedModeReplacement = "sequenceDiagram\nA->>B: hi";
        var scenario = await Scenario.OpenAsync();
        using var bridge = scenario.CreateBridge();
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Changed(scenario.Snapshot.SessionId, LimitedModeReplacement, 1)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = LimitedModeReplacement,
            language = "mermaid",
            sourceVersion = 1,
            supported = true,
            reason = "",
        })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = LimitedModeReplacement,
            language = "mermaid",
            sourceVersion = 1,
        })));

        scenario.Apply(await bridge.Completion);

        Assert.Contains(LimitedModeReplacement, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstSource, scenario.Tab.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_is_refused_until_the_editor_reports_supported_current_source()
    {
        // Break caught: the native Confirm path can submit stale or unsupported source before validation completes.
        var scenario = await Scenario.OpenAsync();
        using var bridge = scenario.CreateBridge();
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Changed(
            scenario.Snapshot.SessionId,
            "sequenceDiagram\nA->>B: hi",
            1)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = "sequenceDiagram\nA->>B: hi",
            language = "mermaid",
            sourceVersion = 1,
            supported = false,
            reason = "unsupported-diagram-type",
        })));

        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = "sequenceDiagram\nA->>B: hi",
            language = "mermaid",
            sourceVersion = 1,
        })));
        Assert.False(bridge.Completion.IsCompleted);

        Assert.True(bridge.TryHandleMessage(Changed(scenario.Snapshot.SessionId, Replacement, 2)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 2,
            supported = true,
            reason = "",
        })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 2,
        })));
        Assert.True((await bridge.Completion).IsConfirmed);
    }

    [Fact]
    public async Task Foreign_validity_report_cannot_enable_confirm_for_the_owned_session()
    {
        // Break caught: another local editor session can authorize this dialog's current source.
        var scenario = await Scenario.OpenAsync();
        using var bridge = scenario.CreateBridge();
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.False(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = Guid.NewGuid(),
            source = Replacement,
            language = "mermaid",
            sourceVersion = 0,
            supported = true,
            reason = "",
        })));

        Assert.False(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 0,
        })));
        Assert.False(bridge.Completion.IsCompleted);
    }

    [Fact]
    public async Task Dirty_edit_during_modal_makes_confirmation_stale_without_a_second_recovery_update()
    {
        // Break caught: a stale modal overwrites a newer document edit or schedules recovery for refused content.
        var scenario = await Scenario.OpenAsync();
        using var bridge = scenario.CreateBridge();
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Changed(scenario.Snapshot.SessionId, Replacement, 1)));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
            supported = true,
            reason = "",
        })));
        scenario.DirtyDuringModal("\nnewer edit");
        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = scenario.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
        })));

        var prepared = MermaidEditTransaction.Prepare(
            scenario.Tab.Buffer!,
            scenario.Snapshot,
            (await bridge.Completion).Source);

        Assert.Equal(MermaidApplyResult.StaleRevision, prepared.Result);
        Assert.Contains(FirstSource, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Replacement, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.EndsWith("newer edit", scenario.Tab.Text, StringComparison.Ordinal);
        Assert.Single(scenario.Recovery);
    }

    [Fact]
    public async Task Closed_modal_does_not_own_the_recreated_document_WebView()
    {
        // Break caught: a completed modal retains browser ownership and prevents safe document-surface recreation.
        var scenario = await Scenario.OpenAsync();
        var bridge = scenario.CreateBridge();
        Assert.True(bridge.TryCancel());
        Assert.False((await bridge.Completion).IsConfirmed);
        bridge.Dispose();
        var mounted = new List<TestControl>();
        var disposed = new List<TestControl>();
        var created = 0;
        using var lifetime = new WebViewControlLifetime<TestControl>(
            () => new TestControl(++created),
            mounted.Add,
            control => mounted.Remove(control),
            disposed.Add);
        var original = lifetime.Current;

        lifetime.Recreate(() => { });

        Assert.NotSame(original, lifetime.Current);
        Assert.Equal([lifetime.Current], mounted);
        Assert.Equal([original], disposed);
    }

    [Theory]
    [InlineData("rendered")]
    [InlineData("editor")]
    public async Task Document_edit_session_drives_typed_action_through_bridge_and_authoritative_shell(
        string actionOrigin)
    {
        // Unit boundary: session/bridge/Shell rules remain independently observable.
        var scenario = await Scenario.OpenAsync();
        var surface = new object();
        var message = WebMessageParser.Parse(EditRequested(scenario, actionOrigin));
        Assert.True(MermaidDocumentEditSession.TryCreate(
            message,
            dialogGeneration: 4,
            surface,
            out var session));
        using var bridge = new MermaidDialogBridge(session.DialogRequest, _ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.changed", new
        {
            sessionId = session.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
        })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
        {
            sessionId = session.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
            supported = true,
            reason = "",
        })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
        {
            sessionId = session.Snapshot.SessionId,
            source = Replacement,
            language = "mermaid",
            sourceVersion = 1,
        })));
        var current = new WebResponseContext(RequestId, scenario.Tab.Id, scenario.Tab.Revision);
        var activations = 0;

        var applied = session.TryApply(
            currentDialogGeneration: 4,
            current,
            scenario.Shell.WindowId,
            scenario.Tab.Buffer!,
            scenario.Shell.HandleDocumentChanged,
            (expected, requestId, revision) =>
            {
                if (current != expected || requestId != RequestId) return false;
                current = new WebResponseContext(requestId, expected.TabId, revision);
                return true;
            },
            () => activations++,
            (await bridge.Completion).Source);

        Assert.Equal(MermaidApplyResult.Applied, applied);
        Assert.Equal(scenario.InitialRevision + 1, scenario.Tab.Revision);
        Assert.True(scenario.Tab.IsDirty);
        Assert.Single(scenario.Recovery);
        Assert.Equal(1, activations);
        Assert.Contains(Replacement, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.Contains(SecondSource, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.False(session.TryCreateFocusMessage(
            4,
            current,
            scenario.Shell.WindowId,
            scenario.Tab.Id,
            scenario.Tab.Revision,
            surface,
            out _));
    }

    [Fact]
    public async Task Document_edit_session_creates_focus_request_only_for_exact_owner_action_and_surface()
    {
        // Unit boundary: typed focus-request ownership is exact before native acknowledgement handling.
        var scenario = await Scenario.OpenAsync();
        var surface = new object();
        var message = WebMessageParser.Parse(EditRequested(scenario, "rendered"));
        Assert.True(MermaidDocumentEditSession.TryCreate(message, 9, surface, out var session));
        var current = new WebResponseContext(RequestId, scenario.Tab.Id, scenario.Tab.Revision);
        using var bridge = new MermaidDialogBridge(session.DialogRequest, _ => { });
        Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
        Assert.True(bridge.TryHandleMessage(Message("mermaid.cancel", new
        {
            sessionId = session.Snapshot.SessionId,
        })));
        Assert.False((await bridge.Completion).IsConfirmed);
        Assert.Equal(Markdown, scenario.Tab.Text);

        Assert.True(session.TryCreateFocusMessage(
            9,
            current,
            scenario.Shell.WindowId,
            scenario.Tab.Id,
            scenario.Tab.Revision,
            surface,
            out var json));
        using var focus = JsonDocument.Parse(json);
        Assert.Equal("mermaid.focusRequested", focus.RootElement.GetProperty("type").GetString());
        Assert.Equal(RequestId.ToString(), focus.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(scenario.Shell.WindowId.ToString(),
            focus.RootElement.GetProperty("windowId").GetString());
        Assert.Equal(scenario.Tab.Id.ToString(), focus.RootElement.GetProperty("tabId").GetString());
        Assert.Equal(scenario.Tab.Revision,
            focus.RootElement.GetProperty("documentRevision").GetInt64());
        var focusPayload = focus.RootElement.GetProperty("payload");
        Assert.Equal(2, focusPayload.EnumerateObject().Count());
        Assert.Equal("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            focusPayload.GetProperty("actionId").GetString());
        Assert.Equal("rendered", focusPayload.GetProperty("actionOrigin").GetString());

        Assert.False(session.TryCreateFocusMessage(
            9, current, scenario.Shell.WindowId, scenario.Tab.Id, scenario.Tab.Revision,
            new object(), out _));
        Assert.False(session.TryCreateFocusMessage(
            9, current with { Revision = current.Revision + 1 }, scenario.Shell.WindowId,
            scenario.Tab.Id, scenario.Tab.Revision, surface, out _));
        Assert.False(session.TryCreateFocusMessage(
            10, current, scenario.Shell.WindowId, scenario.Tab.Id,
            scenario.Tab.Revision, surface, out _));
        Assert.False(session.TryCreateFocusMessage(
            9, current, Guid.NewGuid(), scenario.Tab.Id,
            scenario.Tab.Revision, surface, out _));
        Assert.False(session.TryCreateFocusMessage(
            9, current, scenario.Shell.WindowId, Guid.NewGuid(),
            scenario.Tab.Revision, surface, out _));
    }

    [Fact]
    public async Task Conflict_reopen_uses_fresh_current_owner_while_preserving_dialog_action_identity()
    {
        // Break caught: strict original-revision eligibility suppresses Reopen after the conflict it handles.
        var scenario = await Scenario.OpenAsync();
        var surface = new object();
        Assert.True(MermaidDocumentEditSession.TryCreate(
            WebMessageParser.Parse(EditRequested(scenario, "editor")),
            dialogGeneration: 12,
            surface,
            out var session));
        scenario.DirtyDuringModal("\nnewer");
        var freshRequestId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        var fresh = new WebResponseContext(freshRequestId, scenario.Tab.Id, scenario.Tab.Revision);

        Assert.True(session.TryCreateReopenMessage(
            MermaidDialogResult.ReopenRequested(session.Snapshot.Source),
            expectedDialogGeneration: 12,
            currentDialogGeneration: 12,
            fresh,
            scenario.Shell.WindowId,
            scenario.Tab.Id,
            scenario.Tab.Revision,
            out var json));

        using var reopen = JsonDocument.Parse(json);
        Assert.Equal(freshRequestId.ToString(), reopen.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(scenario.Tab.Revision,
            reopen.RootElement.GetProperty("documentRevision").GetInt64());
        var payload = reopen.RootElement.GetProperty("payload");
        Assert.Equal("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            payload.GetProperty("actionId").GetString());
        Assert.Equal("editor", payload.GetProperty("actionOrigin").GetString());
    }

    [Theory]
    [InlineData("rendered")]
    [InlineData("editor")]
    public Task Actual_surface_dispatches_action_through_dialog_bridge_and_shell_confirm(
        string actionOrigin) => RunOnStaAsync(async () =>
    {
        // Break caught: acceptance bypasses WebDocumentSurface, native dialog factory, and event wiring.
        var scenario = await Scenario.OpenAsync();
        var coordinator = PrimeCoordinator(scenario.Tab);
        var transport = new RecordingMermaidTransport();
        var dialogFactory = ScriptedDialogFactory.Confirm(Replacement);
        using var focus = new MermaidFocusRestoration((_, token) => Task.Delay(-1, token));
        var owner = new Window();
        using var surface = new WebDocumentSurface(
            coordinator,
            () => transport,
            dialogFactory,
            focus,
            () => scenario.Tab,
            () => owner);
        surface.Configure(
            new TestAppDataPaths(),
            scenario.Shell.WindowId,
            () => [scenario.Tab],
            () => scenario.Tab);
        surface.DocumentChanged += scenario.Shell.HandleDocumentChanged;

        Assert.True(await surface.HandleMermaidMessageAsync(EditRequested(scenario, actionOrigin)));

        Assert.Equal(scenario.InitialRevision + 1, scenario.Tab.Revision);
        Assert.True(scenario.Tab.IsDirty);
        Assert.Single(scenario.Recovery);
        Assert.Contains(Replacement, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.Contains(SecondSource, scenario.Tab.Text, StringComparison.Ordinal);
        Assert.Contains(transport.Posted, json =>
            JsonDocument.Parse(json).RootElement.GetProperty("type").GetString() == "document.activate");
        Assert.Equal(0, transport.FocusCount);
    });

    [Fact]
    public Task Actual_surface_focuses_only_after_exact_ack_and_refuses_replaced_transport() =>
        RunOnStaAsync(async () =>
        {
            // Break caught: surface focuses native WebView before DOM ack or after control replacement.
            var scenario = await Scenario.OpenAsync();
            var coordinator = PrimeCoordinator(scenario.Tab);
            var currentTransport = new RecordingMermaidTransport();
            using var focus = new MermaidFocusRestoration((_, token) => Task.Delay(-1, token));
            var owner = new Window();
            using var surface = new WebDocumentSurface(
                coordinator,
                () => currentTransport,
                ScriptedDialogFactory.Cancel(),
                focus,
                () => scenario.Tab,
                () => owner);
            surface.Configure(
                new TestAppDataPaths(),
                scenario.Shell.WindowId,
                () => [scenario.Tab],
                () => scenario.Tab);

            Assert.True(await surface.HandleMermaidMessageAsync(EditRequested(scenario, "rendered")));
            var focusRequest = Assert.Single(currentTransport.Posted, json =>
                JsonDocument.Parse(json).RootElement.GetProperty("type").GetString() ==
                "mermaid.focusRequested");
            Assert.Equal(0, currentTransport.FocusCount);

            var exactAck = FocusAck(focusRequest);
            Assert.True(await surface.HandleMermaidMessageAsync(exactAck));
            Assert.Equal(1, currentTransport.FocusCount);

            Assert.True(await surface.HandleMermaidMessageAsync(EditRequested(scenario, "rendered")));
            var staleTransport = currentTransport;
            var secondFocus = staleTransport.Posted.Last(json =>
                JsonDocument.Parse(json).RootElement.GetProperty("type").GetString() ==
                "mermaid.focusRequested");
            currentTransport = new RecordingMermaidTransport();

            Assert.True(await surface.HandleMermaidMessageAsync(FocusAck(secondFocus)));
            Assert.Equal(1, staleTransport.FocusCount);
            Assert.Equal(0, currentTransport.FocusCount);

            var beforeRerender = coordinator.CurrentResponse!.Value;
            Assert.True(coordinator.TryUpdateCurrentResponse(
                beforeRerender,
                Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
                beforeRerender.Revision));
            Assert.False(focus.HasPending);
            Assert.True(await surface.HandleMermaidMessageAsync(FocusAck(secondFocus)));
            Assert.Equal(1, staleTransport.FocusCount);
            Assert.Equal(0, currentTransport.FocusCount);
        });

    [Fact]
    public Task Actual_surface_conflict_reopen_posts_fresh_current_snapshot_owner() =>
        RunOnStaAsync(async () =>
        {
            // Break caught: native conflict Reopen is suppressed by the original edit revision.
            var scenario = await Scenario.OpenAsync();
            var coordinator = PrimeCoordinator(scenario.Tab);
            var original = coordinator.CurrentResponse!.Value;
            var freshRequestId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
            var transport = new RecordingMermaidTransport();
            var dialogFactory = ScriptedDialogFactory.ConflictReopen(Replacement, () =>
            {
                scenario.DirtyDuringModal("\nnewer");
                Assert.True(coordinator.TryUpdateCurrentResponse(
                    original,
                    freshRequestId,
                    scenario.Tab.Revision));
            });
            using var focus = new MermaidFocusRestoration((_, token) => Task.Delay(-1, token));
            var owner = new Window();
            using var surface = new WebDocumentSurface(
                coordinator,
                () => transport,
                dialogFactory,
                focus,
                () => scenario.Tab,
                () => owner);
            surface.Configure(
                new TestAppDataPaths(),
                scenario.Shell.WindowId,
                () => [scenario.Tab],
                () => scenario.Tab);

            Assert.True(await surface.HandleMermaidMessageAsync(EditRequested(scenario, "editor")));

            var json = Assert.Single(transport.Posted, item =>
                JsonDocument.Parse(item).RootElement.GetProperty("type").GetString() ==
                "mermaid.reopenRequested");
            using var reopen = JsonDocument.Parse(json);
            Assert.Equal(freshRequestId.ToString(), reopen.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(scenario.Tab.Revision,
                reopen.RootElement.GetProperty("documentRevision").GetInt64());
            Assert.Equal("editor", reopen.RootElement.GetProperty("payload")
                .GetProperty("actionOrigin").GetString());
        });

    private static string EditRequested(Scenario scenario, string actionOrigin) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            type = "mermaid.editRequested",
            requestId = RequestId,
            windowId = scenario.Shell.WindowId,
            tabId = scenario.Tab.Id,
            documentRevision = scenario.Tab.Revision,
            payload = new
            {
                from = scenario.Snapshot.From,
                to = scenario.Snapshot.To,
                source = scenario.Snapshot.Source,
                sourceHash = scenario.Snapshot.SourceHash,
                actionId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
                actionOrigin,
            },
        });

    private static string FocusAck(string focusRequest)
    {
        using var request = JsonDocument.Parse(focusRequest);
        var root = request.RootElement;
        return JsonSerializer.Serialize(new
        {
            version = 1,
            type = "mermaid.focusCompleted",
            requestId = root.GetProperty("requestId").GetString(),
            windowId = root.GetProperty("windowId").GetString(),
            tabId = root.GetProperty("tabId").GetString(),
            documentRevision = root.GetProperty("documentRevision").GetInt64(),
            payload = new
            {
                actionId = root.GetProperty("payload").GetProperty("actionId").GetString(),
                actionOrigin = root.GetProperty("payload").GetProperty("actionOrigin").GetString(),
            },
        });
    }

    private static WebSurfaceActivationCoordinator PrimeCoordinator(DocumentTabViewModel tab)
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        var activation = coordinator.BeginActivation(tab.Id);
        coordinator.MarkAwaitingReady(activation, tab.Id);
        Assert.True(coordinator.TryMarkReady(tab.Id));
        Assert.True(coordinator.TryRecordPosted(activation, RequestId, tab.Revision));
        return coordinator;
    }

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class RecordingMermaidTransport : IMermaidSurfaceTransport
    {
        internal List<string> Posted { get; } = [];

        internal int FocusCount { get; private set; }

        public object Control => this;

        public CoreWebView2Environment? EditorEnvironment => null;

        public void PostMessage(string json) => Posted.Add(json);

        public void Focus() => FocusCount++;
    }

    private sealed class ScriptedDialogFactory : IMermaidEditDialogFactory
    {
        private readonly string? replacement;
        private readonly bool reopenOnConflict;
        private readonly Action? beforeApply;

        private ScriptedDialogFactory(string? replacement, bool reopenOnConflict, Action? beforeApply)
        {
            this.replacement = replacement;
            this.reopenOnConflict = reopenOnConflict;
            this.beforeApply = beforeApply;
        }

        internal static ScriptedDialogFactory Confirm(string replacement) =>
            new(replacement, false, null);

        internal static ScriptedDialogFactory Cancel() => new(null, false, null);

        internal static ScriptedDialogFactory ConflictReopen(string replacement, Action beforeApply) =>
            new(replacement, true, beforeApply);

        public IMermaidEditDialog Create(
            IAppDataPaths paths,
            CoreWebView2Environment? environment,
            Func<MermaidBlockSnapshot, string, MermaidApplyResult> apply) =>
            new ScriptedDialog(replacement, reopenOnConflict, beforeApply, apply);
    }

    private sealed class ScriptedDialog : IMermaidEditDialog
    {
        private readonly string? replacement;
        private readonly bool reopenOnConflict;
        private readonly Action? beforeApply;
        private readonly Func<MermaidBlockSnapshot, string, MermaidApplyResult> apply;

        internal ScriptedDialog(
            string? replacement,
            bool reopenOnConflict,
            Action? beforeApply,
            Func<MermaidBlockSnapshot, string, MermaidApplyResult> apply)
        {
            this.replacement = replacement;
            this.reopenOnConflict = reopenOnConflict;
            this.beforeApply = beforeApply;
            this.apply = apply;
        }

        public async Task<MermaidDialogResult> ShowAsync(
            MermaidEditRequest request,
            Window owner,
            CancellationToken cancellationToken)
        {
            using var bridge = new MermaidDialogBridge(request, _ => { });
            Assert.True(bridge.TryHandleMessage(Message("mermaid.ready", new { })));
            if (replacement is null)
            {
                Assert.True(bridge.TryHandleMessage(Message("mermaid.cancel", new
                {
                    sessionId = request.Snapshot.SessionId,
                })));
                return await bridge.Completion;
            }

            Assert.True(bridge.TryHandleMessage(Changed(
                request.Snapshot.SessionId, replacement, sourceVersion: 1)));
            Assert.True(bridge.TryHandleMessage(Message("mermaid.validityChanged", new
            {
                sessionId = request.Snapshot.SessionId,
                source = replacement,
                language = "mermaid",
                sourceVersion = 1,
                supported = true,
                reason = "",
            })));
            Assert.True(bridge.TryHandleMessage(Message("mermaid.confirm", new
            {
                sessionId = request.Snapshot.SessionId,
                source = replacement,
                language = "mermaid",
                sourceVersion = 1,
            })));
            var result = await bridge.Completion;
            beforeApply?.Invoke();
            var applied = apply(request.Snapshot, result.Source);
            if (applied == MermaidApplyResult.Applied)
            {
                return result;
            }

            Assert.True(reopenOnConflict);
            Assert.Contains(applied, new[]
            {
                MermaidApplyResult.StaleRevision,
                MermaidApplyResult.RangeChanged,
            });
            return MermaidDialogResult.ReopenRequested(request.Snapshot.Source);
        }
    }

    private sealed class TestAppDataPaths : IAppDataPaths
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "markup-view-mini-task5-r2");

        public string DataDirectory => Root;
        public string SettingsFile => Path.Combine(Root, "settings.json");
        public string SessionFile => Path.Combine(Root, "session.json");
        public string RecoveryDirectory => Path.Combine(Root, "recovery");
        public string LogsDirectory => Path.Combine(Root, "logs");
        public string WebView2Directory => Path.Combine(Root, "webview2");
    }

    private static string Message(string type, object payload) =>
        JsonSerializer.Serialize(new { version = 1, type, payload });

    private static string Changed(Guid sessionId, string source, int sourceVersion) =>
        Message("mermaid.changed", new
        {
            sessionId,
            source,
            language = "mermaid",
            sourceVersion,
        });

    private sealed class Scenario
    {
        private Scenario(
            ShellViewModel shell,
            DocumentTabViewModel tab,
            MermaidBlockSnapshot snapshot,
            List<DocumentBufferSnapshot> recovery)
        {
            Shell = shell;
            Tab = tab;
            Snapshot = snapshot;
            InitialRevision = tab.Revision;
            Recovery = recovery;
        }

        internal ShellViewModel Shell { get; }
        internal DocumentTabViewModel Tab { get; }
        internal MermaidBlockSnapshot Snapshot { get; }
        internal long InitialRevision { get; }
        internal List<DocumentBufferSnapshot> Recovery { get; }

        internal static async Task<Scenario> OpenAsync()
        {
            App.RegisterEncodingProviders();
            var recovery = new List<DocumentBufferSnapshot>();
            var shell = new ShellViewModel(
                new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
                (_, _, _) => Task.FromResult(new LoadedDocument(
                    Markdown,
                    new EncodingDescriptor("utf-8", false),
                    NewLineKind.Lf,
                    "\n",
                    new DiskFileVersion(Markdown.Length, DateTime.UnixEpoch, new string('a', 64)))),
                (_, _) => Task.CompletedTask,
                scheduleRecovery: buffer => recovery.Add(buffer.CaptureSnapshot()));
            await shell.OpenAsync(
                new DocumentTarget(Path.GetFullPath("mermaid-acceptance.md"), null, null),
                OpenGesture.Normal,
                CancellationToken.None);
            var tab = shell.ActiveTab!;
            var from = tab.Text.IndexOf(FirstSource, StringComparison.Ordinal);
            var snapshot = new MermaidBlockSnapshot(
                Guid.NewGuid(),
                tab.Id,
                tab.Revision,
                from,
                from + FirstSource.Length,
                FirstSource,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FirstSource)))
                    .ToLowerInvariant());
            return new Scenario(shell, tab, snapshot, recovery);
        }

        internal MermaidDialogBridge CreateBridge() =>
            new(new MermaidEditRequest(Snapshot), _ => { });

        internal void Apply(MermaidDialogResult result)
        {
            Assert.True(result.IsConfirmed);
            var prepared = MermaidEditTransaction.Prepare(Tab.Buffer!, Snapshot, result.Source);
            Assert.Equal(MermaidApplyResult.Applied, prepared.Result);
            Shell.HandleDocumentChanged(new DocumentChangedMessage(
                new WebMessageOwner(Guid.NewGuid(), Shell.WindowId, Tab.Id, Tab.Revision),
                Assert.IsType<DocumentEdit>(prepared.Edit)));
        }

        internal void DirtyDuringModal(string text) =>
            Shell.HandleDocumentChanged(new DocumentChangedMessage(
                new WebMessageOwner(Guid.NewGuid(), Shell.WindowId, Tab.Id, Tab.Revision),
                new DocumentEdit(
                    Tab.Revision,
                    [new TextChange(Tab.Text.Length, Tab.Text.Length, text)])));
    }

    private sealed record TestControl(int Id);
}
