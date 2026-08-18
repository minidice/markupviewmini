namespace MarkUpViewMini.Core.Localization;

/// <summary>
/// Every shipped language, and the rule for turning a (language, key) pair into text.
/// </summary>
/// <remarks>
/// The supported-language list is derived from the catalogues themselves, so shipping a new
/// language is a matter of adding a file - no lookup or resolution code changes.
/// </remarks>
public sealed class LocalizationCatalog
{
    private readonly Dictionary<string, LanguageCatalog> byCode;

    public LocalizationCatalog(IEnumerable<LanguageCatalog> catalogues, string fallbackCode)
    {
        ArgumentNullException.ThrowIfNull(catalogues);
        byCode = catalogues.ToDictionary(entry => entry.Code, StringComparer.Ordinal);
        if (byCode.Count == 0)
        {
            throw new ArgumentException("At least one catalogue is required.", nameof(catalogues));
        }

        Resolver = new LanguageResolver(byCode.Keys, fallbackCode);
        Fallback = byCode[Resolver.FallbackCode];
    }

    public LanguageResolver Resolver { get; }

    private LanguageCatalog Fallback { get; }

    public IReadOnlyList<string> SupportedCodes => Resolver.SupportedCodes;

    public string FallbackCode => Resolver.FallbackCode;

    /// <summary>
    /// Looks up display text, falling back from the wanted language to the fallback language
    /// and finally to the key itself.
    /// </summary>
    /// <remarks>
    /// The last step never returns an empty string on purpose: a blank menu item or button
    /// reads as a broken feature, whereas a visible key reads as a missing translation and
    /// says exactly which one to add.
    /// </remarks>
    public string Get(string? languageCode, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var code = LanguagePreference.Sanitize(languageCode);
        if (byCode.TryGetValue(code, out var wanted) && wanted.TryGet(key, out var text))
        {
            return text;
        }

        return Fallback.TryGet(key, out var fallbackText) ? fallbackText : key;
    }
}
