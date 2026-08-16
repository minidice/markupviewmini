using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.Core.Tests.Navigation;

public sealed class NavigationHistoryTests
{
    [Fact]
    public void Push_after_moving_back_drops_the_forward_branch()
    {
        var history = new NavigationHistory();
        var first = Entry(@"C:\Docs\first.md");
        var second = Entry(@"C:\Docs\second.md");
        var abandoned = Entry(@"C:\Docs\abandoned.md");
        var replacement = Entry(@"C:\Docs\replacement.md");
        history.Push(first);
        history.Push(second);
        history.Push(abandoned);
        Assert.True(history.TryMoveBack(out var back));
        Assert.Equal(second, back);

        history.Push(replacement);

        Assert.False(history.TryMoveForward(out _));
        Assert.True(history.TryMoveBack(out back));
        Assert.Equal(second, back);
    }

    [Fact]
    public void Push_coalesces_repeated_identical_entries()
    {
        var history = new NavigationHistory();
        var entry = Entry(@"C:\Docs\guide.md");

        history.Push(entry);
        history.Push(entry);

        Assert.False(history.TryMoveBack(out _));
    }

    [Fact]
    public void Back_and_forward_retain_the_complete_navigation_entry()
    {
        var history = new NavigationHistory();
        var first = new NavigationEntry(
            @"C:\Docs\first.md",
            17,
            "install",
            DocumentMode.Edit,
            218.5);
        var second = new NavigationEntry(
            @"C:\Docs\second.md",
            null,
            null,
            DocumentMode.Read,
            null);
        history.Push(first);
        history.Push(second);

        Assert.True(history.TryMoveBack(out var back));
        Assert.Equal(first, back);
        Assert.True(history.TryMoveForward(out var forward));
        Assert.Equal(second, forward);
    }

    [Fact]
    public void Empty_history_cannot_move_back_or_forward()
    {
        var history = new NavigationHistory();

        Assert.False(history.TryMoveBack(out _));
        Assert.False(history.TryMoveForward(out _));
    }

    [Fact]
    public void Move_availability_reports_cursor_state_without_changing_it()
    {
        // Break caught: Shell history enablement must not probe by moving and corrupting the cursor.
        var history = new NavigationHistory();
        history.Push(Entry(@"C:\Docs\first.md"));
        history.Push(Entry(@"C:\Docs\second.md"));

        Assert.True(history.CanMoveBack);
        Assert.False(history.CanMoveForward);
        Assert.True(history.TryMoveBack(out var back));
        Assert.Equal(@"C:\Docs\first.md", back.Path);
        Assert.False(history.CanMoveBack);
        Assert.True(history.CanMoveForward);
    }

    private static NavigationEntry Entry(string path) =>
        new(path, null, null, DocumentMode.Read, null);
}
