using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class LanguageSettingsViewModelTests
{
    private static LanguageSettingsViewModel Create(
        string stored,
        string systemCode,
        List<string>? persisted = null) =>
        new(
            new LanguageResolver(["en", "ko"], "en"),
            systemCode,
            stored,
            code => persisted?.Add(code));

    [Fact]
    public void Opens_on_the_stored_choice_rather_than_the_resolved_language()
    {
        // "System" is a choice, not a language. A Korean machine following the system must
        // show System ticked, not Korean - otherwise the user cannot tell the two apart.
        var model = Create(LanguagePreference.SystemCode, "ko");

        Assert.True(model.IsSystem);
        Assert.False(model.IsKorean);
        Assert.Equal("ko", model.ResolvedCode);
    }

    [Fact]
    public void Choosing_a_language_persists_the_code_and_moves_the_tick()
    {
        var persisted = new List<string>();
        var model = Create(LanguagePreference.SystemCode, "ko", persisted);

        model.ChooseCommand.Execute("en");

        Assert.Equal(["en"], persisted);
        Assert.True(model.IsEnglish);
        Assert.False(model.IsSystem);
        Assert.Equal("en", model.ResolvedCode);
    }

    [Fact]
    public void Choosing_system_stores_the_empty_code_so_the_machine_decides()
    {
        var persisted = new List<string>();
        var model = Create("en", "ko", persisted);

        model.ChooseCommand.Execute("");

        Assert.Equal([LanguagePreference.SystemCode], persisted);
        Assert.True(model.IsSystem);
        Assert.Equal("ko", model.ResolvedCode);
    }

    [Fact]
    public void Re_picking_the_current_choice_restores_the_tick_without_writing_settings()
    {
        // Break caught: a checkable menu item toggles itself off on click before the binding
        // is re-read, so re-picking the active language left the menu showing nothing chosen.
        var persisted = new List<string>();
        var model = Create("ko", "ko", persisted);
        var raised = new List<string?>();
        model.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        model.ChooseCommand.Execute("ko");

        Assert.Empty(persisted);
        Assert.True(model.IsKorean);
        Assert.Contains(nameof(LanguageSettingsViewModel.IsKorean), raised);
    }

    [Fact]
    public void Announces_a_changed_display_language_so_surfaces_can_re_render()
    {
        var model = Create(LanguagePreference.SystemCode, "ko");
        var announced = 0;
        model.ResolvedLanguageChanged += (_, _) => announced++;

        model.ChooseCommand.Execute("en");

        Assert.Equal(1, announced);
    }

    [Fact]
    public void Stays_quiet_when_the_choice_changes_but_the_displayed_language_does_not()
    {
        // On a Korean machine, System and Korean render identically. Re-rendering every
        // surface for a no-op change would flicker the editor for nothing.
        var model = Create(LanguagePreference.SystemCode, "ko");
        var announced = 0;
        model.ResolvedLanguageChanged += (_, _) => announced++;

        model.ChooseCommand.Execute("ko");

        Assert.Equal(0, announced);
        Assert.True(model.IsKorean);
    }

    [Fact]
    public void A_stored_choice_we_do_not_ship_still_displays_the_fallback()
    {
        var model = Create("de", "ko");

        Assert.Equal("en", model.ResolvedCode);
        Assert.False(model.IsSystem);
    }
}
