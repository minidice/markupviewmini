namespace MarkUpViewMini.Infrastructure.Paths;

public interface IAppDataPaths
{
    string DataDirectory { get; }

    string SettingsFile { get; }

    string SessionFile { get; }

    string RecoveryDirectory { get; }

    string LogsDirectory { get; }

    string WebView2Directory { get; }
}
