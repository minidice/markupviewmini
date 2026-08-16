using Microsoft.Web.WebView2.Core;

namespace MarkUpViewMini.App.Web;

public enum WebViewProcessRecoveryAction
{
    Ignore,
    RecreateControl,
    Renavigate,
    ShowRetry,
}

public static class WebViewProcessFailurePolicy
{
    public static WebViewProcessRecoveryAction Decide(CoreWebView2ProcessFailedKind kind) =>
        kind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited =>
                WebViewProcessRecoveryAction.RecreateControl,
            CoreWebView2ProcessFailedKind.RenderProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive =>
                WebViewProcessRecoveryAction.Renavigate,
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited or
            CoreWebView2ProcessFailedKind.UtilityProcessExited or
            CoreWebView2ProcessFailedKind.SandboxHelperProcessExited or
            CoreWebView2ProcessFailedKind.GpuProcessExited or
            CoreWebView2ProcessFailedKind.PpapiPluginProcessExited or
            CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited =>
                WebViewProcessRecoveryAction.Ignore,
            _ => WebViewProcessRecoveryAction.ShowRetry,
        };
}
