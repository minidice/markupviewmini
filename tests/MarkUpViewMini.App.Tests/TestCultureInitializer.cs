using System.Globalization;
using System.Runtime.CompilerServices;

namespace MarkUpViewMini.App.Tests;

/// <remarks>
/// AppLocalization resolves the app's UI language from CultureInfo.CurrentUICulture the
/// first time it is touched in this process, and several tests assert exact Korean UI
/// strings. Module initializers run before any test code, so this pins the culture ahead
/// of that first touch regardless of the host OS's locale (CI runners default to en-US).
/// </remarks>
internal static class TestCultureInitializer
{
    [ModuleInitializer]
    internal static void PinCulture()
    {
        var culture = new CultureInfo("ko-KR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
