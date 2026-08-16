using System.Text.Json;
using MarkUpViewMini.App.Mermaid;
using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Mermaid;

public sealed class MermaidFocusRestorationTests
{
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid WindowId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid TabId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid ActionId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly WebResponseContext Current = new(RequestId, TabId, 7);

    [Fact]
    public async Task Exact_positive_ack_is_the_only_path_that_focuses_the_originating_control()
    {
        // Break caught: native WebView focus is stolen before the DOM confirms the exact action.
        var delay = new ManualDelay();
        using var restoration = new MermaidFocusRestoration(delay.WaitAsync);
        var control = new object();
        var posted = new List<string>();
        var focused = 0;
        var pending = restoration.Begin(Request(control), posted.Add);

        Assert.Equal(0, focused);
        Assert.Single(posted);
        Assert.True(restoration.HasPending);

        Assert.True(restoration.TryAcknowledge(
            Ack(), Current, WindowId, control, () => focused++));
        await pending;

        Assert.Equal(1, focused);
        Assert.False(restoration.HasPending);
        Assert.False(restoration.TryAcknowledge(
            Ack(), Current, WindowId, control, () => focused++));
        Assert.Equal(1, focused);
    }

    [Fact]
    public async Task Wrong_owner_action_revision_or_control_never_focuses()
    {
        // Break caught: an ack from another response/action or a recreated control completes focus.
        var delay = new ManualDelay();
        using var restoration = new MermaidFocusRestoration(delay.WaitAsync);
        var control = new object();
        var focused = 0;
        var pending = restoration.Begin(Request(control), _ => { });

        Assert.False(restoration.TryAcknowledge(
            Ack() with { Owner = Ack().Owner with { RequestId = Guid.NewGuid() } },
            Current, WindowId, control, () => focused++));
        Assert.False(restoration.TryAcknowledge(
            Ack() with { Action = Ack().Action with { ActionId = Guid.NewGuid() } },
            Current, WindowId, control, () => focused++));
        Assert.False(restoration.TryAcknowledge(
            Ack() with { Owner = Ack().Owner with { DocumentRevision = 8 } },
            Current, WindowId, control, () => focused++));
        Assert.False(restoration.TryAcknowledge(
            Ack(), Current, WindowId, new object(), () => focused++));
        Assert.False(restoration.TryAcknowledge(
            Ack(), Current with { Revision = 8 }, WindowId, control, () => focused++));

        restoration.Cancel();
        await pending;
        Assert.Equal(0, focused);
    }

    [Fact]
    public async Task Timeout_completes_pending_wait_without_focusing()
    {
        // Break caught: a missing/negative DOM acknowledgement leaves an unbounded focus owner.
        var delay = new ManualDelay();
        using var restoration = new MermaidFocusRestoration(delay.WaitAsync);
        var focused = 0;
        var pending = restoration.Begin(Request(new object()), _ => { });

        delay.Complete();
        await pending;

        Assert.False(restoration.HasPending);
        Assert.Equal(0, focused);
    }

    [Fact]
    public async Task Supersession_cancels_the_old_wait_and_only_the_new_ack_can_focus()
    {
        // Break caught: a late ack for a superseded modal steals focus from the current action.
        var delays = new Queue<ManualDelay>([new ManualDelay(), new ManualDelay()]);
        using var restoration = new MermaidFocusRestoration(
            (_, token) => delays.Dequeue().WaitAsync(TimeSpan.Zero, token));
        var firstControl = new object();
        var secondControl = new object();
        var first = restoration.Begin(Request(firstControl), _ => { });
        var secondRequest = Request(secondControl) with
        {
            Action = new MermaidActionIdentity(Guid.NewGuid(), "editor"),
        };
        var second = restoration.Begin(secondRequest, _ => { });
        var focused = 0;

        await first;
        Assert.False(restoration.TryAcknowledge(
            Ack(), Current, WindowId, firstControl, () => focused++));
        Assert.True(restoration.TryAcknowledge(
            new MermaidFocusCompletedMessage(secondRequest.Owner, secondRequest.Action),
            Current, WindowId, secondControl, () => focused++));
        await second;
        Assert.Equal(1, focused);
    }

    [Fact]
    public async Task Disposal_cancels_pending_wait_and_refuses_late_ack()
    {
        // Break caught: disposed surface lifetime retains an ack callback and focuses later.
        var delay = new ManualDelay();
        var restoration = new MermaidFocusRestoration(delay.WaitAsync);
        var control = new object();
        var focused = 0;
        var pending = restoration.Begin(Request(control), _ => { });

        restoration.Dispose();
        await pending;

        Assert.False(restoration.HasPending);
        Assert.False(restoration.TryAcknowledge(
            Ack(), Current, WindowId, control, () => focused++));
        Assert.Equal(0, focused);
    }

    [Fact]
    public void Focus_completed_parser_requires_exact_typed_action_payload()
    {
        // Break caught: arbitrary or malformed payload is treated as a positive focus acknowledgement.
        var envelope = WebMessageParser.Parse(JsonSerializer.Serialize(new
        {
            version = 1,
            type = "mermaid.focusCompleted",
            requestId = RequestId,
            windowId = WindowId,
            tabId = TabId,
            documentRevision = 7,
            payload = new { actionId = ActionId, actionOrigin = "rendered" },
        }));

        Assert.Equal(Ack(), WebMessageParser.ParseMermaidFocusCompleted(envelope));
        Assert.Throws<WebMessageValidationException>(() =>
            WebMessageParser.ParseMermaidFocusCompleted(envelope with
            {
                Payload = JsonDocument.Parse("{\"actionId\":\"bad\",\"actionOrigin\":\"rendered\"}")
                    .RootElement.Clone(),
            }));
    }

    private static MermaidFocusRequest Request(object control) =>
        new(
            new WebMessageOwner(RequestId, WindowId, TabId, 7),
            new MermaidActionIdentity(ActionId, "rendered"),
            control,
            "{\"type\":\"mermaid.focusRequested\"}");

    private static MermaidFocusCompletedMessage Ack() =>
        new(
            new WebMessageOwner(RequestId, WindowId, TabId, 7),
            new MermaidActionIdentity(ActionId, "rendered"));

    private sealed class ManualDelay
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitAsync(TimeSpan _, CancellationToken cancellationToken) =>
            completion.Task.WaitAsync(cancellationToken);

        internal void Complete() => completion.TrySetResult();
    }
}
