namespace MarkUpViewMini.Infrastructure.Windows;

public sealed class ShellLinkShortcutService : IShortcutService
{
    private const string ShortcutFileName = "MarkUpViewMini.lnk";
    private const string ShortcutDescription = "MarkUpViewMini Markdown viewer";
    private const string AppUserModelId = "MarkUpViewMini.App";

    private readonly IShellLinkAccessor accessor;
    private readonly IBackgroundExecutor backgroundExecutor;
    private readonly IFileAssociationOperationGate operationGate;
    private readonly string executablePath;
    private readonly string startMenuShortcutPath;
    private readonly string desktopShortcutPath;
    private readonly ShellLinkDefinition expectedLink;

    public ShellLinkShortcutService(
        string executablePath,
        string iconPath,
        string currentUserProgramsPath,
        string currentUserDesktopPath)
        : this(
            new ComShellLinkAccessor(),
            executablePath,
            iconPath,
            currentUserProgramsPath,
            currentUserDesktopPath,
            new ThreadPoolBackgroundExecutor(),
            FileAssociationOperationGate.ProcessWide)
    {
    }

    internal ShellLinkShortcutService(
        IShellLinkAccessor accessor,
        string executablePath,
        string iconPath,
        string currentUserProgramsPath,
        string currentUserDesktopPath,
        IBackgroundExecutor backgroundExecutor,
        IFileAssociationOperationGate operationGate)
    {
        this.accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        this.backgroundExecutor = backgroundExecutor ??
            throw new ArgumentNullException(nameof(backgroundExecutor));
        this.operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
        this.executablePath = NormalizeRequiredPath(executablePath, nameof(executablePath));
        var normalizedIconPath = NormalizeRequiredPath(iconPath, nameof(iconPath));
        var programsPath = NormalizeRequiredPath(
            currentUserProgramsPath,
            nameof(currentUserProgramsPath));
        var desktopPath = NormalizeRequiredPath(
            currentUserDesktopPath,
            nameof(currentUserDesktopPath));
        startMenuShortcutPath = Path.Combine(programsPath, ShortcutFileName);
        desktopShortcutPath = Path.Combine(desktopPath, ShortcutFileName);
        expectedLink = new ShellLinkDefinition(
            this.executablePath,
            Path.GetDirectoryName(this.executablePath)!,
            ShortcutDescription,
            normalizedIconPath,
            0,
            AppUserModelId);
    }

    public Task CreateStartMenuShortcutAsync() =>
        RunAsync(() => CreateShortcut(startMenuShortcutPath));

    public Task CreateDesktopShortcutAsync() =>
        RunAsync(() => CreateShortcut(desktopShortcutPath));

    public Task RemoveOwnedShortcutsAsync() => RunAsync(RemoveOwnedShortcuts);

    public Task<ShortcutStatus> GetShortcutStatusAsync() => RunAsync(() => new ShortcutStatus(
        IsOwnedShortcut(startMenuShortcutPath),
        IsOwnedShortcut(desktopShortcutPath)));

    private Task RunAsync(Action action) =>
        operationGate.RunAsync(() => backgroundExecutor.RunAsync(action));

    private Task<T> RunAsync<T>(Func<T> action) =>
        operationGate.RunAsync(() => backgroundExecutor.RunAsync(action));

    private void CreateShortcut(string shortcutPath)
    {
        var replaceOwnedShortcut = false;
        if (File.Exists(shortcutPath))
        {
            var existingLink = accessor.Read(shortcutPath);
            if (!IsOwnedLink(existingLink))
            {
                throw new InvalidOperationException(
                    $"A shortcut not owned by MarkUpViewMini already exists at '{shortcutPath}'.");
            }

            if (GetMismatchedExpectedProperties(existingLink).Count == 0)
            {
                return;
            }

            replaceOwnedShortcut = true;
        }

        var directory = Path.GetDirectoryName(shortcutPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".MarkUpViewMini.{Guid.NewGuid():N}.lnk");
        try
        {
            accessor.Write(temporaryPath, expectedLink);
            var mismatchedProperties = GetMismatchedExpectedProperties(accessor.Read(temporaryPath));
            if (mismatchedProperties.Count != 0)
            {
                throw new InvalidOperationException(
                    $"The shortcut could not be verified before installation: {string.Join(", ", mismatchedProperties)}.");
            }

            if (replaceOwnedShortcut)
            {
                ReplaceOwnedShortcut(shortcutPath, temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, shortcutPath, overwrite: false);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void ReplaceOwnedShortcut(string shortcutPath, string replacementPath)
    {
        var directory = Path.GetDirectoryName(shortcutPath)!;
        var quarantinePath = Path.Combine(
            directory,
            $".MarkUpViewMini.Replace.{Guid.NewGuid():N}.lnk");
        File.Move(shortcutPath, quarantinePath, overwrite: false);
        try
        {
            if (!IsOwnedShortcut(quarantinePath))
            {
                throw new InvalidOperationException(
                    "The shortcut changed ownership before it could be replaced.");
            }

            File.Move(replacementPath, shortcutPath, overwrite: false);
            File.Delete(quarantinePath);
        }
        finally
        {
            RestoreOrPreserveQuarantinedShortcut(quarantinePath, shortcutPath);
        }
    }

    private void RemoveOwnedShortcuts()
    {
        foreach (var shortcutPath in new[] { startMenuShortcutPath, desktopShortcutPath })
        {
            RemoveOwnedShortcut(shortcutPath);
        }
    }

    private void RemoveOwnedShortcut(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(shortcutPath)!;
        var quarantinePath = Path.Combine(
            directory,
            $".MarkUpViewMini.Remove.{Guid.NewGuid():N}.lnk");
        try
        {
            File.Move(shortcutPath, quarantinePath, overwrite: false);
        }
        catch (FileNotFoundException)
        {
            return;
        }

        try
        {
            if (IsOwnedShortcut(quarantinePath))
            {
                File.Delete(quarantinePath);
            }
        }
        finally
        {
            RestoreOrPreserveQuarantinedShortcut(quarantinePath, shortcutPath);
        }
    }

    private static void RestoreOrPreserveQuarantinedShortcut(
        string quarantinePath,
        string originalPath)
    {
        if (!File.Exists(quarantinePath))
        {
            return;
        }

        try
        {
            File.Move(quarantinePath, originalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(quarantinePath) && File.Exists(originalPath))
        {
            var directory = Path.GetDirectoryName(originalPath)!;
            var preservedPath = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(originalPath)} (preserved {Guid.NewGuid():N}).lnk");
            File.Move(quarantinePath, preservedPath, overwrite: false);
            throw new InvalidOperationException(
                $"The original shortcut path was reoccupied; the prior shortcut was preserved as '{Path.GetFileName(preservedPath)}'.");
        }
    }

    private bool IsOwnedShortcut(string shortcutPath)
    {
        if (!File.Exists(shortcutPath) ||
            !string.Equals(Path.GetExtension(shortcutPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsOwnedLink(accessor.Read(shortcutPath));
    }

    private bool IsOwnedLink(ShellLinkSnapshot link) =>
        PathsEqual(link.TargetPath, executablePath) &&
        string.Equals(link.AppUserModelId, AppUserModelId, StringComparison.Ordinal);

    private IReadOnlyList<string> GetMismatchedExpectedProperties(ShellLinkSnapshot link)
    {
        var mismatches = new List<string>();
        if (!PathsEqual(link.TargetPath, expectedLink.TargetPath))
        {
            mismatches.Add("target");
        }

        if (!PathsEqual(link.WorkingDirectory, expectedLink.WorkingDirectory))
        {
            mismatches.Add("working directory");
        }

        if (!string.Equals(link.Description, expectedLink.Description, StringComparison.Ordinal))
        {
            mismatches.Add("description");
        }

        if (!PathsEqual(link.IconPath, expectedLink.IconPath) || link.IconIndex != expectedLink.IconIndex)
        {
            mismatches.Add("icon");
        }

        if (!string.Equals(link.AppUserModelId, expectedLink.AppUserModelId, StringComparison.Ordinal))
        {
            mismatches.Add(
                $"AppUserModelID (key present: {link.AppUserModelIdKeyPresent}, VT: {link.AppUserModelIdValueType})");
        }

        return mismatches;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string NormalizeRequiredPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.GetFullPath(path);
    }
}
