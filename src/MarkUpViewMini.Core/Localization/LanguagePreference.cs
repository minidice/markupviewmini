namespace MarkUpViewMini.Core.Localization;

/// <summary>
/// The shape of a stored language choice.
/// </summary>
/// <remarks>
/// The choice is persisted as a culture code rather than an enum so that shipping a new
/// language never changes the settings schema - only the catalogue list grows. The empty
/// code means "follow the system".
/// </remarks>
public static class LanguagePreference
{
    /// <summary>Follow the operating system's UI language.</summary>
    public const string SystemCode = "";

    /// <summary>
    /// Reduces a stored or user-supplied code to the shape we persist: either the empty
    /// string or a lower-case two-letter ISO code.
    /// </summary>
    /// <remarks>
    /// This deliberately does NOT check whether the code is one we actually ship. That is
    /// decided later, against the catalogue (see <see cref="LanguageResolver"/>), because a
    /// settings file naming a language we add tomorrow has to survive being read today -
    /// discarding it here would silently reset the user's choice on every launch until the
    /// upgrade landed.
    /// </remarks>
    public static string Sanitize(string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length != 2)
        {
            return SystemCode;
        }

        foreach (var character in trimmed)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z'))
            {
                return SystemCode;
            }
        }

        return trimmed.ToLowerInvariant();
    }
}
