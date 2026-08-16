namespace MarkUpViewMini.Infrastructure.Paths;

public sealed class InstalledAppDataPaths : AppDataPathsBase
{
    private InstalledAppDataPaths(string dataDirectory)
        : base(dataDirectory)
    {
    }

    public static InstalledAppDataPaths Create(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        return new InstalledAppDataPaths(Path.Combine(localAppData, "MarkUpViewMini", "data"));
    }
}
