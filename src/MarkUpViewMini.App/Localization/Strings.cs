using System.Globalization;

namespace MarkUpViewMini.App.Localization;

/// <summary>
/// Catalogue lookups for code that builds text outside a XAML binding.
/// </summary>
/// <remarks>
/// Bindings refresh themselves when the language changes; text built in code does not. Call
/// these at the moment the text is shown - a value cached in a field keeps whatever language
/// was current when it was computed.
/// </remarks>
public static class Strings
{
    public static string Get(string key) => AppLocalization.Source[key];

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, AppLocalization.Source[key], arguments);
}
