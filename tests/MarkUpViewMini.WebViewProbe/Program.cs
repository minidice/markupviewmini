using System.IO;
using System.Windows;
using MarkUpViewMini.App.Mermaid;
using MarkUpViewMini.Core.Mermaid;
using MarkUpViewMini.Infrastructure.Paths;
using Microsoft.Web.WebView2.Core;

namespace MarkUpViewMini.WebViewProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            return 2;
        }

        var dataDirectory = Path.GetFullPath(args[0]);
        var paths = new ProbePaths(dataDirectory);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            var environment = CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: paths.WebView2Directory)
                .GetAwaiter()
                .GetResult();
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var owner = new Window
            {
                Width = 1,
                Height = 1,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            owner.Show();
            const string source = "flowchart LR\nA --> B";
            var snapshot = new MermaidBlockSnapshot(
                Guid.NewGuid(),
                Guid.NewGuid(),
                0,
                0,
                source.Length,
                source,
                new string('0', 64));
            var dialog = new MermaidEditDialog(
                paths,
                environment,
                (_, _) => MermaidApplyResult.Applied);
            _ = dialog.ShowAsync(
                    new MermaidEditRequest(snapshot),
                    owner,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            owner.Close();
            application.Shutdown();
            Console.WriteLine("mermaid-production-probe-complete");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private sealed class ProbePaths(string dataDirectory) : IAppDataPaths
    {
        public string DataDirectory { get; } = dataDirectory;
        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public string SessionFile => Path.Combine(DataDirectory, "session.json");
        public string RecoveryDirectory => Path.Combine(DataDirectory, "recovery");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string WebView2Directory => Path.Combine(DataDirectory, "webview2");
    }
}
