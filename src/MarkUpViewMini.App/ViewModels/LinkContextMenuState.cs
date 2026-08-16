namespace MarkUpViewMini.App.ViewModels;

public sealed record LinkContextMenuState(
    bool CanOpenDefault,
    bool CanOpenInternal,
    bool CanOpenWithWindows,
    bool CanOpenNewTab);
