using MarkUpViewMini.App.Localization;
using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.App.Tests.Localization;

public sealed class CatalogResourcesTests
{
    private static readonly LocalizationCatalog Catalog = CatalogResources.Load();

    [Fact]
    public void Every_shipped_catalogue_is_discovered_from_the_assembly()
    {
        // Discovery is by resource scan, not a hard-coded list, so a language that was added
        // but never wired up would show here as a missing code rather than silently vanishing.
        Assert.Contains("ko", Catalog.SupportedCodes);
        Assert.Contains("en", Catalog.SupportedCodes);
        Assert.Equal("en", Catalog.FallbackCode);
    }

    [Fact]
    public void Catalogues_translate_exactly_the_same_keys()
    {
        // Break caught: a key added to one language only. The missing side would silently fall
        // back to English at runtime, so nothing would look broken until a user noticed one
        // stray English label in an otherwise Korean menu.
        var reference = LoadKeys(Catalog.FallbackCode).OrderBy(key => key, StringComparer.Ordinal);

        foreach (var code in Catalog.SupportedCodes)
        {
            var keys = LoadKeys(code);
            Assert.NotEmpty(keys);
            Assert.Equal(reference, keys.OrderBy(key => key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void No_translation_is_blank()
    {
        // A blank string reads as a broken feature; an untranslated one at least reads as text.
        foreach (var code in Catalog.SupportedCodes)
        {
            foreach (var key in LoadKeys(code))
            {
                Assert.False(string.IsNullOrWhiteSpace(Catalog.Get(code, key)), $"{code}:{key}");
            }
        }
    }

    [Fact]
    public void An_unknown_key_shows_the_key_rather_than_an_empty_control()
    {
        Assert.Equal("no.such.key", Catalog.Get("ko", "no.such.key"));
        Assert.Equal("no.such.key", Catalog.Get("en", "no.such.key"));
    }

    [Fact]
    public void An_unknown_language_reads_the_fallback_catalogue()
    {
        Assert.Equal(Catalog.Get("en", "menu.file"), Catalog.Get("de", "menu.file"));
    }

    [Fact]
    public void Korean_and_English_actually_differ_so_a_switch_is_visible()
    {
        Assert.NotEqual(Catalog.Get("ko", "menu.file"), Catalog.Get("en", "menu.file"));
    }

    private static IReadOnlyCollection<string> LoadKeys(string code)
    {
        using var stream = typeof(CatalogResources).Assembly
            .GetManifestResourceStream($"{CatalogResourcesAccess.Prefix}{code}.json")!;
        var entries = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(stream)!;
        return entries.Keys;
    }

    private static class CatalogResourcesAccess
    {
        internal const string Prefix = "MarkUpViewMini.App.Localization.";
    }
}
