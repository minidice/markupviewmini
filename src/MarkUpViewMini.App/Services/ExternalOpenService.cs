using System.ComponentModel;
using System.Diagnostics;
using MarkUpViewMini.Core.Navigation;

namespace MarkUpViewMini.App.Services;

public sealed record ExternalOpenResult(bool Succeeded, string? Error);

public sealed class ExternalOpenService
{
    public ExternalOpenResult Open(LinkRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (route.Kind is not LinkRouteKind.DefaultBrowser
            and not LinkRouteKind.WindowsAssociatedApp)
        {
            return new ExternalOpenResult(false, "The route is not approved for external launch.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(route.Target) { UseShellExecute = true });
            return new ExternalOpenResult(true, null);
        }
        catch (Win32Exception exception)
        {
            return new ExternalOpenResult(false, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new ExternalOpenResult(false, exception.Message);
        }
    }
}
