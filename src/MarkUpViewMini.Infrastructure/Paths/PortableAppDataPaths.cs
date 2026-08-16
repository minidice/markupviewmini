namespace MarkUpViewMini.Infrastructure.Paths;

public sealed class PortableAppDataPaths : AppDataPathsBase
{
    private PortableAppDataPaths(string dataDirectory)
        : base(dataDirectory)
    {
    }

    public static PortableAppDataPaths Create(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return new PortableAppDataPaths(Path.Combine(baseDirectory, "data"));
    }
}
