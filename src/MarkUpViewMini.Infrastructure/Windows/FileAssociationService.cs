using System.Diagnostics;
using Microsoft.Win32;

namespace MarkUpViewMini.Infrastructure.Windows;

public sealed class FileAssociationService : IFileAssociationService
{
    private const string ApplicationName = "MarkUpViewMini";
    private const string OwnerValueName = "MarkUpViewMini.Owner";
    private const string OwnerValue = "MarkUpViewMini";
    private const string MarkdownProgId = "MarkUpViewMini.md";
    private const string LongMarkdownProgId = "MarkUpViewMini.markdown";
    private const string MarkdownProgIdPath = @"Software\Classes\MarkUpViewMini.md";
    private const string LongMarkdownProgIdPath = @"Software\Classes\MarkUpViewMini.markdown";
    private const string MarkdownExtensionPath = @"Software\Classes\.md";
    private const string LongMarkdownExtensionPath = @"Software\Classes\.markdown";
    private const string MarkdownOpenWithPath = @"Software\Classes\.md\OpenWithProgids";
    private const string LongMarkdownOpenWithPath = @"Software\Classes\.markdown\OpenWithProgids";
    private const string ApplicationRootPath = @"Software\MarkUpViewMini";
    private const string CapabilitiesPath = @"Software\MarkUpViewMini\Capabilities";
    private const string FileAssociationsPath = @"Software\MarkUpViewMini\Capabilities\FileAssociations";
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    private const string CapabilitiesRegistrationPath = @"Software\MarkUpViewMini\Capabilities";
    private const string MarkdownExtensionCreatedValueName = "MarkUpViewMini.CreatedMdExtension";
    private const string MarkdownOpenWithCreatedValueName = "MarkUpViewMini.CreatedMdOpenWithProgids";
    private const string LongMarkdownExtensionCreatedValueName = "MarkUpViewMini.CreatedMarkdownExtension";
    private const string LongMarkdownOpenWithCreatedValueName = "MarkUpViewMini.CreatedMarkdownOpenWithProgids";

    private readonly IRegistryStore registry;
    private readonly IProcessLauncher processLauncher;
    private readonly IBackgroundExecutor backgroundExecutor;
    private readonly IAssociationChangeNotifier notifier;
    private readonly IFileAssociationOperationGate operationGate;
    private readonly string executablePath;
    private readonly IReadOnlyList<ExpectedValue> sharedValues;

    public FileAssociationService(
        IRegistryStore registry,
        IProcessLauncher processLauncher,
        string executablePath,
        IBackgroundExecutor? backgroundExecutor = null,
        IAssociationChangeNotifier? notifier = null,
        IFileAssociationOperationGate? operationGate = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        this.executablePath = ValidateExecutablePath(executablePath);
        this.backgroundExecutor = backgroundExecutor ?? new ThreadPoolBackgroundExecutor();
        this.notifier = notifier ?? new ShellAssociationChangeNotifier();
        this.operationGate = operationGate ?? FileAssociationOperationGate.ProcessWide;
        sharedValues = CreateSharedValues();
    }

    public Task RegisterAsync(string executablePath)
    {
        var requestedPath = ValidateExecutablePath(executablePath);
        if (!string.Equals(requestedPath, this.executablePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The registration path must match the service executable path.",
                nameof(executablePath));
        }

        return operationGate.RunAsync(() => backgroundExecutor.RunAsync(Register));
    }

    public Task UnregisterAsync() =>
        operationGate.RunAsync(() => backgroundExecutor.RunAsync(Unregister));

    public Task<FileAssociationStatus> GetStatusAsync() =>
        operationGate.RunAsync(() =>
            backgroundExecutor.RunAsync(() => new FileAssociationStatus(IsExactlyRegistered())));

    public void OpenWindowsDefaultAppsSettings() =>
        processLauncher.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:defaultapps?registeredAppUser=MarkUpViewMini",
            UseShellExecute = true,
        });

    private void Register()
    {
        var creationState = GetRegistrationCreationState();
        var ownedKeys = CreateOwnedKeys(executablePath, creationState);
        var before = PreflightRegistration(ownedKeys);
        var added = new List<ExpectedValue>();
        try
        {
            foreach (var expected in AllValues(ownedKeys))
            {
                if (before[expected.Path]?.Values.ContainsKey(expected.Name) != true)
                {
                    added.Add(expected);
                    registry.SetString(expected.Path, ToStoreValueName(expected.Name), expected.Value);
                }
            }

            if (!IsExactlyRegistered())
            {
                throw new InvalidOperationException("The file association registration could not be verified.");
            }

            notifier.NotifyChanged();
        }
        catch
        {
            RollBackAddedValues(added, before, ownedKeys, creationState);
            throw;
        }
    }

    private void Unregister()
    {
        var applicationRoot = registry.ReadKey(ApplicationRootPath);
        var rootIsOwned = HasExactValue(applicationRoot, OwnerValueName, OwnerValue);
        var hasValidCreationState = TryReadCreationState(applicationRoot, out var creationState);
        var ownedKeys = CreateOwnedKeys(
            executablePath,
            hasValidCreationState ? creationState : null);
        foreach (var expected in AllValues(ownedKeys).Reverse())
        {
            var snapshot = registry.ReadKey(expected.Path);
            if (HasExactValue(snapshot, expected.Name, expected.Value))
            {
                registry.DeleteValue(expected.Path, expected.Name);
            }
        }

        if (hasValidCreationState)
        {
            DeleteCreatedSharedKeysIfEmpty(creationState);
        }
        else if (rootIsOwned)
        {
            DeleteRecognizedCreationFlags();
        }

        foreach (var path in ownedKeys.Select(key => key.Path)
                     .OrderByDescending(path => path.Count(character => character == '\\')))
        {
            registry.DeleteKeyIfEmpty(path);
        }

        notifier.NotifyChanged();
    }

    private Dictionary<string, RegistryKeySnapshot?> PreflightRegistration(
        IReadOnlyList<ExpectedKey> ownedKeys)
    {
        var snapshots = new Dictionary<string, RegistryKeySnapshot?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ownedKeys)
        {
            var snapshot = registry.ReadKey(key.Path);
            if (snapshot is null)
            {
                snapshots[key.Path] = null;
                continue;
            }

            snapshots[key.Path] = snapshot;
            if (key.RequiresOwnerMarker &&
                !HasExactValue(snapshot, OwnerValueName, OwnerValue))
            {
                throw Collision(key.Path);
            }

            if (snapshot.Values.Keys.Any(name => !key.Values.ContainsKey(name)) ||
                snapshot.SubKeyNames.Any(name => !key.SubKeyNames.Contains(name)) ||
                key.Values.Any(expected =>
                    snapshot.Values.ContainsKey(expected.Key) &&
                    !HasExactValue(snapshot, expected.Key, expected.Value)))
            {
                throw Collision(key.Path);
            }
        }

        foreach (var expected in sharedValues)
        {
            var snapshot = registry.ReadKey(expected.Path);
            snapshots[expected.Path] = snapshot;
            if (snapshot is not null && snapshot.Values.ContainsKey(expected.Name) &&
                !HasExactValue(snapshot, expected.Name, expected.Value))
            {
                throw Collision($"{expected.Path}\\{expected.Name}");
            }
        }


        return snapshots;
    }

    private bool IsExactlyRegistered()
    {
        if (!TryReadCreationState(out var creationState))
        {
            return false;
        }

        var ownedKeys = CreateOwnedKeys(executablePath, creationState);
        foreach (var key in ownedKeys)
        {
            var snapshot = registry.ReadKey(key.Path);
            if (snapshot is null ||
                snapshot.Values.Count != key.Values.Count ||
                snapshot.SubKeyNames.Count != key.SubKeyNames.Count ||
                key.Values.Any(expected => !HasExactValue(snapshot, expected.Key, expected.Value)) ||
                snapshot.SubKeyNames.Any(name => !key.SubKeyNames.Contains(name)))
            {
                return false;
            }
        }

        return sharedValues.All(expected =>
            HasExactValue(registry.ReadKey(expected.Path), expected.Name, expected.Value));
    }

    private void RollBackAddedValues(
        IReadOnlyList<ExpectedValue> added,
        IReadOnlyDictionary<string, RegistryKeySnapshot?> before,
        IReadOnlyList<ExpectedKey> ownedKeys,
        CreationState creationState)
    {
        foreach (var expected in added.Reverse())
        {
            if (HasExactValue(registry.ReadKey(expected.Path), expected.Name, expected.Value))
            {
                registry.DeleteValue(expected.Path, expected.Name);
            }
        }

        DeleteCreatedSharedKeysIfEmpty(creationState);

        foreach (var path in ownedKeys.Select(key => key.Path)
                     .Concat(added.Select(value => value.Path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(path => before.TryGetValue(path, out var snapshot) && snapshot is null)
                     .OrderByDescending(path => path.Count(character => character == '\\')))
        {
            registry.DeleteKeyIfEmpty(path);
        }
    }

    private IEnumerable<ExpectedValue> AllValues(IReadOnlyList<ExpectedKey> ownedKeys) =>
        ownedKeys.SelectMany(key => key.Values.Select(value =>
            new ExpectedValue(key.Path, value.Key, value.Value))).Concat(sharedValues);

    private static IReadOnlyList<ExpectedKey> CreateOwnedKeys(
        string executablePath,
        CreationState? creationState)
    {
        var command = $"\"{executablePath}\" \"%1\"";
        var icon = $"\"{executablePath}\",0";
        var applicationRootValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OwnerValueName] = OwnerValue,
        };
        if (creationState is not null)
        {
            applicationRootValues[MarkdownExtensionCreatedValueName] = Flag(creationState.MarkdownExtensionCreated);
            applicationRootValues[MarkdownOpenWithCreatedValueName] = Flag(creationState.MarkdownOpenWithCreated);
            applicationRootValues[LongMarkdownExtensionCreatedValueName] = Flag(creationState.LongMarkdownExtensionCreated);
            applicationRootValues[LongMarkdownOpenWithCreatedValueName] = Flag(creationState.LongMarkdownOpenWithCreated);
        }

        return
        [
            new ExpectedKey(
                ApplicationRootPath,
                applicationRootValues,
                Set("Capabilities"),
                RequiresOwnerMarker: true),
            ProgId(MarkdownProgIdPath, "MarkUpViewMini Markdown 문서 (.md)", icon, command),
            ProgId(MarkdownProgIdPath + @"\DefaultIcon", icon),
            Branch(MarkdownProgIdPath + @"\shell", "open"),
            Branch(MarkdownProgIdPath + @"\shell\open", "command"),
            ProgId(MarkdownProgIdPath + @"\shell\open\command", command),
            ProgId(LongMarkdownProgIdPath, "MarkUpViewMini Markdown 문서 (.markdown)", icon, command),
            ProgId(LongMarkdownProgIdPath + @"\DefaultIcon", icon),
            Branch(LongMarkdownProgIdPath + @"\shell", "open"),
            Branch(LongMarkdownProgIdPath + @"\shell\open", "command"),
            ProgId(LongMarkdownProgIdPath + @"\shell\open\command", command),
            new ExpectedKey(
                CapabilitiesPath,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [OwnerValueName] = OwnerValue,
                    ["ApplicationName"] = ApplicationName,
                    ["ApplicationDescription"] = "Markdown 문서를 읽고 편집합니다.",
                    ["ApplicationIcon"] = icon,
                },
                Set("FileAssociations"),
                RequiresOwnerMarker: true),
            new ExpectedKey(
                FileAssociationsPath,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".md"] = MarkdownProgId,
                    [".markdown"] = LongMarkdownProgId,
                },
                Set(),
                RequiresOwnerMarker: false),
        ];
    }

    private CreationState GetRegistrationCreationState()
    {
        var root = registry.ReadKey(ApplicationRootPath);
        if (root is null)
        {
            return new CreationState(
                registry.ReadKey(MarkdownExtensionPath) is null,
                registry.ReadKey(MarkdownOpenWithPath) is null,
                registry.ReadKey(LongMarkdownExtensionPath) is null,
                registry.ReadKey(LongMarkdownOpenWithPath) is null);
        }

        if (!HasExactValue(root, OwnerValueName, OwnerValue))
        {
            throw Collision(ApplicationRootPath);
        }

        var stateValueNames = CreationStateValueNames();
        var presentStateValues = root.Values.Keys.Count(stateValueNames.Contains);
        if (presentStateValues == 0)
        {
            return new CreationState(false, false, false, false);
        }

        if (presentStateValues != stateValueNames.Count || !TryReadCreationState(root, out var state))
        {
            throw Collision(ApplicationRootPath);
        }

        return state;
    }

    private bool TryReadCreationState(out CreationState state) =>
        TryReadCreationState(registry.ReadKey(ApplicationRootPath), out state);

    private static bool TryReadCreationState(RegistryKeySnapshot? root, out CreationState state)
    {
        state = new CreationState(false, false, false, false);
        if (root is null || root.Values.Count != 5 ||
            !HasExactValue(root, OwnerValueName, OwnerValue) ||
            !TryReadFlag(root, MarkdownExtensionCreatedValueName, out var markdownExtensionCreated) ||
            !TryReadFlag(root, MarkdownOpenWithCreatedValueName, out var markdownOpenWithCreated) ||
            !TryReadFlag(root, LongMarkdownExtensionCreatedValueName, out var longMarkdownExtensionCreated) ||
            !TryReadFlag(root, LongMarkdownOpenWithCreatedValueName, out var longMarkdownOpenWithCreated))
        {
            return false;
        }

        state = new CreationState(
            markdownExtensionCreated,
            markdownOpenWithCreated,
            longMarkdownExtensionCreated,
            longMarkdownOpenWithCreated);
        return true;
    }

    private static bool TryReadFlag(RegistryKeySnapshot root, string name, out bool value)
    {
        if (HasExactValue(root, name, "1"))
        {
            value = true;
            return true;
        }

        value = false;
        return HasExactValue(root, name, "0");
    }

    private void DeleteCreatedSharedKeysIfEmpty(CreationState state)
    {
        if (state.MarkdownOpenWithCreated)
        {
            registry.DeleteKeyIfEmpty(MarkdownOpenWithPath);
        }

        if (state.MarkdownExtensionCreated)
        {
            registry.DeleteKeyIfEmpty(MarkdownExtensionPath);
        }

        if (state.LongMarkdownOpenWithCreated)
        {
            registry.DeleteKeyIfEmpty(LongMarkdownOpenWithPath);
        }

        if (state.LongMarkdownExtensionCreated)
        {
            registry.DeleteKeyIfEmpty(LongMarkdownExtensionPath);
        }
    }

    private void DeleteRecognizedCreationFlags()
    {
        foreach (var name in CreationStateValueNames())
        {
            var root = registry.ReadKey(ApplicationRootPath);
            if (HasExactValue(root, name, "0") || HasExactValue(root, name, "1"))
            {
                registry.DeleteValue(ApplicationRootPath, name);
            }
        }
    }

    private static HashSet<string> CreationStateValueNames() =>
        Set(
            MarkdownExtensionCreatedValueName,
            MarkdownOpenWithCreatedValueName,
            LongMarkdownExtensionCreatedValueName,
            LongMarkdownOpenWithCreatedValueName);

    private static string Flag(bool value) => value ? "1" : "0";

    private static IReadOnlyList<ExpectedValue> CreateSharedValues() =>
    [
        new(MarkdownOpenWithPath, MarkdownProgId, string.Empty),
        new(LongMarkdownOpenWithPath, LongMarkdownProgId, string.Empty),
        new(RegisteredApplicationsPath, ApplicationName, CapabilitiesRegistrationPath),
    ];

    private static ExpectedKey ProgId(string path, string defaultValue, string? icon = null, string? command = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = defaultValue,
        };
        var children = Set();
        var ownsRoot = icon is not null && command is not null;
        if (ownsRoot)
        {
            values[OwnerValueName] = OwnerValue;
            children = Set("DefaultIcon", "shell");
        }

        return new ExpectedKey(path, values, children, ownsRoot);
    }

    private static ExpectedKey Branch(string path, string child) =>
        new(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Set(child), false);

    private static HashSet<string> Set(params string[] values) =>
        values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasExactValue(RegistryKeySnapshot? snapshot, string name, string expected) =>
        snapshot is not null &&
        snapshot.Values.TryGetValue(name, out var actual) &&
        actual.Kind == RegistryValueKind.String &&
        actual.Value is string text &&
        string.Equals(text, expected, StringComparison.Ordinal);

    private static string? ToStoreValueName(string name) => name.Length == 0 ? null : name;

    private static InvalidOperationException Collision(string location) =>
        new($"File association registration collision at '{location}'. No changes were made.");

    private static string ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains('"'))
        {
            throw new ArgumentException(
                "The executable path must be a fully qualified .exe path.",
                nameof(executablePath));
        }

        return Path.GetFullPath(executablePath);
    }

    private sealed record ExpectedKey(
        string Path,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> SubKeyNames,
        bool RequiresOwnerMarker);

    private sealed record ExpectedValue(string Path, string Name, string Value);

    private sealed record CreationState(
        bool MarkdownExtensionCreated,
        bool MarkdownOpenWithCreated,
        bool LongMarkdownExtensionCreated,
        bool LongMarkdownOpenWithCreated);
}
