using System.Globalization;
using MarkUpViewMini.App.Localization;
using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.App;

/// <summary>Which languages this build ships, and what the machine is set to.</summary>
/// <remarks>
/// The list comes from the shipped catalogues themselves, so adding a language is exactly one
/// new JSON file - nothing here or in the resolver changes.
/// </remarks>
internal static class AppLanguages
{
    internal const string FallbackCode = "en";

    internal static IReadOnlyList<string> SupportedCodes =>
        AppLocalization.Source.Catalog.SupportedCodes;

    internal static LanguageResolver CreateResolver() => AppLocalization.Source.Catalog.Resolver;

    /// <summary>The operating system's UI language as a two-letter code, e.g. "ko".</summary>
    internal static string SystemCode => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
}
