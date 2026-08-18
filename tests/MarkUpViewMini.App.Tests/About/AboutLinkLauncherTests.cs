using MarkUpViewMini.App.Localization;
using System.ComponentModel;
using System.Diagnostics;
using MarkUpViewMini.App.About;

namespace MarkUpViewMini.App.Tests.About;

public sealed class AboutLinkLauncherTests
{
    [Theory]
    [InlineData("https://ministool.com/", true)]
    [InlineData("https://mdvm.ministool.com/", true)]
    [InlineData("https://github.com/minidice/markupviewmini", true)]
    [InlineData("https://example.invalid/", false)]
    [InlineData("file:///C:/private.txt", false)]
    public void Link_policy_allows_only_the_three_application_https_urls(string value, bool expected)
    {
        Assert.Equal(expected, AboutLinkLauncher.IsAllowed(new Uri(value)));
    }

    [Fact]
    public void Rejected_link_never_reaches_the_process_launcher()
    {
        var processStarts = 0;
        var launcher = new AboutLinkLauncher(_ => processStarts++);

        var opened = launcher.TryOpen(new Uri("https://example.invalid/"), out var errorMessage);

        Assert.False(opened);
        Assert.Equal(0, processStarts);
        Assert.Contains("열 수 없습니다", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowed_link_reaches_the_shell_launcher_with_its_exact_url()
    {
        ProcessStartInfo? started = null;
        var launcher = new AboutLinkLauncher(startInfo => started = startInfo);
        var link = new Uri("https://ministool.com/");

        var opened = launcher.TryOpen(link, out var errorMessage);

        Assert.True(opened);
        Assert.Null(errorMessage);
        Assert.NotNull(started);
        Assert.Equal("https://ministool.com/", started.FileName);
        Assert.True(started.UseShellExecute);
    }

    [Fact]
    public void Launch_failure_returns_a_non_sensitive_local_message()
    {
        var launcher = new AboutLinkLauncher(_ => throw new Win32Exception("sensitive system detail"));

        var opened = launcher.TryOpen(new Uri("https://ministool.com/"), out var errorMessage);

        Assert.False(opened);
        Assert.Equal(Strings.Get("about.linkOpenFailed"), errorMessage);
    }
}
