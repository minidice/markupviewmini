using System.Globalization;
using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.App.Localization;

/// <summary>
/// The one localisation source the whole UI binds to.
/// </summary>
/// <remarks>
/// A single shared instance rather than one per window: dialogs and every window have to
/// change language together, and XAML can reach a static far more simply than a per-window
/// object.
///
/// It starts on the machine's language, not on the fallback. Settings are read later, so
/// anything shown before then - and anything built outside a window, such as a dialog raised
/// during start-up - would otherwise appear in English on a Korean machine and only switch
/// once the main window finished loading.
/// </remarks>
public static class AppLocalization
{
    public static LocalizationSource Source { get; } = CreateSource();

    private static LocalizationSource CreateSource()
    {
        var catalog = CatalogResources.Load();
        return new LocalizationSource(
            catalog,
            catalog.Resolver.Resolve(
                LanguagePreference.SystemCode,
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName));
    }
}
