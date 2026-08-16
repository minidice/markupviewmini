using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MarkUpViewMini.App.Mermaid;

internal interface IMermaidSurfaceTransport
{
    object Control { get; }

    CoreWebView2Environment? EditorEnvironment { get; }

    void PostMessage(string json);

    void Focus();
}

internal sealed class WebView2MermaidSurfaceTransport(WebView2 browser) : IMermaidSurfaceTransport
{
    public object Control => browser;

    public CoreWebView2Environment? EditorEnvironment => browser.CoreWebView2?.Environment;

    public void PostMessage(string json) => browser.CoreWebView2.PostWebMessageAsJson(json);

    public void Focus() => browser.Focus();
}
