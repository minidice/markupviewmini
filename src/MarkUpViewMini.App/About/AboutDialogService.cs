using System.Windows;

namespace MarkUpViewMini.App.About;

internal sealed class AboutDialogService(IAboutMetadataProvider provider, IAboutLinkLauncher launcher)
    : IAboutDialogService
{
    public void Show(AboutDialogKind kind, Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dialog = new AboutDialog(provider.GetContent(kind), launcher)
        {
            Owner = owner,
        };
        dialog.ShowDialog();
    }
}
