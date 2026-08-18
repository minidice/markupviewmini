namespace MarkUpViewMini.Core.Localization;

/// <summary>One language's strings, keyed by a dotted name such as "menu.file".</summary>
public sealed class LanguageCatalog
{
    private readonly Dictionary<string, string> entries;

    public LanguageCatalog(string code, IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Code = LanguagePreference.Sanitize(code);
        if (Code == LanguagePreference.SystemCode)
        {
            throw new ArgumentException("A catalogue needs a real language code.", nameof(code));
        }

        this.entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Key))
            {
                this.entries[entry.Key] = entry.Value ?? string.Empty;
            }
        }
    }

    public string Code { get; }

    public IReadOnlyCollection<string> Keys => entries.Keys;

    public bool TryGet(string key, out string value) => entries.TryGetValue(key, out value!);
}
