using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebSurfaceActivationTransactionTests
{
    private static readonly Guid TabId = Guid.Parse("2649f27c-f8ad-42f8-ae29-20d77ee2342b");

    [Fact]
    public void Failed_post_does_not_publish_a_response_or_enable_document_commands()
    {
        var coordinator = ReadyCoordinator(out var activation);

        Assert.Throws<InvalidOperationException>(() =>
            WebSurfaceActivationTransaction.TryPost(
                coordinator,
                activation,
                Guid.NewGuid(),
                revision: 7,
                () => throw new InvalidOperationException("post failed")));

        Assert.Null(coordinator.CurrentResponse);
        Assert.False(coordinator.HasActiveDocument);
        Assert.Equal(WebSurfaceLifecycleState.Ready, coordinator.State);
    }

    [Fact]
    public void Successful_post_commits_the_exact_response_after_the_post_returns()
    {
        var coordinator = ReadyCoordinator(out var activation);
        var requestId = Guid.NewGuid();
        var responseDuringPost = new List<WebResponseContext?>();

        Assert.True(WebSurfaceActivationTransaction.TryPost(
            coordinator,
            activation,
            requestId,
            revision: 7,
            () => responseDuringPost.Add(coordinator.CurrentResponse)));

        Assert.Equal([null], responseDuringPost);
        Assert.Equal(new WebResponseContext(requestId, TabId, 7), coordinator.CurrentResponse);
        Assert.True(coordinator.HasActiveDocument);
    }

    private static WebSurfaceActivationCoordinator ReadyCoordinator(out WebActivationStamp activation)
    {
        var coordinator = new WebSurfaceActivationCoordinator();
        activation = coordinator.BeginActivation(TabId);
        coordinator.MarkAwaitingReady(activation, TabId);
        Assert.True(coordinator.TryMarkReady(TabId));
        return coordinator;
    }
}
