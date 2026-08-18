using MarkUpViewMini.App.Localization;
using System.ComponentModel;
using System.Diagnostics;

namespace MarkUpViewMini.App.About;

internal interface IAboutLinkLauncher
{
    bool TryOpen(Uri uri, out string? errorMessage);
}

internal sealed class AboutLinkLauncher : IAboutLinkLauncher
{
    private static string LinkOpenFailureMessage => Strings.Get("about.linkOpenFailed");
    private static readonly HashSet<string> AllowedUrls =
    [
        "https://ministool.com/",
        "https://mdvm.ministool.com/",
        "https://github.com/minidice/markupviewmini",
    ];

    private readonly Action<ProcessStartInfo> startProcess;

    internal AboutLinkLauncher()
        : this(static startInfo => Process.Start(startInfo))
    {
    }

    internal AboutLinkLauncher(Action<ProcessStartInfo> startProcess)
    {
        this.startProcess = startProcess;
    }

    public bool TryOpen(Uri uri, out string? errorMessage)
    {
        if (!IsAllowed(uri))
        {
            errorMessage = LinkOpenFailureMessage;
            return false;
        }

        try
        {
            startProcess(new ProcessStartInfo(uri.OriginalString)
            {
                UseShellExecute = true,
            });
            errorMessage = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            errorMessage = LinkOpenFailureMessage;
            return false;
        }
    }

    internal static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedUrls.Contains(uri.OriginalString);
}
