namespace MarkUpViewMini.Infrastructure.Windows;

public sealed record FileAssociationStatus(bool IsRegistered);

public interface IFileAssociationService
{
    Task RegisterAsync(string executablePath);

    Task UnregisterAsync();

    Task<FileAssociationStatus> GetStatusAsync();

    void OpenWindowsDefaultAppsSettings();
}
