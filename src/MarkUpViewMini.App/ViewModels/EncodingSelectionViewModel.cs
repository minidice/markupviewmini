using System.Collections.ObjectModel;
using System.Text;

namespace MarkUpViewMini.App.ViewModels;

public sealed record EncodingChoice(string DisplayName, Encoding Encoding);

public sealed class EncodingSelectionViewModel : ObservableObject
{
    private EncodingChoice? selected;

    public EncodingSelectionViewModel()
    {
        Options = new ReadOnlyCollection<EncodingChoice>(
        [
            new("한국어 (Windows-949)", Encoding.GetEncoding(949)),
            new("Unicode (UTF-16 LE)", Encoding.Unicode),
            new("Unicode (UTF-16 BE)", Encoding.BigEndianUnicode),
            new("Unicode (UTF-8)", Encoding.UTF8),
        ]);
        selected = Options[0];
    }

    public IReadOnlyList<EncodingChoice> Options { get; }

    public EncodingChoice? Selected
    {
        get => selected;
        set => SetProperty(ref selected, value);
    }
}
