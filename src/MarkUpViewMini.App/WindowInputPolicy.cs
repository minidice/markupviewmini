using System.Windows.Input;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.Core.Navigation;

namespace MarkUpViewMini.App;

internal static class WindowInputPolicy
{
    internal static bool IsItemActivationKey(Key key) => key is Key.Enter or Key.Space;

    internal static bool CanExecuteFind(
        DocumentTabViewModel? tab,
        WebResponseContext? response) =>
        IsExactLoadedOwner(tab, response);

    internal static bool CanExecuteModeToggle(
        DocumentTabViewModel? tab,
        WebResponseContext? response) =>
        IsExactLoadedOwner(tab, response) &&
        tab!.CanEdit;

    internal static bool CanExecuteEditorHistory(
        DocumentTabViewModel? tab,
        WebResponseContext? response) =>
        tab?.Mode == DocumentMode.Edit && CanExecuteModeToggle(tab, response);

    internal static bool CanExecuteSave(DocumentTabViewModel? tab) =>
        tab is { Error: null, Revision: > 0, Buffer: not null, CanEdit: true };

    internal static bool CanExecuteSaveAs(DocumentTabViewModel? tab) =>
        tab is { Error: null, Revision: > 0, Buffer: not null };

    private static bool IsExactLoadedOwner(
        DocumentTabViewModel? tab,
        WebResponseContext? response) =>
        tab is { Error: null, Revision: > 0, DiskVersion: not null } &&
        response is { } current &&
        current.TabId == tab.Id &&
        current.Revision == tab.Revision;

    internal static LinkOpenDisposition GetLinkDisposition(ModifierKeys modifiers) =>
        modifiers.HasFlag(ModifierKeys.Control)
            ? LinkOpenDisposition.NewTab
            : LinkOpenDisposition.Default;
}
