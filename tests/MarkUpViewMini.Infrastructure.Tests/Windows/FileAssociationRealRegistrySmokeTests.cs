using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MarkUpViewMini.Infrastructure.Windows;
using Microsoft.Win32;

namespace MarkUpViewMini.Infrastructure.Tests.Windows;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileAssociationRealRegistrySmokeCollection
{
    public const string Name = "Real HKCU file association smoke";
}

[Collection(FileAssociationRealRegistrySmokeCollection.Name)]
public sealed class FileAssociationRealRegistrySmokeTests
{
    private const string EnabledVariable = "MARKUPVIEWMINI_RUN_FILE_ASSOC_SMOKE";
    private const string ExecutableVariable = "MARKUPVIEWMINI_FILE_ASSOC_EXE";
    private const string EvidenceVariable = "MARKUPVIEWMINI_FILE_ASSOC_EVIDENCE";

    private static readonly string[] OwnedRoots =
    [
        @"Software\Classes\MarkUpViewMini.md",
        @"Software\Classes\MarkUpViewMini.markdown",
        @"Software\MarkUpViewMini",
    ];

    [Fact]
    public async Task Register_shell_discovery_and_unregister_restore_the_real_current_user_profile()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        var executablePath = Environment.GetEnvironmentVariable(ExecutableVariable);
        Assert.True(File.Exists(executablePath), $"{ExecutableVariable} must name a built executable.");
        executablePath = Path.GetFullPath(executablePath!);

        var before = CaptureProtectedState();
        PreflightMustBeClear(before);
        var launcher = new RecordingProcessLauncher();
        var service = new FileAssociationService(
            new CurrentUserRegistryStore(),
            launcher,
            executablePath);
        var registered = false;
        var shellMd = false;
        var shellMarkdown = false;

        try
        {
            await service.RegisterAsync(executablePath);
            registered = (await service.GetStatusAsync()).IsRegistered;
            Assert.True(registered);

            service.OpenWindowsDefaultAppsSettings();
            var settingsStart = Assert.Single(launcher.Starts);
            Assert.Equal(
                "ms-settings:defaultapps?registeredAppUser=MarkUpViewMini",
                settingsStart.FileName);
            Assert.True(settingsStart.UseShellExecute);

            shellMd = await WaitForShellHandlerAsync(".md", executablePath);
            shellMarkdown = await WaitForShellHandlerAsync(".markdown", executablePath);
            Assert.True(shellMd, "The Shell did not enumerate MarkUpViewMini for .md.");
            Assert.True(shellMarkdown, "The Shell did not enumerate MarkUpViewMini for .markdown.");
        }
        finally
        {
            await service.UnregisterAsync();
        }

        var after = CaptureProtectedState();
        Assert.Equal(Serialize(before), Serialize(after));
        Assert.False((await service.GetStatusAsync()).IsRegistered);

        WriteSanitizedEvidence(new
        {
            schemaVersion = 1,
            completedUtc = DateTimeOffset.UtcNow,
            executableFileName = Path.GetFileName(executablePath),
            preflightClear = true,
            exactRegistryPlanVerified = registered,
            shellEnumeratedMd = shellMd,
            shellEnumeratedMarkdown = shellMarkdown,
            settingsLaunchControlled = true,
            markdownUserChoicePresent = before.MarkdownUserChoice.Exists,
            longMarkdownUserChoicePresent = before.LongMarkdownUserChoice.Exists,
            protectedExplorerUserChoiceSnapshotsCompared = true,
            completeSharedClassTreesCompared = true,
            extensionDefaultsUnchanged = true,
            userChoiceUnchanged = true,
            exactOriginalStateRestored = true,
            protectedDefaultWasNotChosen = true,
            doubleClickDefaultSelection = "manual-user-controlled",
        });
    }

    private static ProtectedState CaptureProtectedState() =>
        new(
            OwnedRoots.Select(CaptureTree).ToArray(),
            CaptureTree(@"Software\Classes\.md"),
            CaptureTree(@"Software\Classes\.markdown"),
            CaptureValue(@"Software\Classes\.md\OpenWithProgids", "MarkUpViewMini.md"),
            CaptureValue(@"Software\Classes\.markdown\OpenWithProgids", "MarkUpViewMini.markdown"),
            CaptureValue(@"Software\RegisteredApplications", "MarkUpViewMini"),
            CaptureTree(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice"),
            CaptureTree(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.markdown\UserChoice"));

    private static void PreflightMustBeClear(ProtectedState state)
    {
        Assert.All(state.OwnedTrees, tree =>
            Assert.False(tree.Exists, $"Preflight collision at HKCU\\{tree.Path}."));
        Assert.False(state.MarkdownOpenWith.Exists, "Preflight collision in .md OpenWithProgids.");
        Assert.False(state.LongMarkdownOpenWith.Exists, "Preflight collision in .markdown OpenWithProgids.");
        Assert.False(state.RegisteredApplication.Exists, "Preflight collision in RegisteredApplications.");
    }

    private static RegistryTreeSnapshot CaptureTree(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        if (key is null)
        {
            return new RegistryTreeSnapshot(path, false, [], []);
        }

        var values = key.GetValueNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => CaptureValue(key, path, name))
            .ToArray();
        var children = key.GetSubKeyNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => CaptureTree(path + "\\" + name))
            .ToArray();
        return new RegistryTreeSnapshot(path, true, values, children);
    }

    private static RegistryValueSnapshotForSmoke CaptureValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key is null
            ? new RegistryValueSnapshotForSmoke(path, name, false, null, null)
            : CaptureValue(key, path, name);
    }

    private static RegistryValueSnapshotForSmoke CaptureValue(RegistryKey key, string path, string name)
    {
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new RegistryValueSnapshotForSmoke(path, name, false, null, null);
        }

        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new RegistryValueSnapshotForSmoke(path, name, true, kind.ToString(), Normalize(value));
    }

    private static string? Normalize(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        string[] strings => string.Join("\u001f", strings),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task<bool> WaitForShellHandlerAsync(string extension, string executablePath)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (EnumerateShellHandlerNames(extension).Any(name =>
                    string.Equals(Path.GetFullPath(name), executablePath, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static IReadOnlyList<string> EnumerateShellHandlerNames(string extension)
    {
        var result = new List<string>();
        var hresult = SHAssocEnumHandlers(extension, 0, out var enumerator);
        Marshal.ThrowExceptionForHR(hresult);
        try
        {
            while (enumerator.Next(1, out var handler, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (handler.GetName(out var name) == 0 && !string.IsNullOrWhiteSpace(name))
                    {
                        result.Add(name);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(handler);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return result;
    }

    private static string Serialize(ProtectedState state) =>
        JsonSerializer.Serialize(state);

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(
        string pszExtra,
        uint afFilter,
        out IEnumAssocHandlers ppEnumHandler);

    [ComImport]
    [Guid("973810AE-9599-4B88-9E4D-6EE98C9552DA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(
            uint celt,
            [MarshalAs(UnmanagedType.Interface)] out IAssocHandler rgelt,
            out uint pceltFetched);
    }

    [ComImport]
    [Guid("F04061AC-1659-4A3F-A954-775AA57FC083")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig]
        int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    }

    private sealed record ProtectedState(
        RegistryTreeSnapshot[] OwnedTrees,
        RegistryTreeSnapshot MarkdownClassTree,
        RegistryTreeSnapshot LongMarkdownClassTree,
        RegistryValueSnapshotForSmoke MarkdownOpenWith,
        RegistryValueSnapshotForSmoke LongMarkdownOpenWith,
        RegistryValueSnapshotForSmoke RegisteredApplication,
        RegistryTreeSnapshot MarkdownUserChoice,
        RegistryTreeSnapshot LongMarkdownUserChoice);

    private sealed record RegistryTreeSnapshot(
        string Path,
        bool Exists,
        RegistryValueSnapshotForSmoke[] Values,
        RegistryTreeSnapshot[] Children);

    private sealed record RegistryValueSnapshotForSmoke(
        string Path,
        string Name,
        bool Exists,
        string? Kind,
        string? Value);

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Starts { get; } = [];

        public void Start(ProcessStartInfo startInfo) => Starts.Add(startInfo);
    }
}
