using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using MarkUpViewMini.Infrastructure.Windows;

namespace MarkUpViewMini.Infrastructure.Tests.Windows;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShellLinkRealShortcutSmokeCollection
{
    public const string Name = "Real current-user shortcut smoke";
}

[Collection(ShellLinkRealShortcutSmokeCollection.Name)]
public sealed class ShellLinkRealShortcutSmokeTests
{
    private const string EnabledVariable = "MARKUPVIEWMINI_RUN_SHORTCUT_SMOKE";
    private const string ExecutableVariable = "MARKUPVIEWMINI_SHORTCUT_EXE";
    private const string EvidenceVariable = "MARKUPVIEWMINI_SHORTCUT_EVIDENCE";
    private const string AppUserModelId = "MarkUpViewMini.App";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Cleanup_runner_continues_after_an_earlier_step_fails()
    {
        var laterStepRan = false;
        var failures = new List<Exception>();

        RunCleanupSteps(
            failures,
            () => throw new IOException("first cleanup failed"),
            () => laterStepRan = true);

        Assert.True(laterStepRan);
        Assert.Single(failures);
    }

    [Fact]
    public async Task Create_read_launch_activate_and_remove_restore_the_current_user_profile()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        var sourceExecutable = Environment.GetEnvironmentVariable(ExecutableVariable);
        Assert.True(File.Exists(sourceExecutable), $"{ExecutableVariable} must name a built executable.");
        sourceExecutable = Path.GetFullPath(sourceExecutable!);
        var programsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Assert.False(string.IsNullOrWhiteSpace(programsPath));
        Assert.False(string.IsNullOrWhiteSpace(desktopPath));
        var startMenuLink = Path.Combine(programsPath, "MarkUpViewMini.lnk");
        var desktopLink = Path.Combine(desktopPath, "MarkUpViewMini.lnk");

        Assert.False(File.Exists(startMenuLink), "Preflight collision at the exact Start Menu link path.");
        Assert.False(File.Exists(desktopLink), "Preflight collision at the exact Desktop link path.");
        Assert.Equal(0, GetProcessCount("MarkUpViewMini.App"));

        string? tempRoot = null;
        string? executablePath = null;
        ShellLinkShortcutService? service = null;
        Process? primary = null;
        Process? secondary = null;
        var failures = new List<Exception>();
        var propertiesVerified = false;
        var primaryWindowCreated = false;
        var primaryMinimizedBeforeForward = false;
        var secondaryForwarded = false;
        var primaryActivated = false;
        try
        {
            tempRoot = CreateVerifiedTempRoot();
            var copiedOutput = Path.Combine(tempRoot, "app");
            CopyDirectory(Path.GetDirectoryName(sourceExecutable)!, copiedOutput);
            var copiedData = Path.Combine(copiedOutput, "data");
            if (Directory.Exists(copiedData))
            {
                Directory.Delete(copiedData, recursive: true);
            }

            executablePath = Path.Combine(copiedOutput, Path.GetFileName(sourceExecutable));
            service = new ShellLinkShortcutService(
                executablePath,
                executablePath,
                programsPath,
                desktopPath);
            await service.CreateStartMenuShortcutAsync();
            await service.CreateDesktopShortcutAsync();
            Assert.Equal(new ShortcutStatus(true, true), await service.GetShortcutStatusAsync());

            var accessor = new ComShellLinkAccessor();
            AssertExactLink(accessor.Read(startMenuLink), executablePath);
            AssertExactLink(accessor.Read(desktopLink), executablePath);
            propertiesVerified = true;

            primary = StartShortcut(startMenuLink, copiedOutput);
            await WaitUntilAsync(
                () =>
                {
                    primary.Refresh();
                    return !primary.HasExited && primary.MainWindowHandle != IntPtr.Zero;
                },
                ProcessTimeout,
                "The shortcut launch did not create a primary WPF window.");
            primaryWindowCreated = true;

            var primaryWindowHandle = primary.MainWindowHandle;
            ShowWindow(primaryWindowHandle, 6);
            await WaitUntilAsync(
                () => IsIconic(primaryWindowHandle),
                ProcessTimeout,
                "The primary WPF window did not enter the minimized pre-activation state.");
            primaryMinimizedBeforeForward = true;

            secondary = StartShortcut(desktopLink, copiedOutput);
            Assert.NotEqual(primary.Id, secondary.Id);
            var secondaryWindowObserved = false;
            var secondaryDeadline = DateTime.UtcNow + ProcessTimeout;
            while (!secondary.HasExited && DateTime.UtcNow < secondaryDeadline)
            {
                secondary.Refresh();
                secondaryWindowObserved |= secondary.MainWindowHandle != IntPtr.Zero;
                await Task.Delay(50);
            }

            Assert.True(secondary.HasExited);
            Assert.Equal(0, secondary.ExitCode);
            Assert.False(secondaryWindowObserved);
            secondaryForwarded = true;
            secondary.Dispose();
            secondary = null;
            primary.Refresh();
            Assert.False(primary.HasExited);
            Assert.Equal(primaryWindowHandle, primary.MainWindowHandle);
            await WaitUntilAsync(
                () => !IsIconic(primaryWindowHandle),
                ProcessTimeout,
                "The forwarded activation did not restore the minimized primary WPF window.");
            primaryActivated = true;
            await WaitUntilAsync(
                () => GetProcessCount("MarkUpViewMini.App") == 1,
                ProcessTimeout,
                "The forwarded secondary process did not leave only the primary process running.");
            Assert.Equal(1, GetProcessCount("MarkUpViewMini.App"));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            if (service is not null)
            {
                try
                {
                    await service.RemoveOwnedShortcutsAsync();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            RunCleanupSteps(
                failures,
                () => StopOwnedProcess(secondary),
                () => StopOwnedProcess(primary),
                () => secondary?.Dispose(),
                () => primary?.Dispose(),
                () =>
                {
                    if (tempRoot is not null)
                    {
                        DeleteDirectoryWithRetry(tempRoot);
                    }
                });
        }

        RunCleanupSteps(
            failures,
            () => Assert.False(
                File.Exists(startMenuLink),
                "The smoke-created Start Menu link remains."),
            () => Assert.False(
                File.Exists(desktopLink),
                "The smoke-created Desktop link remains."),
            () => Assert.Equal(0, GetProcessCount("MarkUpViewMini.App")),
            () => Assert.False(
                tempRoot is not null && Directory.Exists(tempRoot),
                "The verified smoke temp directory remains."));
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "The shortcut smoke failed and one or more cleanup or residue checks also failed.",
                failures);
        }

        WriteSanitizedEvidence(new
        {
            schemaVersion = 1,
            completedUtc = DateTimeOffset.UtcNow,
            executableFileName = Path.GetFileName(executablePath!),
            preflightExactPathsClear = true,
            startMenuCreated = true,
            desktopCreated = true,
            targetWorkingDirectoryDescriptionIconAndIdentityVerified = propertiesVerified,
            appUserModelId = AppUserModelId,
            primaryWindowCreated,
            primaryMinimizedBeforeForward,
            secondaryExitedAndForwarded = secondaryForwarded,
            primaryWindowActivated = primaryActivated,
            noShortcutProcessesOrTempDataRemain = true,
        });
    }

    private static void AssertExactLink(ShellLinkSnapshot link, string executablePath)
    {
        Assert.Equal(executablePath, link.TargetPath, ignoreCase: true);
        Assert.Equal(Path.GetDirectoryName(executablePath), link.WorkingDirectory, ignoreCase: true);
        Assert.Equal("MarkUpViewMini Markdown viewer", link.Description);
        Assert.Equal(executablePath, link.IconPath, ignoreCase: true);
        Assert.Equal(0, link.IconIndex);
        Assert.Equal(AppUserModelId, link.AppUserModelId);
    }

    private static Process StartShortcut(string shortcutPath, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shortcutPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        };

        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Windows Shell did not return the shortcut process.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(failureMessage);
    }

    private static string CreateVerifiedTempRoot()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var runRoot = Path.GetFullPath(Path.Combine(
            tempRoot,
            $"MarkUpViewMini-ShortcutSmoke-{Guid.NewGuid():N}"));
        Assert.StartsWith(tempRoot, runRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            "MarkUpViewMini-ShortcutSmoke-",
            Path.GetFileName(runRoot),
            StringComparison.Ordinal);
        Directory.CreateDirectory(runRoot);
        return runRoot;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void StopOwnedProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        process.Refresh();
        if (process.HasExited)
        {
            return;
        }

        ShowWindow(process.MainWindowHandle, 9);
        if (!process.CloseMainWindow() || !process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
    }

    private static int GetProcessCount(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void RunCleanupSteps(
        ICollection<Exception> failures,
        params Action[] cleanupSteps)
    {
        foreach (var cleanupStep in cleanupSteps)
        {
            try
            {
                cleanupStep();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        do
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
        while (Directory.Exists(path) && DateTime.UtcNow < deadline);

        if (Directory.Exists(path))
        {
            throw new IOException("The verified shortcut smoke temp directory could not be removed.");
        }
    }

    private static void WriteSanitizedEvidence(object evidence)
    {
        var path = Environment.GetEnvironmentVariable(EvidenceVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

}
