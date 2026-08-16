using MarkUpViewMini.Infrastructure.Paths;

namespace MarkUpViewMini.Infrastructure.Tests.Paths;

public sealed class AppDataPathSelectorTests
{
    [Theory]
    [InlineData(
        AppDistributionKind.Portable,
        @"D:\Applications\MarkUpViewMini",
        @"C:\Users\Avery\AppData\Local",
        @"D:\Applications\MarkUpViewMini\data")]
    [InlineData(
        AppDistributionKind.Installed,
        @"D:\Applications\MarkUpViewMini",
        @"C:\Users\Avery\AppData\Local",
        @"C:\Users\Avery\AppData\Local\MarkUpViewMini\data")]
    [InlineData(
        AppDistributionKind.Msix,
        @"D:\Applications\MarkUpViewMini",
        @"C:\Users\Avery\AppData\Local",
        @"C:\Users\Avery\AppData\Local\MarkUpViewMini\data")]
    public void Select_uses_the_distribution_specific_data_root(
        AppDistributionKind kind,
        string executableDirectory,
        string localAppData,
        string expectedDataDirectory)
    {
        // Break caught: a distribution selects another distribution's writable root.
        var paths = AppDataPathSelector.Select(kind, executableDirectory, localAppData);

        Assert.Equal(expectedDataDirectory, paths.DataDirectory);
        Assert.Equal(Path.Combine(expectedDataDirectory, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(expectedDataDirectory, "session.json"), paths.SessionFile);
        Assert.Equal(Path.Combine(expectedDataDirectory, "recovery"), paths.RecoveryDirectory);
        Assert.Equal(Path.Combine(expectedDataDirectory, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(expectedDataDirectory, "webview2"), paths.WebView2Directory);
    }

    [Theory]
    [InlineData(AppDistributionKind.Portable, @"D:\Applications\MarkUpViewMini\bin\..", @"C:\Users\Avery\AppData\Local")]
    [InlineData(AppDistributionKind.Installed, @"D:\Applications\MarkUpViewMini", @"C:\Users\Avery\AppData\Local\.")]
    [InlineData(AppDistributionKind.Msix, @"D:\Applications\MarkUpViewMini", @"C:\Users\Avery\AppData\Local\.")]
    public void Select_canonicalizes_every_writable_path_under_its_data_root(
        AppDistributionKind kind,
        string executableDirectory,
        string localAppData)
    {
        // Break caught: a writable child can use a rooted or parent component to escape the selected data root.
        var paths = AppDataPathSelector.Select(kind, executableDirectory, localAppData);

        var root = Path.GetFullPath(paths.DataDirectory);
        Assert.Equal(root, paths.DataDirectory);
        foreach (var path in WritablePaths(paths))
        {
            Assert.Equal(Path.GetFullPath(path), path);
            AssertPathIsWithin(root, path);
        }
    }

    private static IEnumerable<string> WritablePaths(IAppDataPaths paths)
    {
        yield return paths.SettingsFile;
        yield return paths.SessionFile;
        yield return paths.RecoveryDirectory;
        yield return paths.LogsDirectory;
        yield return paths.WebView2Directory;
    }

    private static void AssertPathIsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        Assert.False(Path.IsPathRooted(relative));
        Assert.False(relative.Equals("..", StringComparison.Ordinal));
        Assert.False(relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.False(relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
