using System.Reflection;
using System.Text.Json;
using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.App.Localization;

/// <summary>Reads the shipped language catalogues out of the assembly.</summary>
/// <remarks>
/// Catalogues are discovered by scanning embedded resource names rather than being listed
/// here, so shipping a language is exactly one new JSON file - no code and no project edit
/// beyond the wildcard that embeds the folder.
/// </remarks>
public static class CatalogResources
{
    internal const string ResourcePrefix = "MarkUpViewMini.App.Localization.";
    private const string ResourceSuffix = ".json";

    public static LocalizationCatalog Load(string fallbackCode = "en") =>
        Load(typeof(CatalogResources).Assembly, fallbackCode);

    public static LocalizationCatalog Load(Assembly assembly, string fallbackCode)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var catalogues = new List<LanguageCatalog>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var code = name[ResourcePrefix.Length..^ResourceSuffix.Length];
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (entries is not null)
            {
                catalogues.Add(new LanguageCatalog(code, entries));
            }
        }

        return new LocalizationCatalog(catalogues, fallbackCode);
    }
}
