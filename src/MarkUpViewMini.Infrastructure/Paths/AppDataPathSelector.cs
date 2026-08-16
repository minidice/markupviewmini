namespace MarkUpViewMini.Infrastructure.Paths;

public static class AppDataPathSelector
{
    public static IAppDataPaths Select(
        AppDistributionKind kind,
        string executableDirectory,
        string localAppData) =>
        kind switch
        {
            AppDistributionKind.Portable => PortableAppDataPaths.Create(executableDirectory),
            AppDistributionKind.Installed => InstalledAppDataPaths.Create(localAppData),
            AppDistributionKind.Msix => MsixAppDataPaths.Create(localAppData),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown app distribution."),
        };
}

public abstract class AppDataPathsBase : IAppDataPaths
{
    protected AppDataPathsBase(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        SettingsFile = CreateChildPath("settings.json");
        SessionFile = CreateChildPath("session.json");
        RecoveryDirectory = CreateChildPath("recovery");
        LogsDirectory = CreateChildPath("logs");
        WebView2Directory = CreateChildPath("webview2");
    }

    public string DataDirectory { get; }

    public string SettingsFile { get; }

    public string SessionFile { get; }

    public string RecoveryDirectory { get; }

    public string LogsDirectory { get; }

    public string WebView2Directory { get; }

    private string CreateChildPath(string child)
    {
        if (Path.IsPathRooted(child) ||
            child.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "." or ".."))
        {
            throw new ArgumentException("App-data child paths must stay under the data directory.", nameof(child));
        }

        var path = Path.GetFullPath(Path.Combine(DataDirectory, child));
        var relative = Path.GetRelativePath(DataDirectory, path);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("App-data child paths must stay under the data directory.", nameof(child));
        }

        return path;
    }
}
