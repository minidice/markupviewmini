using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.Core.Tests.Localization;

public sealed class LanguageResolverTests
{
    private static LanguageResolver Shipping() => new(["en", "ko"], "en");

    [Theory]
    [InlineData("ko", "ko")]
    [InlineData("en", "en")]
    public void System_choice_follows_a_machine_whose_language_we_ship(string systemCode, string expected)
    {
        Assert.Equal(expected, Shipping().Resolve(LanguagePreference.SystemCode, systemCode));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("fr")]
    [InlineData("zh")]
    [InlineData("")]
    [InlineData(null)]
    public void System_choice_falls_back_to_English_on_a_machine_we_do_not_ship(string? systemCode)
    {
        // Break caught: leaving the app in whatever half-translated state an unknown machine
        // language implies. The documented rule is English for everything we do not ship.
        Assert.Equal("en", Shipping().Resolve(LanguagePreference.SystemCode, systemCode));
    }

    [Fact]
    public void An_explicit_choice_wins_over_the_machine_language()
    {
        var resolver = Shipping();

        Assert.Equal("ko", resolver.Resolve("ko", "ja"));
        Assert.Equal("en", resolver.Resolve("en", "ko"));
    }

    [Fact]
    public void An_explicit_choice_we_no_longer_ship_falls_back_rather_than_following_the_machine()
    {
        // Break caught: quietly switching a user who asked for one language onto whatever the
        // machine is set to. They made an explicit choice; the documented fallback is less
        // surprising than a third language they never picked.
        Assert.Equal("en", Shipping().Resolve("de", "ko"));
    }

    [Fact]
    public void Stored_codes_survive_stray_casing_and_padding()
    {
        var resolver = Shipping();

        Assert.Equal("ko", resolver.Resolve("  KO ", "en"));
        Assert.Equal("ko", resolver.Resolve(LanguagePreference.SystemCode, "Ko"));
    }

    [Theory]
    [InlineData("kor")]
    [InlineData("k")]
    [InlineData("k1")]
    [InlineData("  ")]
    public void A_malformed_stored_code_is_treated_as_following_the_system(string code)
    {
        // Sanitize reduces junk to the system choice, so a hand-edited settings file cannot
        // pin the app to a language that does not exist.
        Assert.Equal(LanguagePreference.SystemCode, LanguagePreference.Sanitize(code));
        Assert.Equal("ko", Shipping().Resolve(code, "ko"));
    }

    [Fact]
    public void A_code_for_a_language_we_have_not_shipped_yet_is_preserved_when_stored()
    {
        // Break caught: sanitising against the shipped catalogue would reset a forward-looking
        // choice on every launch, so upgrading would never pick it back up.
        Assert.Equal("de", LanguagePreference.Sanitize("de"));
        Assert.False(Shipping().IsSupported("de"));
    }

    [Fact]
    public void The_supported_list_drives_resolution_so_adding_a_language_needs_no_logic_change()
    {
        var widened = new LanguageResolver(["en", "ko", "ja"], "en");

        Assert.Equal("ja", widened.Resolve(LanguagePreference.SystemCode, "ja"));
        Assert.Equal("ja", widened.Resolve("ja", "en"));
    }

    [Fact]
    public void A_fallback_outside_the_supported_list_is_rejected_at_construction()
    {
        // A fallback we cannot display would strand every unsupported machine with no text.
        Assert.Throws<ArgumentException>(() => new LanguageResolver(["ko"], "en"));
        Assert.Throws<ArgumentException>(() => new LanguageResolver(["en"], LanguagePreference.SystemCode));
    }
}
