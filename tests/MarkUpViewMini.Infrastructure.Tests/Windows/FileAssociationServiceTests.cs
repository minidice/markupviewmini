using System.Diagnostics;
using MarkUpViewMini.Infrastructure.Windows;
using Microsoft.Win32;

namespace MarkUpViewMini.Infrastructure.Tests.Windows;

public sealed class FileAssociationServiceTests
{
    private const string ExecutablePath = @"C:\Program Files\MarkUp View Mini\MarkUpViewMini.App.exe";

    [Fact]
    public async Task Register_writes_distinct_progids_capabilities_and_open_with_without_claiming_defaults()
    {
        // Break caught: registration overwrites a user's extension/default instead of only advertising this app.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);

        await service.RegisterAsync(ExecutablePath);

        Assert.Equal("MarkUpViewMini Markdown 문서 (.md)", registry.GetString(
            @"Software\Classes\MarkUpViewMini.md", null));
        Assert.Null(registry.GetString(
            @"Software\Classes\MarkUpViewMini.md", "FriendlyTypeName"));
        Assert.Equal("\"C:\\Program Files\\MarkUp View Mini\\MarkUpViewMini.App.exe\",0", registry.GetString(
            @"Software\Classes\MarkUpViewMini.md\DefaultIcon", null));
        Assert.Equal("\"C:\\Program Files\\MarkUp View Mini\\MarkUpViewMini.App.exe\" \"%1\"", registry.GetString(
            @"Software\Classes\MarkUpViewMini.md\shell\open\command", null));

        Assert.Equal("MarkUpViewMini Markdown 문서 (.markdown)", registry.GetString(
            @"Software\Classes\MarkUpViewMini.markdown", null));
        Assert.Null(registry.GetString(
            @"Software\Classes\MarkUpViewMini.markdown", "FriendlyTypeName"));
        Assert.Equal("\"C:\\Program Files\\MarkUp View Mini\\MarkUpViewMini.App.exe\" \"%1\"", registry.GetString(
            @"Software\Classes\MarkUpViewMini.markdown\shell\open\command", null));

        Assert.Equal(string.Empty, registry.GetString(
            @"Software\Classes\.md\OpenWithProgids", "MarkUpViewMini.md"));
        Assert.Equal(string.Empty, registry.GetString(
            @"Software\Classes\.markdown\OpenWithProgids", "MarkUpViewMini.markdown"));
        Assert.Equal("MarkUpViewMini", registry.GetString(
            @"Software\MarkUpViewMini\Capabilities", "ApplicationName"));
        Assert.Equal("Markdown 문서를 읽고 편집합니다.", registry.GetString(
            @"Software\MarkUpViewMini\Capabilities", "ApplicationDescription"));
        Assert.Equal("MarkUpViewMini.md", registry.GetString(
            @"Software\MarkUpViewMini\Capabilities\FileAssociations", ".md"));
        Assert.Equal("MarkUpViewMini.markdown", registry.GetString(
            @"Software\MarkUpViewMini\Capabilities\FileAssociations", ".markdown"));
        Assert.Equal(@"Software\MarkUpViewMini\Capabilities", registry.GetString(
            @"Software\RegisteredApplications", "MarkUpViewMini"));

        Assert.DoesNotContain(registry.Writes, write =>
            write.Path.Contains("UserChoice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(registry.Writes, write =>
            (write.Path.Equals(@"Software\Classes\.md", StringComparison.OrdinalIgnoreCase) ||
             write.Path.Equals(@"Software\Classes\.markdown", StringComparison.OrdinalIgnoreCase)) &&
            write.Name is null);
    }

    [Fact]
    public async Task Register_uses_constant_registry_paths_when_the_executable_path_contains_registry_like_text()
    {
        // Break caught: untrusted path text becomes a registry key or value name instead of value data.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry, @"C:\portable\.md\UserChoice\viewer.exe");

        await service.RegisterAsync(@"C:\portable\.md\UserChoice\viewer.exe");

        Assert.All(registry.Writes, write => Assert.DoesNotContain("viewer.exe", write.Path));
        Assert.All(registry.Writes, write => Assert.DoesNotContain("viewer.exe", write.Name ?? string.Empty));
        Assert.Contains(registry.Writes, write =>
            write.Path == @"Software\Classes\MarkUpViewMini.md\shell\open\command" &&
            write.Value == "\"C:\\portable\\.md\\UserChoice\\viewer.exe\" \"%1\"");
    }

    [Fact]
    public async Task Unregister_removes_only_owned_keys_and_values_and_is_idempotent()
    {
        // Break caught: uninstall deletes another application's current extension choice or UserChoice data.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(@"Software\Classes\.md", null, "Other.Editor");
        registry.SetString(@"Software\Classes\.markdown", null, "Other.Editor");
        registry.SetString(@"Software\Classes\.md\UserChoice", "ProgId", "Other.Editor");
        registry.SetString(@"Software\Classes\.md\OpenWithProgids", "Other.Editor", "");

        await service.UnregisterAsync();
        await service.UnregisterAsync();

        Assert.False(registry.KeyExists(@"Software\Classes\MarkUpViewMini.md"));
        Assert.False(registry.KeyExists(@"Software\Classes\MarkUpViewMini.markdown"));
        Assert.False(registry.KeyExists(@"Software\MarkUpViewMini\Capabilities"));
        Assert.Null(registry.GetString(@"Software\Classes\.md\OpenWithProgids", "MarkUpViewMini.md"));
        Assert.Null(registry.GetString(@"Software\Classes\.markdown\OpenWithProgids", "MarkUpViewMini.markdown"));
        Assert.Null(registry.GetString(@"Software\RegisteredApplications", "MarkUpViewMini"));

        Assert.Equal("Other.Editor", registry.GetString(@"Software\Classes\.md", null));
        Assert.Equal("Other.Editor", registry.GetString(@"Software\Classes\.markdown", null));
        Assert.Equal("Other.Editor", registry.GetString(@"Software\Classes\.md\UserChoice", "ProgId"));
        Assert.Equal("", registry.GetString(@"Software\Classes\.md\OpenWithProgids", "Other.Editor"));
        Assert.DoesNotContain(registry.DeletedKeys, path =>
            path.Contains("UserChoice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(registry.DeletedKeys, path =>
            path.Equals(@"Software\Classes\.md", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(@"Software\Classes\.markdown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unregister_preserves_entries_whose_ownership_values_were_replaced()
    {
        // Break caught: a later application reuses/replaces a registration and this app removes its data.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(@"Software\Classes\MarkUpViewMini.md", "MarkUpViewMini.Owner", "Other.Editor");
        registry.SetString(
            @"Software\RegisteredApplications",
            "MarkUpViewMini",
            @"Software\OtherEditor\Capabilities");
        registry.SetString(
            @"Software\Classes\.md\OpenWithProgids",
            "MarkUpViewMini.md",
            "Other.Editor");

        await service.UnregisterAsync();

        Assert.True(registry.KeyExists(@"Software\Classes\MarkUpViewMini.md"));
        Assert.Equal(@"Software\OtherEditor\Capabilities", registry.GetString(
            @"Software\RegisteredApplications", "MarkUpViewMini"));
        Assert.Equal("Other.Editor", registry.GetString(
            @"Software\Classes\.md\OpenWithProgids", "MarkUpViewMini.md"));
    }

    [Fact]
    public async Task GetStatus_requires_the_complete_owned_registration()
    {
        // Break caught: a partial/stale registration is reported as ready to Windows.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);

        Assert.False((await service.GetStatusAsync()).IsRegistered);
        await service.RegisterAsync(ExecutablePath);
        Assert.True((await service.GetStatusAsync()).IsRegistered);

        registry.DeleteValue(
            @"Software\MarkUpViewMini\Capabilities\FileAssociations",
            ".markdown");
        Assert.False((await service.GetStatusAsync()).IsRegistered);

        await service.RegisterAsync(ExecutablePath);
        registry.DeleteValue(
            @"Software\Classes\MarkUpViewMini.markdown\shell\open\command",
            string.Empty);
        Assert.False((await service.GetStatusAsync()).IsRegistered);
    }

    [Theory]
    [InlineData(@"Software\Classes\MarkUpViewMini.md", "MarkUpViewMini.Owner", "Other.Editor")]
    [InlineData(@"Software\Classes\MarkUpViewMini.md\shell\open\command", "", "\"C:\\Other.exe\" \"%1\"")]
    [InlineData(@"Software\MarkUpViewMini\Capabilities", "ApplicationIcon", "\"C:\\Other.exe\",0")]
    [InlineData(@"Software\RegisteredApplications", "MarkUpViewMini", @"Software\Other\Capabilities")]
    [InlineData(@"Software\Classes\.md\OpenWithProgids", "MarkUpViewMini.md", "Other.Editor")]
    public async Task Register_preflight_rejects_collisions_without_writing(
        string path,
        string name,
        string value)
    {
        // Break caught: registration overwrites a foreign or tampered value before detecting it.
        var registry = new RecordingRegistryStore();
        registry.SetString(path, name.Length == 0 ? null : name, value);
        var writeCount = registry.Writes.Count;
        var service = CreateService(registry);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(ExecutablePath));

        Assert.Equal(writeCount, registry.Writes.Count);
        Assert.Equal(value, registry.GetString(path, name.Length == 0 ? null : name));
    }

    [Fact]
    public async Task Register_preflight_rejects_an_extra_descendant_under_an_owned_root()
    {
        // Break caught: an intact root ownership marker hides a foreign descendant collision.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(
            @"Software\Classes\MarkUpViewMini.md\ForeignHandler",
            "Vendor",
            "Other.Editor");
        var writeCount = registry.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(ExecutablePath));

        Assert.Equal(writeCount, registry.Writes.Count);
        Assert.Equal("Other.Editor", registry.GetString(
            @"Software\Classes\MarkUpViewMini.md\ForeignHandler",
            "Vendor"));
    }

    [Fact]
    public async Task Register_preflight_rejects_an_extra_descendant_under_the_application_root()
    {
        // Break caught: a foreign child in the application's HKCU namespace is ignored by preflight.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(@"Software\MarkUpViewMini\Foreign", "Vendor", "Other.Editor");
        var writeCount = registry.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(ExecutablePath));

        Assert.Equal(writeCount, registry.Writes.Count);
        Assert.Equal("Other.Editor", registry.GetString(
            @"Software\MarkUpViewMini\Foreign",
            "Vendor"));
    }

    [Fact]
    public async Task Register_rolls_back_only_values_added_by_the_failed_attempt()
    {
        // Break caught: a mid-plan registry failure leaves a partial registration behind.
        var registry = new RecordingRegistryStore { ThrowOnWriteNumber = 5 };
        var service = CreateService(registry);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RegisterAsync(ExecutablePath));

        Assert.False(registry.KeyExists(@"Software\Classes\MarkUpViewMini.md"));
        Assert.False(registry.KeyExists(@"Software\Classes\MarkUpViewMini.markdown"));
        Assert.Null(registry.GetString(
            @"Software\Classes\.md\OpenWithProgids",
            "MarkUpViewMini.md"));
        Assert.Null(registry.GetString(
            @"Software\RegisteredApplications",
            "MarkUpViewMini"));
    }

    [Fact]
    public async Task Register_is_idempotent_when_every_owned_slot_is_already_exact()
    {
        // Break caught: repeated registration rewrites an exact plan and expands the failure surface.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        var writeCount = registry.Writes.Count;

        await service.RegisterAsync(ExecutablePath);

        Assert.Equal(writeCount, registry.Writes.Count);
        Assert.True((await service.GetStatusAsync()).IsRegistered);
    }

    [Fact]
    public async Task Unregister_preserves_tampered_values_and_extra_descendants_with_the_root_marker_intact()
    {
        // Break caught: recursive deletion trusts only the root marker and destroys changed descendants.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(
            @"Software\Classes\MarkUpViewMini.md\shell\open\command",
            null,
            "\"C:\\Other.exe\" \"%1\"");
        registry.SetString(
            @"Software\Classes\MarkUpViewMini.md\ForeignHandler",
            "Vendor",
            "Other.Editor");

        await service.UnregisterAsync();

        Assert.Equal("\"C:\\Other.exe\" \"%1\"", registry.GetString(
            @"Software\Classes\MarkUpViewMini.md\shell\open\command",
            null));
        Assert.Equal("Other.Editor", registry.GetString(
            @"Software\Classes\MarkUpViewMini.md\ForeignHandler",
            "Vendor"));
        Assert.Null(registry.GetString(
            @"Software\Classes\MarkUpViewMini.md",
            "MarkUpViewMini.Owner"));
        Assert.True(registry.KeyExists(@"Software\Classes\MarkUpViewMini.md"));
    }

    [Fact]
    public async Task Unregister_preserves_a_tampered_capability_descendant_with_the_root_marker_intact()
    {
        // Break caught: capability cleanup recursively deletes a changed association below an intact marker.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(
            @"Software\MarkUpViewMini\Capabilities\FileAssociations",
            ".md",
            "Other.Editor");

        await service.UnregisterAsync();

        Assert.Equal("Other.Editor", registry.GetString(
            @"Software\MarkUpViewMini\Capabilities\FileAssociations",
            ".md"));
        Assert.True(registry.KeyExists(@"Software\MarkUpViewMini\Capabilities"));
        Assert.Null(registry.GetString(
            @"Software\MarkUpViewMini\Capabilities",
            "MarkUpViewMini.Owner"));
    }

    [Theory]
    [InlineData(@"Software\Classes\MarkUpViewMini.md", "", "Wrong description")]
    [InlineData(@"Software\Classes\MarkUpViewMini.md\DefaultIcon", "", "\"C:\\Other.exe\",0")]
    [InlineData(@"Software\Classes\MarkUpViewMini.md\shell\open\command", "", "\"C:\\Other.exe\" \"%1\"")]
    [InlineData(@"Software\MarkUpViewMini\Capabilities", "ApplicationDescription", "Wrong description")]
    [InlineData(@"Software\MarkUpViewMini\Capabilities", "ApplicationIcon", "\"C:\\Other.exe\",0")]
    public async Task GetStatus_rejects_each_tampered_expected_value(
        string path,
        string name,
        string value)
    {
        // Break caught: status checks only for non-empty strings instead of the current executable's plan.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(path, name.Length == 0 ? null : name, value);

        Assert.False((await service.GetStatusAsync()).IsRegistered);
    }

    [Fact]
    public async Task GetStatus_rejects_extra_values_and_descendants()
    {
        // Break caught: an apparently complete registration masks foreign additions in an owned tree.
        var registry = new RecordingRegistryStore();
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);
        registry.SetString(@"Software\MarkUpViewMini\Capabilities\Foreign", "Value", "Other");

        Assert.False((await service.GetStatusAsync()).IsRegistered);
    }

    [Fact]
    public async Task Registry_status_read_returns_to_the_caller_while_io_is_blocked()
    {
        // Break caught: async-looking service methods execute registry I/O synchronously on the UI caller.
        var registry = new BlockingRegistryStore();
        var service = new FileAssociationService(
            registry,
            new RecordingProcessLauncher(),
            ExecutablePath,
            new ThreadPoolBackgroundExecutor(),
            new RecordingNotifier(),
            new FileAssociationOperationGate());
        var callReturned = new TaskCompletionSource<Task<FileAssociationStatus>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(async () =>
        {
            var operation = service.GetStatusAsync();
            callReturned.TrySetResult(operation);
            return await operation;
        });
        await registry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            var operation = await callReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            registry.Release.Set();
        }

        Assert.False((await invocation).IsRegistered);
    }

    [Fact]
    public async Task Register_returns_to_the_caller_while_shell_notification_is_blocked()
    {
        // Break caught: SHChangeNotify executes synchronously on the WPF command caller.
        var notifier = new BlockingNotifier();
        var service = CreateService(
            new RecordingRegistryStore(),
            executor: new ThreadPoolBackgroundExecutor(),
            notifier: notifier);
        var callReturned = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(async () =>
        {
            var operation = service.RegisterAsync(ExecutablePath);
            callReturned.TrySetResult(operation);
            await operation;
        });
        await notifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            var operation = await callReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            notifier.Release.Set();
        }

        await invocation;
    }

    [Fact]
    public async Task Two_services_cannot_let_a_failed_register_roll_back_the_other_services_success()
    {
        // Break caught: both instances preflight absent, then one rollback removes the other's exact values.
        var registry = new RecordingRegistryStore();
        var faulting = new BlockingThenFaultingRegistryStore(registry);
        var gate = new FileAssociationOperationGate();
        var first = new FileAssociationService(
            faulting,
            new RecordingProcessLauncher(),
            ExecutablePath,
            new ThreadPoolBackgroundExecutor(),
            new RecordingNotifier(),
            gate);
        var second = CreateService(
            registry,
            executor: new ThreadPoolBackgroundExecutor(),
            operationGate: gate);

        var failedRegistration = first.RegisterAsync(ExecutablePath);
        await faulting.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var successfulRegistration = second.RegisterAsync(ExecutablePath);

        try
        {
            Assert.False(successfulRegistration.IsCompleted);
        }
        finally
        {
            faulting.ReleaseFirstWrite.Set();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => failedRegistration);
        await successfulRegistration;
        Assert.True((await second.GetStatusAsync()).IsRegistered);
    }

    [Fact]
    public async Task Unregister_from_another_service_waits_for_register_to_finish()
    {
        // Break caught: unregister observes and removes a partially written registration.
        var registry = new RecordingRegistryStore();
        var blocking = new BlockingFirstWriteRegistryStore(registry);
        var gate = new FileAssociationOperationGate();
        var first = new FileAssociationService(
            blocking,
            new RecordingProcessLauncher(),
            ExecutablePath,
            new ThreadPoolBackgroundExecutor(),
            new RecordingNotifier(),
            gate);
        var second = CreateService(
            registry,
            executor: new ThreadPoolBackgroundExecutor(),
            operationGate: gate);
        var registration = first.RegisterAsync(ExecutablePath);
        await blocking.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var unregister = second.UnregisterAsync();

        try
        {
            Assert.False(unregister.IsCompleted);
        }
        finally
        {
            blocking.ReleaseFirstWrite.Set();
        }

        await registration;
        await unregister;
        Assert.False((await second.GetStatusAsync()).IsRegistered);
    }

    [Fact]
    public async Task Status_from_another_service_waits_for_register_and_observes_the_after_state()
    {
        // Break caught: status reads a partial tree while another window is registering it.
        var registry = new RecordingRegistryStore();
        var blocking = new BlockingFirstWriteRegistryStore(registry);
        var gate = new FileAssociationOperationGate();
        var first = new FileAssociationService(
            blocking,
            new RecordingProcessLauncher(),
            ExecutablePath,
            new ThreadPoolBackgroundExecutor(),
            new RecordingNotifier(),
            gate);
        var second = CreateService(
            registry,
            executor: new ThreadPoolBackgroundExecutor(),
            operationGate: gate);
        var registration = first.RegisterAsync(ExecutablePath);
        await blocking.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var status = second.GetStatusAsync();

        try
        {
            Assert.False(status.IsCompleted);
        }
        finally
        {
            blocking.ReleaseFirstWrite.Set();
        }

        await registration;
        Assert.True((await status).IsRegistered);
    }

    [Fact]
    public async Task A_new_service_unregisters_shared_keys_created_by_registration()
    {
        // Break caught: removing our value leaves new empty .md/.markdown/OpenWithProgids keys behind.
        var registry = new RecordingRegistryStore();
        await CreateService(registry).RegisterAsync(ExecutablePath);

        await CreateService(registry).UnregisterAsync();

        Assert.False(registry.KeyExists(@"Software\Classes\.md\OpenWithProgids"));
        Assert.False(registry.KeyExists(@"Software\Classes\.md"));
        Assert.False(registry.KeyExists(@"Software\Classes\.markdown\OpenWithProgids"));
        Assert.False(registry.KeyExists(@"Software\Classes\.markdown"));
    }

    [Fact]
    public async Task Unregister_preserves_preexisting_empty_shared_keys()
    {
        // Break caught: cleanup cannot distinguish an app-created empty key from an existing empty key.
        var registry = new RecordingRegistryStore();
        registry.AddEmptyKey(@"Software\Classes\.md\OpenWithProgids");
        registry.AddEmptyKey(@"Software\Classes\.markdown\OpenWithProgids");
        var service = CreateService(registry);
        await service.RegisterAsync(ExecutablePath);

        await CreateService(registry).UnregisterAsync();

        Assert.True(registry.KeyExists(@"Software\Classes\.md\OpenWithProgids"));
        Assert.True(registry.KeyExists(@"Software\Classes\.md"));
        Assert.True(registry.KeyExists(@"Software\Classes\.markdown\OpenWithProgids"));
        Assert.True(registry.KeyExists(@"Software\Classes\.markdown"));
    }

    [Fact]
    public async Task Unregister_deletes_only_the_open_with_key_when_the_extension_key_preexisted()
    {
        // Break caught: one creation flag is incorrectly reused for both levels of the shared tree.
        var registry = new RecordingRegistryStore();
        registry.AddEmptyKey(@"Software\Classes\.md");
        registry.AddEmptyKey(@"Software\Classes\.markdown");
        await CreateService(registry).RegisterAsync(ExecutablePath);

        await CreateService(registry).UnregisterAsync();

        Assert.True(registry.KeyExists(@"Software\Classes\.md"));
        Assert.False(registry.KeyExists(@"Software\Classes\.md\OpenWithProgids"));
        Assert.True(registry.KeyExists(@"Software\Classes\.markdown"));
        Assert.False(registry.KeyExists(@"Software\Classes\.markdown\OpenWithProgids"));
    }

    [Fact]
    public async Task Unregister_preserves_shared_keys_when_creation_bookkeeping_is_tampered()
    {
        // Break caught: untrusted bookkeeping authorizes deletion of a shared Windows class key.
        var registry = new RecordingRegistryStore();
        await CreateService(registry).RegisterAsync(ExecutablePath);
        registry.SetString(
            @"Software\MarkUpViewMini",
            "MarkUpViewMini.CreatedMdOpenWithProgids",
            "tampered");

        await CreateService(registry).UnregisterAsync();

        Assert.True(registry.KeyExists(@"Software\Classes\.md\OpenWithProgids"));
        Assert.True(registry.KeyExists(@"Software\Classes\.md"));
        Assert.Equal("tampered", registry.GetString(
            @"Software\MarkUpViewMini",
            "MarkUpViewMini.CreatedMdOpenWithProgids"));
    }

    [Fact]
    public async Task Unregister_preserves_other_application_data_added_to_an_app_created_shared_key()
    {
        // Break caught: creation bookkeeping overrides a later application's ownership of shared data.
        var registry = new RecordingRegistryStore();
        await CreateService(registry).RegisterAsync(ExecutablePath);
        registry.SetString(
            @"Software\Classes\.md\OpenWithProgids",
            "Other.Editor",
            string.Empty);

        await CreateService(registry).UnregisterAsync();

        Assert.Equal(string.Empty, registry.GetString(
            @"Software\Classes\.md\OpenWithProgids",
            "Other.Editor"));
        Assert.True(registry.KeyExists(@"Software\Classes\.md"));
    }

    [Fact]
    public async Task A_new_service_cleans_a_crash_partial_registration_with_complete_creation_bookkeeping()
    {
        // Break caught: a process exit after shared-key creation leaves recoverable owned debris forever.
        var registry = new RecordingRegistryStore();
        registry.SetString(@"Software\MarkUpViewMini", "MarkUpViewMini.Owner", "MarkUpViewMini");
        registry.SetString(@"Software\MarkUpViewMini", "MarkUpViewMini.CreatedMdExtension", "1");
        registry.SetString(@"Software\MarkUpViewMini", "MarkUpViewMini.CreatedMdOpenWithProgids", "1");
        registry.SetString(@"Software\MarkUpViewMini", "MarkUpViewMini.CreatedMarkdownExtension", "1");
        registry.SetString(@"Software\MarkUpViewMini", "MarkUpViewMini.CreatedMarkdownOpenWithProgids", "1");
        registry.AddEmptyKey(@"Software\Classes\.md\OpenWithProgids");
        registry.AddEmptyKey(@"Software\Classes\.markdown\OpenWithProgids");

        await CreateService(registry).UnregisterAsync();

        Assert.False(registry.KeyExists(@"Software\MarkUpViewMini"));
        Assert.False(registry.KeyExists(@"Software\Classes\.md"));
        Assert.False(registry.KeyExists(@"Software\Classes\.markdown"));
    }

    [Fact]
    public void OpenWindowsDefaultAppsSettings_uses_the_per_user_registered_app_uri()
    {
        // Break caught: the command attempts to claim a default or opens an unrelated Settings page.
        var launcher = new RecordingProcessLauncher();
        var service = new FileAssociationService(
            new RecordingRegistryStore(),
            launcher,
            ExecutablePath,
            new InlineBackgroundExecutor(),
            new RecordingNotifier(),
            new FileAssociationOperationGate());

        service.OpenWindowsDefaultAppsSettings();

        var start = Assert.Single(launcher.Starts);
        Assert.Equal(
            "ms-settings:defaultapps?registeredAppUser=MarkUpViewMini",
            start.FileName);
        Assert.True(start.UseShellExecute);
    }

    private sealed class RecordingRegistryStore : IRegistryStore
    {
        private readonly Dictionary<(string Path, string? Name), string> values =
            new(RegistryEntryComparer.Instance);
        private readonly HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new();

        public int? ThrowOnWriteNumber { get; set; }

        public List<RegistryWrite> Writes { get; } = [];

        public List<string> DeletedKeys { get; } = [];

        public string? GetString(string keyPath, string? valueName)
        {
            lock (sync)
            {
                return values.TryGetValue((keyPath, valueName), out var value) ? value : null;
            }
        }

        public bool KeyExists(string keyPath)
        {
            lock (sync)
            {
                return keys.Contains(keyPath);
            }
        }

        public RegistryKeySnapshot? ReadKey(string keyPath)
        {
            lock (sync)
            {
                if (!keys.Contains(keyPath))
                {
                    return null;
                }

                var directValues = values
                    .Where(entry => entry.Key.Path.Equals(keyPath, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        entry => entry.Key.Name ?? string.Empty,
                        entry => new RegistryValueSnapshot(entry.Value, RegistryValueKind.String),
                        StringComparer.OrdinalIgnoreCase);
                var prefix = keyPath + "\\";
                var children = keys
                    .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(path => path[prefix.Length..].Split('\\')[0])
                    .Where(name => name.Length != 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new RegistryKeySnapshot(directValues, children);
            }
        }

        public void SetString(string keyPath, string? valueName, string value)
        {
            lock (sync)
            {
                if (ThrowOnWriteNumber == Writes.Count + 1)
                {
                    throw new UnauthorizedAccessException("injected write failure");
                }

                AddKeyAndAncestors(keyPath);
                values[(keyPath, valueName)] = value;
                Writes.Add(new RegistryWrite(keyPath, valueName, value));
            }
        }

        public void DeleteValue(string keyPath, string valueName)
        {
            lock (sync)
            {
                values.Remove((keyPath, valueName));
            }
        }

        public void DeleteKeyIfEmpty(string keyPath)
        {
            lock (sync)
            {
                if (ReadKey(keyPath) is not { Values.Count: 0, SubKeyNames.Count: 0 })
                {
                    return;
                }

                keys.Remove(keyPath);
                DeletedKeys.Add(keyPath);
            }
        }

        public void AddEmptyKey(string keyPath)
        {
            lock (sync)
            {
                AddKeyAndAncestors(keyPath);
            }
        }

        private void AddKeyAndAncestors(string keyPath)
        {
            for (var current = keyPath; current.Length != 0;)
            {
                keys.Add(current);
                var separator = current.LastIndexOf('\\');
                if (separator < 0)
                {
                    break;
                }

                current = current[..separator];
            }
        }

        public sealed record RegistryWrite(string Path, string? Name, string Value);

        private sealed class RegistryEntryComparer : IEqualityComparer<(string Path, string? Name)>
        {
            public static RegistryEntryComparer Instance { get; } = new();

            public bool Equals((string Path, string? Name) x, (string Path, string? Name) y) =>
                StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path) &&
                StringComparer.OrdinalIgnoreCase.Equals(x.Name ?? string.Empty, y.Name ?? string.Empty);

            public int GetHashCode((string Path, string? Name) obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name ?? string.Empty));
        }
    }

    private sealed class BlockingRegistryStore : IRegistryStore
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public RegistryKeySnapshot? ReadKey(string keyPath)
        {
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
            return null;
        }

        public void SetString(string keyPath, string? valueName, string value) =>
            throw new NotSupportedException();

        public void DeleteValue(string keyPath, string valueName) =>
            throw new NotSupportedException();

        public void DeleteKeyIfEmpty(string keyPath) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingFirstWriteRegistryStore(IRegistryStore inner) : IRegistryStore
    {
        private int writes;

        public TaskCompletionSource FirstWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseFirstWrite { get; } = new(initialState: false);

        public RegistryKeySnapshot? ReadKey(string keyPath) => inner.ReadKey(keyPath);

        public void SetString(string keyPath, string? valueName, string value)
        {
            inner.SetString(keyPath, valueName, value);
            if (Interlocked.Increment(ref writes) == 1)
            {
                FirstWriteEntered.TrySetResult();
                ReleaseFirstWrite.Wait(TimeSpan.FromSeconds(10));
            }
        }

        public void DeleteValue(string keyPath, string valueName) =>
            inner.DeleteValue(keyPath, valueName);

        public void DeleteKeyIfEmpty(string keyPath) => inner.DeleteKeyIfEmpty(keyPath);
    }

    private sealed class BlockingThenFaultingRegistryStore(IRegistryStore inner) : IRegistryStore
    {
        private int writes;

        public TaskCompletionSource FirstWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseFirstWrite { get; } = new(initialState: false);

        public RegistryKeySnapshot? ReadKey(string keyPath) => inner.ReadKey(keyPath);

        public void SetString(string keyPath, string? valueName, string value)
        {
            var write = Interlocked.Increment(ref writes);
            if (write == 2)
            {
                throw new UnauthorizedAccessException("injected write failure");
            }

            inner.SetString(keyPath, valueName, value);
            if (write == 1)
            {
                FirstWriteEntered.TrySetResult();
                ReleaseFirstWrite.Wait(TimeSpan.FromSeconds(10));
            }
        }

        public void DeleteValue(string keyPath, string valueName) =>
            inner.DeleteValue(keyPath, valueName);

        public void DeleteKeyIfEmpty(string keyPath) => inner.DeleteKeyIfEmpty(keyPath);
    }

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Starts { get; } = [];

        public void Start(ProcessStartInfo startInfo) => Starts.Add(startInfo);
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

    private sealed class RecordingNotifier : IAssociationChangeNotifier
    {
        public int Calls { get; private set; }

        public void NotifyChanged() => Calls++;
    }

    private sealed class BlockingNotifier : IAssociationChangeNotifier
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public void NotifyChanged()
        {
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private static FileAssociationService CreateService(
        RecordingRegistryStore registry,
        string executablePath = ExecutablePath,
        IBackgroundExecutor? executor = null,
        IAssociationChangeNotifier? notifier = null,
        IFileAssociationOperationGate? operationGate = null) =>
        new(
            registry,
            new RecordingProcessLauncher(),
            executablePath,
            executor ?? new InlineBackgroundExecutor(),
            notifier ?? new RecordingNotifier(),
            operationGate ?? new FileAssociationOperationGate());
}
