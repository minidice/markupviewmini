using System.Runtime.InteropServices;
using MarkUpViewMini.Infrastructure.Windows;

namespace MarkUpViewMini.Infrastructure.Tests.Windows;

public sealed class ShellLinkShortcutServiceTests : IDisposable
{
    private const string AppUserModelId = "MarkUpViewMini.App";
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        nameof(ShellLinkShortcutServiceTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Prop_variant_matches_the_native_abi_size_for_the_current_architecture()
    {
        // Break caught: PropVariantClear writes beyond an undersized managed PROPVARIANT buffer.
        var expectedSize = IntPtr.Size == 8 ? 24 : 16;

        Assert.Equal(expectedSize, Marshal.SizeOf<PropVariant>());
    }

    [Fact]
    public void App_identity_property_write_is_committed_before_shell_link_persistence()
    {
        // Break caught: SetValue only changes the in-memory property store and the AppUserModelID is lost.
        var propertyStore = new RecordingWritablePropertyStore();

        AppUserModelIdPropertyWriter.Write(propertyStore, AppUserModelId);

        Assert.Equal(["Set:MarkUpViewMini.App", "Commit"], propertyStore.Operations);
    }

    [Fact]
    public void App_identity_property_commit_failure_is_not_silently_accepted()
    {
        // Break caught: IPersistFile.Save runs after a failed Commit and installs a link without ownership identity.
        var expected = new COMException("injected commit failure");
        var propertyStore = new RecordingWritablePropertyStore { CommitFailure = expected };

        var actual = Assert.Throws<COMException>(
            () => AppUserModelIdPropertyWriter.Write(propertyStore, AppUserModelId));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void App_identity_read_disposes_a_partially_populated_value_when_get_value_fails()
    {
        // Break caught: a failed GetValue HRESULT bypasses cleanup of its populated PROPVARIANT.
        var value = new TrackingPropertyValue("partial identity", valueType: 31);
        var reader = new StubPropertyValueReader(value, unchecked((int)0x80004005));

        Assert.Throws<COMException>(() => AppUserModelIdPropertyReader.Read(reader));

        Assert.True(value.IsDisposed);
    }

    [Fact]
    public async Task Start_menu_creation_writes_the_complete_shell_link_contract()
    {
        // Break caught: a shortcut is created without launch, icon, description, or taskbar identity metadata.
        var accessor = new RecordingShellLinkAccessor();
        var service = CreateService(accessor);

        await service.CreateStartMenuShortcutAsync();

        var write = Assert.Single(accessor.Writes);
        Assert.Equal(Path.Combine(root, "Programs"), Path.GetDirectoryName(write.Path));
        Assert.Equal(Path.Combine(root, "app", "MarkUpViewMini.App.exe"), write.Link.TargetPath);
        Assert.Equal(Path.Combine(root, "app"), write.Link.WorkingDirectory);
        Assert.Equal("MarkUpViewMini Markdown viewer", write.Link.Description);
        Assert.Equal(Path.Combine(root, "app", "MarkUpViewMini.App.exe"), write.Link.IconPath);
        Assert.Equal(0, write.Link.IconIndex);
        Assert.Equal(AppUserModelId, write.Link.AppUserModelId);
        Assert.True(File.Exists(Path.Combine(root, "Programs", "MarkUpViewMini.lnk")));
    }

    [Fact]
    public async Task Desktop_creation_uses_only_the_supplied_current_user_desktop()
    {
        // Break caught: desktop creation writes to a public/all-users or Start Menu location.
        var accessor = new RecordingShellLinkAccessor();
        var service = CreateService(accessor);

        await service.CreateDesktopShortcutAsync();

        Assert.Equal(Path.Combine(root, "Desktop"), Path.GetDirectoryName(Assert.Single(accessor.Writes).Path));
        Assert.True(File.Exists(Path.Combine(root, "Desktop", "MarkUpViewMini.lnk")));
        Assert.False(File.Exists(Path.Combine(root, "Programs", "MarkUpViewMini.lnk")));
    }

    [Fact]
    public async Task Creation_rejects_a_foreign_link_without_overwriting_it()
    {
        // Break caught: installing a shortcut silently replaces a pre-existing link owned by another app.
        var accessor = new RecordingShellLinkAccessor();
        var path = Path.Combine(root, "Programs", "MarkUpViewMini.lnk");
        accessor.Seed(path, new ShellLinkSnapshot(
            Path.Combine(root, "other", "Other.exe"),
            root,
            "Other",
            Path.Combine(root, "other", "Other.exe"),
            0,
            "Other.App"));
        var before = File.ReadAllBytes(path);
        var service = CreateService(accessor);

        await Assert.ThrowsAsync<InvalidOperationException>(service.CreateStartMenuShortcutAsync);

        Assert.Empty(accessor.Writes);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Creation_is_idempotent_for_an_owned_link_and_does_not_rewrite_it()
    {
        // Break caught: repeated creation needlessly rewrites an already-owned link and exposes it to partial COM failure.
        var accessor = new RecordingShellLinkAccessor();
        var path = Path.Combine(root, "Programs", "MarkUpViewMini.lnk");
        accessor.Seed(path, OwnedSnapshot());
        var service = CreateService(accessor);

        await service.CreateStartMenuShortcutAsync();
        await service.CreateStartMenuShortcutAsync();

        Assert.Empty(accessor.Writes);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("working directory")]
    [InlineData("description")]
    [InlineData("icon path")]
    [InlineData("icon index")]
    public async Task Creation_replaces_an_owned_link_when_required_metadata_is_stale(
        string staleProperty)
    {
        // Break caught: target plus AppUserModelID alone suppresses repair of stale required metadata.
        var accessor = new RecordingShellLinkAccessor();
        var path = Path.Combine(root, "Programs", "MarkUpViewMini.lnk");
        var stale = staleProperty switch
        {
            "working directory" => OwnedSnapshot() with { WorkingDirectory = root },
            "description" => OwnedSnapshot() with { Description = "stale description" },
            "icon path" => OwnedSnapshot() with { IconPath = Path.Combine(root, "stale.ico") },
            "icon index" => OwnedSnapshot() with { IconIndex = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(staleProperty)),
        };
        accessor.Seed(path, stale);
        var service = CreateService(accessor);

        await service.CreateStartMenuShortcutAsync();

        var write = Assert.Single(accessor.Writes).Link;
        Assert.Equal(Path.Combine(root, "app", "MarkUpViewMini.App.exe"), write.TargetPath);
        Assert.Equal(Path.Combine(root, "app"), write.WorkingDirectory);
        Assert.Equal("MarkUpViewMini Markdown viewer", write.Description);
        Assert.Equal(Path.Combine(root, "app", "MarkUpViewMini.App.exe"), write.IconPath);
        Assert.Equal(0, write.IconIndex);
        Assert.Equal(AppUserModelId, write.AppUserModelId);
        Assert.Equal("fake shell link", File.ReadAllText(path));
    }

    [Fact]
    public async Task Failed_shell_link_write_removes_only_its_temporary_file()
    {
        // Break caught: partial COM persistence leaves a broken final or temporary shortcut behind.
        var accessor = new RecordingShellLinkAccessor
        {
            WriteFailure = new UnauthorizedAccessException("injected write failure"),
        };
        var service = CreateService(accessor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(service.CreateStartMenuShortcutAsync);

        Assert.False(File.Exists(Path.Combine(root, "Programs", "MarkUpViewMini.lnk")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "Programs")));
    }

    [Fact]
    public async Task Removal_deletes_only_links_with_matching_target_and_app_identity()
    {
        // Break caught: removal deletes a foreign or target-tampered shortcut based on filename alone.
        var accessor = new RecordingShellLinkAccessor();
        var startMenuPath = Path.Combine(root, "Programs", "MarkUpViewMini.lnk");
        var desktopPath = Path.Combine(root, "Desktop", "MarkUpViewMini.lnk");
        accessor.Seed(startMenuPath, OwnedSnapshot());
        accessor.Seed(desktopPath, OwnedSnapshot() with { AppUserModelId = "Other.App" });
        var service = CreateService(accessor);

        await service.RemoveOwnedShortcutsAsync();

        Assert.False(File.Exists(startMenuPath));
        Assert.True(File.Exists(desktopPath));
    }

    [Fact]
    public async Task Removal_preserves_links_when_either_ownership_factor_is_wrong()
    {
        // Break caught: matching only target or only AppUserModelID is treated as sufficient ownership.
        var accessor = new RecordingShellLinkAccessor();
        var startMenuPath = Path.Combine(root, "Programs", "MarkUpViewMini.lnk");
        var desktopPath = Path.Combine(root, "Desktop", "MarkUpViewMini.lnk");
        accessor.Seed(startMenuPath, OwnedSnapshot() with
        {
            TargetPath = Path.Combine(root, "other", "Other.exe"),
        });
        accessor.Seed(desktopPath, OwnedSnapshot() with { AppUserModelId = "Other.App" });
        var service = CreateService(accessor);

        await service.RemoveOwnedShortcutsAsync();

        Assert.True(File.Exists(startMenuPath));
        Assert.True(File.Exists(desktopPath));
    }

    [Fact]
    public async Task Removal_does_not_delete_a_foreign_link_replacing_a_verified_owned_path()
    {
        // Break caught: ownership is checked before deletion and a replacement at the same path is deleted.
        var accessor = new RecordingShellLinkAccessor();
        var startMenuPath = Path.Combine(root, "Programs", "MarkUpViewMini.lnk");
        accessor.Seed(startMenuPath, OwnedSnapshot());
        var replacementBytes = "foreign replacement"u8.ToArray();
        var replacementInstalled = false;
        accessor.AfterRead = _ =>
        {
            if (replacementInstalled)
            {
                return;
            }

            replacementInstalled = true;
            accessor.Seed(
                startMenuPath,
                OwnedSnapshot() with { AppUserModelId = "Other.App" },
                replacementBytes);
        };
        var service = CreateService(accessor);

        await service.RemoveOwnedShortcutsAsync();

        Assert.True(File.Exists(startMenuPath));
        Assert.Equal(replacementBytes, File.ReadAllBytes(startMenuPath));
    }

    [Fact]
    public async Task Removal_preserves_a_foreign_quarantined_link_when_its_original_path_is_reoccupied()
    {
        // Break caught: failed rollback strands foreign user data under a hidden quarantine filename.
        var accessor = new RecordingShellLinkAccessor();
        var programsPath = Path.Combine(root, "Programs");
        var startMenuPath = Path.Combine(programsPath, "MarkUpViewMini.lnk");
        var quarantinedBytes = "foreign candidate"u8.ToArray();
        var replacementBytes = "concurrent foreign replacement"u8.ToArray();
        accessor.Seed(
            startMenuPath,
            OwnedSnapshot() with { AppUserModelId = "Other.App" },
            quarantinedBytes);
        var replacementInstalled = false;
        accessor.AfterRead = path =>
        {
            if (replacementInstalled ||
                !Path.GetFileName(path).StartsWith(".MarkUpViewMini.Remove.", StringComparison.Ordinal))
            {
                return;
            }

            replacementInstalled = true;
            accessor.Seed(
                startMenuPath,
                OwnedSnapshot() with { AppUserModelId = "Another.App" },
                replacementBytes);
        };
        var service = CreateService(accessor);

        var failure = await Record.ExceptionAsync(service.RemoveOwnedShortcutsAsync);

        Assert.NotNull(failure);
        Assert.Equal(replacementBytes, File.ReadAllBytes(startMenuPath));
        var preservedPath = Assert.Single(
            Directory.EnumerateFiles(programsPath),
            path => !string.Equals(path, startMenuPath, StringComparison.OrdinalIgnoreCase));
        Assert.False(Path.GetFileName(preservedPath).StartsWith(".", StringComparison.Ordinal));
        Assert.Equal(quarantinedBytes, File.ReadAllBytes(preservedPath));
    }

    [Fact]
    public async Task Status_reports_each_location_only_when_both_ownership_factors_match()
    {
        // Break caught: status reports a tampered link as installed or conflates desktop and Start Menu state.
        var accessor = new RecordingShellLinkAccessor();
        accessor.Seed(Path.Combine(root, "Programs", "MarkUpViewMini.lnk"), OwnedSnapshot());
        accessor.Seed(
            Path.Combine(root, "Desktop", "MarkUpViewMini.lnk"),
            OwnedSnapshot() with { AppUserModelId = "Other.App" });
        var service = CreateService(accessor);

        var status = await service.GetShortcutStatusAsync();

        Assert.True(status.HasStartMenuShortcut);
        Assert.False(status.HasDesktopShortcut);
    }

    [Fact]
    public async Task Shared_gate_serializes_operations_from_different_window_services()
    {
        // Break caught: two windows mutate shared current-user Shell state concurrently.
        var gate = new FileAssociationOperationGate();
        var blockingAccessor = new BlockingShellLinkAccessor();
        blockingAccessor.Seed(
            Path.Combine(root, "Programs", "MarkUpViewMini.lnk"),
            OwnedSnapshot());
        var first = CreateService(blockingAccessor, gate, new ThreadPoolBackgroundExecutor());
        var second = CreateService(
            blockingAccessor,
            gate,
            new ThreadPoolBackgroundExecutor());
        var firstStatus = first.GetShortcutStatusAsync();
        await blockingAccessor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondStatus = second.GetShortcutStatusAsync();
        try
        {
            Assert.False(secondStatus.IsCompleted);
        }
        finally
        {
            blockingAccessor.Release.Set();
        }

        await firstStatus;
        await secondStatus;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private ShellLinkShortcutService CreateService(
        IShellLinkAccessor accessor,
        IFileAssociationOperationGate? gate = null,
        IBackgroundExecutor? executor = null)
    {
        var executablePath = Path.Combine(root, "app", "MarkUpViewMini.App.exe");
        return new ShellLinkShortcutService(
            accessor,
            executablePath,
            executablePath,
            Path.Combine(root, "Programs"),
            Path.Combine(root, "Desktop"),
            executor ?? new InlineBackgroundExecutor(),
            gate ?? new FileAssociationOperationGate());
    }

    private ShellLinkSnapshot OwnedSnapshot() => new(
        Path.Combine(root, "app", "MarkUpViewMini.App.exe"),
        Path.Combine(root, "app"),
        "MarkUpViewMini Markdown viewer",
        Path.Combine(root, "app", "MarkUpViewMini.App.exe"),
        0,
        AppUserModelId);

    private class RecordingShellLinkAccessor : IShellLinkAccessor
    {
        private readonly Dictionary<string, ShellLinkSnapshot> snapshots =
            new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, ShellLinkDefinition Link)> Writes { get; } = [];

        public Exception? WriteFailure { get; init; }

        public Action<string>? AfterRead { get; set; }

        public virtual ShellLinkSnapshot Read(string path)
        {
            if (!snapshots.TryGetValue(path, out var snapshot))
            {
                snapshot = snapshots
                    .Where(pair =>
                        string.Equals(
                            Path.GetDirectoryName(pair.Key),
                            Path.GetDirectoryName(path),
                            StringComparison.OrdinalIgnoreCase) &&
                        !File.Exists(pair.Key))
                    .Select(pair => pair.Value)
                    .Single();
            }

            AfterRead?.Invoke(path);
            return snapshot;
        }

        public virtual void Write(string path, ShellLinkDefinition link)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fake shell link");
            Writes.Add((path, link));
            snapshots[path] = new ShellLinkSnapshot(
                link.TargetPath,
                link.WorkingDirectory,
                link.Description,
                link.IconPath,
                link.IconIndex,
                link.AppUserModelId);
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
        }

        public void Seed(string path, ShellLinkSnapshot snapshot, byte[]? bytes = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes ?? "preexisting shell link"u8.ToArray());
            snapshots[path] = snapshot;
        }
    }

    private sealed class BlockingShellLinkAccessor : RecordingShellLinkAccessor
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public override ShellLinkSnapshot Read(string path)
        {
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
            return base.Read(path);
        }
    }

    private sealed class InlineBackgroundExecutor : IBackgroundExecutor
    {
        public Task RunAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> RunAsync<T>(Func<T> action) => Task.FromResult(action());
    }

    private sealed class RecordingWritablePropertyStore : IWritablePropertyStore
    {
        public List<string> Operations { get; } = [];

        public Exception? CommitFailure { get; init; }

        public void SetAppUserModelId(string value) => Operations.Add($"Set:{value}");

        public void Commit()
        {
            Operations.Add("Commit");
            if (CommitFailure is not null)
            {
                throw CommitFailure;
            }
        }
    }

    private sealed class StubPropertyValueReader(
        IPropertyValue value,
        int result) : IPropertyValueReader
    {
        public int GetValue(out IPropertyValue propertyValue)
        {
            propertyValue = value;
            return result;
        }
    }

    private sealed class TrackingPropertyValue(
        string value,
        ushort valueType) : IPropertyValue
    {
        public ushort ValueType => valueType;

        public bool IsDisposed { get; private set; }

        public string GetString() => value;

        public void Dispose() => IsDisposed = true;
    }
}
