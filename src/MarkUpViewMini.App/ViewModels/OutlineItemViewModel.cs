namespace MarkUpViewMini.App.ViewModels;

public sealed record OutlineItemViewModel(
    int Level,
    string Text,
    string Anchor,
    int SourceLine);
