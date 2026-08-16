using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.App.Tests.Activation;

public sealed class ActivationRoutingTests
{
    [Fact]
    public void Registry_selects_the_most_recently_active_nonclosing_window()
    {
        var first = new TestWindow("first");
        var second = new TestWindow("second");
        var registry = new ActivationWindowRegistry<TestWindow>();
        registry.Register(first);
        registry.Register(second);
        registry.RecordActivated(second, DateTimeOffset.UnixEpoch.AddSeconds(1));
        registry.RecordActivated(first, DateTimeOffset.UnixEpoch.AddSeconds(2));

        Assert.Same(first, registry.GetOrCreate(() => new TestWindow("unexpected")));

        registry.RecordClosing(first);

        Assert.Same(second, registry.GetOrCreate(() => new TestWindow("unexpected")));
    }

    [Fact]
    public void Registry_creates_exactly_one_window_when_no_nonclosing_window_exists()
    {
        var closing = new TestWindow("closing");
        var created = new TestWindow("created");
        var createCount = 0;
        var registry = new ActivationWindowRegistry<TestWindow>();
        registry.Register(closing);
        registry.RecordClosing(closing);

        var actual = registry.GetOrCreate(() =>
        {
            createCount++;
            return created;
        });

        Assert.Same(created, actual);
        Assert.Equal(1, createCount);
    }

    [Fact]
    public void Registry_breaks_equal_timestamp_ties_by_activation_order()
    {
        var first = new TestWindow("first");
        var second = new TestWindow("second");
        var registry = new ActivationWindowRegistry<TestWindow>();
        registry.Register(first);
        registry.Register(second);
        registry.RecordActivated(first, DateTimeOffset.UnixEpoch);
        registry.RecordActivated(second, DateTimeOffset.UnixEpoch);

        Assert.Same(second, registry.GetOrCreate(() => new TestWindow("unexpected")));
    }

    [Fact]
    public void Registry_uses_activation_order_when_the_wall_clock_moves_backward()
    {
        var first = new TestWindow("first");
        var second = new TestWindow("second");
        var registry = new ActivationWindowRegistry<TestWindow>();
        registry.Register(first);
        registry.Register(second);
        registry.RecordActivated(first, DateTimeOffset.UnixEpoch.AddHours(1));
        registry.RecordActivated(second, DateTimeOffset.UnixEpoch);

        Assert.Same(second, registry.GetOrCreate(() => new TestWindow("unexpected")));
    }

    [Fact]
    public async Task Activation_paths_always_open_as_explicit_new_tabs()
    {
        using var shell = CreateShell();
        var cleanPath = Path.GetFullPath("clean.md");
        var firstActivationPath = Path.GetFullPath("first-activation.md");
        var secondActivationPath = Path.GetFullPath("second-activation.md");
        await shell.OpenActivationPathsAsync([cleanPath], CancellationToken.None);
        var cleanTab = Assert.Single(shell.Tabs);

        await shell.OpenActivationPathsAsync(
            [firstActivationPath, secondActivationPath],
            CancellationToken.None);

        Assert.Equal(3, shell.Tabs.Count);
        Assert.Same(cleanTab, shell.Tabs[0]);
        Assert.Equal(cleanPath, shell.Tabs[0].Path);
        Assert.Equal(firstActivationPath, shell.Tabs[1].Path);
        Assert.Equal(secondActivationPath, shell.Tabs[2].Path);
    }

    private static ShellViewModel CreateShell()
    {
        App.RegisterEncodingProviders();
        var registry = new DocumentFormatRegistry([new MarkdownDocumentProvider()]);
        return new ShellViewModel(
            registry,
            (path, _, _) => Task.FromResult(Loaded(path)),
            static (_, _) => Task.CompletedTask,
            static () => { });
    }

    private static LoadedDocument Loaded(string path)
    {
        var text = $"loaded:{Path.GetFileName(path)}";
        return new LoadedDocument(
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(text.Length, DateTime.UnixEpoch, new string('a', 64)));
    }

    private sealed record TestWindow(string Name);
}
