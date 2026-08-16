namespace MarkUpViewMini.Infrastructure.Windows;

public sealed record ShortcutStatus(
    bool HasStartMenuShortcut,
    bool HasDesktopShortcut);

public interface IShortcutService
{
    Task CreateStartMenuShortcutAsync();

    Task CreateDesktopShortcutAsync();

    Task RemoveOwnedShortcutsAsync();

    Task<ShortcutStatus> GetShortcutStatusAsync();
}
