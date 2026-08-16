using MarkUpViewMini.App.Services;
using MarkUpViewMini.Core.Navigation;

namespace MarkUpViewMini.App.Tests.Services;

public sealed class ExternalOpenServiceTests
{
    [Theory]
    [InlineData(LinkRouteKind.InternalCurrentTab)]
    [InlineData(LinkRouteKind.InternalNewTab)]
    public void Open_rejects_routes_that_were_not_approved_for_external_launch(LinkRouteKind kind)
    {
        var service = new ExternalOpenService();

        var result = service.Open(new LinkRoute(kind, @"C:\Docs\guide.md", null, null));

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }
}
