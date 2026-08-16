namespace MarkUpViewMini.Core.Navigation;

public enum LinkRouteKind
{
    InternalCurrentTab,
    InternalNewTab,
    DefaultBrowser,
    WindowsAssociatedApp
}

public sealed record LinkRoute(LinkRouteKind Kind, string Target, int? Line, string? Anchor);
