using System.Windows.Input;

namespace MarkUpViewMini.App;

public static class MainWindowCommands
{
    public static RoutedUICommand Save { get; } = new("Save", nameof(Save), typeof(MainWindowCommands));

    public static RoutedUICommand SaveAs { get; } = new("Save As", nameof(SaveAs), typeof(MainWindowCommands));

    public static RoutedUICommand Undo { get; } = new("Undo", nameof(Undo), typeof(MainWindowCommands));

    public static RoutedUICommand Redo { get; } = new("Redo", nameof(Redo), typeof(MainWindowCommands));

    public static RoutedUICommand ToggleMode { get; } = new(
        "Toggle Read/Edit Mode",
        nameof(ToggleMode),
        typeof(MainWindowCommands));

    public static RoutedUICommand OpenFind { get; } = new("Find", nameof(OpenFind), typeof(MainWindowCommands));

    public static RoutedUICommand FindNext { get; } = new("Find Next", nameof(FindNext), typeof(MainWindowCommands));

    public static RoutedUICommand FindPrevious { get; } = new("Find Previous", nameof(FindPrevious), typeof(MainWindowCommands));

    public static RoutedUICommand CloseFind { get; } = new("Close Find", nameof(CloseFind), typeof(MainWindowCommands));
}
