namespace MarkUpViewMini.Core.Localization;

/// <summary>
/// Turns a stored language choice into the catalogue we will actually display.
/// </summary>
/// <remarks>
/// The supported list comes from the catalogues that shipped, so adding a language is a
/// matter of adding a file - this logic does not change. The operating system's language is
/// passed in rather than read here so the rule stays testable without a platform call.
/// </remarks>
public sealed class LanguageResolver
{
    private readonly HashSet<string> supported;

    public LanguageResolver(IEnumerable<string> supportedCodes, string fallbackCode)
    {
        ArgumentNullException.ThrowIfNull(supportedCodes);
        var codes = supportedCodes
            .Select(LanguagePreference.Sanitize)
            .Where(code => code != LanguagePreference.SystemCode)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var fallback = LanguagePreference.Sanitize(fallbackCode);
        if (fallback == LanguagePreference.SystemCode || !codes.Contains(fallback, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The fallback language must be one of the supported languages.",
                nameof(fallbackCode));
        }

        supported = new HashSet<string>(codes, StringComparer.Ordinal);
        SupportedCodes = codes;
        FallbackCode = fallback;
    }

    /// <summary>Every language a catalogue shipped for, in the order given.</summary>
    public IReadOnlyList<string> SupportedCodes { get; }

    /// <summary>The language used whenever the wanted one is unavailable.</summary>
    public string FallbackCode { get; }

    public bool IsSupported(string? code) =>
        supported.Contains(LanguagePreference.Sanitize(code));

    /// <summary>
    /// Resolves the catalogue to display.
    /// </summary>
    /// <param name="preference">The stored choice; empty means "follow the system".</param>
    /// <param name="systemCode">The operating system's UI language, e.g. "ko".</param>
    /// <remarks>
    /// An explicit choice we no longer ship also falls back rather than following the
    /// system: the user asked for a specific language, and quietly swapping in whatever the
    /// machine happens to be set to is a stranger answer than the documented fallback.
    /// </remarks>
    public string Resolve(string? preference, string? systemCode)
    {
        var wanted = LanguagePreference.Sanitize(preference);
        if (wanted != LanguagePreference.SystemCode)
        {
            return supported.Contains(wanted) ? wanted : FallbackCode;
        }

        var system = LanguagePreference.Sanitize(systemCode);
        return supported.Contains(system) ? system : FallbackCode;
    }
}
