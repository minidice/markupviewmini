using System.ComponentModel;
using System.Windows.Input;
using MarkUpViewMini.Core.Localization;

namespace MarkUpViewMini.App.ViewModels;

/// <summary>
/// The language choice shown in the Settings menu.
/// </summary>
/// <remarks>
/// The stored choice and the language actually displayed are separate ideas: "system" is a
/// choice, not a language. Keeping both here means the menu can show what the user picked
/// while the rest of the app asks for what to render.
/// </remarks>
public sealed class LanguageSettingsViewModel : INotifyPropertyChanged
{
    private readonly LanguageResolver resolver;
    private readonly string systemCode;
    private readonly Action<string> persist;
    private string preference;

    public LanguageSettingsViewModel(
        LanguageResolver resolver,
        string systemCode,
        string storedPreference,
        Action<string> persist)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.systemCode = systemCode ?? string.Empty;
        this.persist = persist ?? throw new ArgumentNullException(nameof(persist));
        preference = LanguagePreference.Sanitize(storedPreference);
        ChooseCommand = new DelegateCommand(Choose);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the language to display changed, so surfaces can re-render.</summary>
    public event EventHandler? ResolvedLanguageChanged;

    /// <summary>What the user picked; empty means "follow the system".</summary>
    public string Preference => preference;

    /// <summary>The catalogue to actually display.</summary>
    public string ResolvedCode => resolver.Resolve(preference, systemCode);

    public bool IsSystem => preference == LanguagePreference.SystemCode;

    public bool IsKorean => preference == "ko";

    public bool IsEnglish => preference == "en";

    public ICommand ChooseCommand { get; }

    /// <summary>Applies a stored choice that came from somewhere else (e.g. another window).</summary>
    public void Apply(string? storedPreference) => Choose(storedPreference, persistChoice: false);

    private void Choose(object? parameter) =>
        Choose(parameter as string ?? LanguagePreference.SystemCode, persistChoice: true);

    private void Choose(string? next, bool persistChoice)
    {
        var wanted = LanguagePreference.Sanitize(next);
        if (wanted == preference)
        {
            // Re-assert the checkmarks anyway: clicking an already-checked item toggles it off
            // locally before the binding is consulted, so without this the menu would show
            // nothing selected until something else happened to refresh it.
            RaiseChoiceChanged();
            return;
        }

        var before = ResolvedCode;
        preference = wanted;
        if (persistChoice)
        {
            persist(preference);
        }

        RaiseChoiceChanged();
        if (ResolvedCode != before)
        {
            ResolvedLanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseChoiceChanged()
    {
        foreach (var name in new[]
        {
            nameof(Preference),
            nameof(ResolvedCode),
            nameof(IsSystem),
            nameof(IsKorean),
            nameof(IsEnglish),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }
}
