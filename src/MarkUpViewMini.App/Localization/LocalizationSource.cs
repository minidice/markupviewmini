using System.ComponentModel;
using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.App.Localization;

/// <summary>
/// The binding target every localised string in XAML reads through.
/// </summary>
/// <remarks>
/// Exposed as an indexer so XAML can write <c>{Binding [menu.file], Source=...}</c>. Changing
/// the language raises a change for the indexer itself, which is how WPF is told to re-read
/// every such binding at once - that is what makes the switch apply without a restart.
/// </remarks>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    /// <summary>WPF's name for "every indexer binding is stale".</summary>
    private const string IndexerName = "Item[]";

    private readonly LocalizationCatalog catalog;
    private string code;

    public LocalizationSource(LocalizationCatalog catalog, string? languageCode = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        code = catalog.Resolver.Resolve(languageCode, null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => catalog.Get(code, key);

    public string CurrentCode => code;

    public LocalizationCatalog Catalog => catalog;

    /// <summary>Switches the displayed language, refreshing every bound string.</summary>
    public void SetLanguage(string? languageCode)
    {
        var next = catalog.Resolver.Resolve(languageCode, null);
        if (next == code)
        {
            return;
        }

        code = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCode)));
    }
}
