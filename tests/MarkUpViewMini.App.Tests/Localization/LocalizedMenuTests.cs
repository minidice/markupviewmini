using System.Windows;
using System.Windows.Controls;
using MarkUpViewMini.App;
using MarkUpViewMini.App.Localization;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Paths;

namespace MarkUpViewMini.App.Tests.Localization;

public sealed class LocalizedMenuTests
{
    [Fact]
    public void Menu_headers_come_from_the_catalogue_and_follow_a_language_switch()
    {
        // Break caught: a dotted key inside an indexer binding path ("[menu.file]") is exactly
        // the shape WPF's path parser could mangle, and a broken binding shows as an empty
        // menu rather than an exception - so only reading the rendered header proves it works.
        RunOnSta(() =>
        {
            App.RegisterEncodingProviders();
            var testRoot = Path.Combine(Path.GetTempPath(), $"markup-view-mini-i18n-{Guid.NewGuid():N}");
            var window = new MainWindow(
                new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
                PortableAppDataPaths.Create(testRoot));
            var original = AppLocalization.Source.CurrentCode;
            try
            {
                var settingsMenu = (MenuItem)window.FindName("SettingsMenu");
                var catalog = AppLocalization.Source.Catalog;

                AppLocalization.Source.SetLanguage("ko");
                window.UpdateLayout();
                Assert.Equal(catalog.Get("ko", "menu.settings"), settingsMenu.Header);
                Assert.NotEqual("menu.settings", settingsMenu.Header);

                AppLocalization.Source.SetLanguage("en");
                window.UpdateLayout();
                Assert.Equal(catalog.Get("en", "menu.settings"), settingsMenu.Header);
            }
            finally
            {
                AppLocalization.Source.SetLanguage(original);
                window.Close();
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
        });
    }

    [Fact]
    public void Switching_language_announces_a_single_indexer_refresh()
    {
        // WPF re-reads every "{Binding [key]}" when the indexer itself changes; without that
        // notification the strings would only update on the next unrelated layout pass.
        var source = new LocalizationSource(CatalogResources.Load(), "en");
        var announced = new List<string?>();
        source.PropertyChanged += (_, args) => announced.Add(args.PropertyName);

        source.SetLanguage("ko");

        Assert.Contains("Item[]", announced);
        Assert.Equal("ko", source.CurrentCode);
    }

    [Fact]
    public void Re_selecting_the_current_language_says_nothing()
    {
        var source = new LocalizationSource(CatalogResources.Load(), "ko");
        var announced = 0;
        source.PropertyChanged += (_, _) => announced++;

        source.SetLanguage("ko");

        Assert.Equal(0, announced);
    }

    [Fact]
    public void An_unshipped_language_resolves_to_the_fallback_rather_than_blank_text()
    {
        var source = new LocalizationSource(CatalogResources.Load(), "de");

        Assert.Equal("en", source.CurrentCode);
        Assert.Equal(source.Catalog.Get("en", "menu.file"), source["menu.file"]);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
