using System.Windows;

namespace MarkUpViewMini.App.About;

internal interface IAboutDialogService
{
    void Show(AboutDialogKind kind, Window owner);
}
