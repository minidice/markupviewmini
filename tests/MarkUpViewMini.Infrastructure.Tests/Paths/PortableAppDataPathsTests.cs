using MarkUpViewMini.Infrastructure.Paths;

namespace MarkUpViewMini.Infrastructure.Tests.Paths;

public sealed class PortableAppDataPathsTests
{
    [Fact]
    public void Create_places_every_writable_path_under_portable_data_directory()
    {
        var paths = PortableAppDataPaths.Create(@"D:\Apps\MarkUpViewMini");

        Assert.Equal(@"D:\Apps\MarkUpViewMini\data", paths.DataDirectory);
        Assert.Equal(@"D:\Apps\MarkUpViewMini\data\settings.json", paths.SettingsFile);
        Assert.Equal(@"D:\Apps\MarkUpViewMini\data\session.json", paths.SessionFile);
        Assert.Equal(@"D:\Apps\MarkUpViewMini\data\recovery", paths.RecoveryDirectory);
        Assert.Equal(@"D:\Apps\MarkUpViewMini\data\logs", paths.LogsDirectory);
        Assert.Equal(@"D:\Apps\MarkUpViewMini\data\webview2", paths.WebView2Directory);
    }
}
