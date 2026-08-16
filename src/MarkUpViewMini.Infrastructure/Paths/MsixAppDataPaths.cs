namespace MarkUpViewMini.Infrastructure.Paths;

public sealed class MsixAppDataPaths : AppDataPathsBase
{
    private MsixAppDataPaths(string dataDirectory)
        : base(dataDirectory)
    {
    }

    public static MsixAppDataPaths Create(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        return new MsixAppDataPaths(Path.Combine(localAppData, "MarkUpViewMini", "data"));
    }
}
