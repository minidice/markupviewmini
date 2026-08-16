using System.Windows;
using MarkUpViewMini.Core.Mermaid;
using MarkUpViewMini.Infrastructure.Paths;
using Microsoft.Web.WebView2.Core;

namespace MarkUpViewMini.App.Mermaid;

internal interface IMermaidEditDialog
{
    Task<MermaidDialogResult> ShowAsync(
        MermaidEditRequest request,
        Window owner,
        CancellationToken cancellationToken);
}

internal interface IMermaidEditDialogFactory
{
    IMermaidEditDialog Create(
        IAppDataPaths paths,
        CoreWebView2Environment? environment,
        Func<MermaidBlockSnapshot, string, MermaidApplyResult> apply);
}

internal sealed class MermaidEditDialogFactory : IMermaidEditDialogFactory
{
    public IMermaidEditDialog Create(
        IAppDataPaths paths,
        CoreWebView2Environment? environment,
        Func<MermaidBlockSnapshot, string, MermaidApplyResult> apply) =>
        new MermaidEditDialog(
            paths,
            environment ?? throw new InvalidOperationException(
                "The document WebView environment is not initialized."),
            apply);
}
