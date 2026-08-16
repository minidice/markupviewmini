using Microsoft.Win32;

namespace MarkUpViewMini.Infrastructure.Windows;

public interface IRegistryStore
{
    RegistryKeySnapshot? ReadKey(string keyPath);

    void SetString(string keyPath, string? valueName, string value);

    void DeleteValue(string keyPath, string valueName);

    void DeleteKeyIfEmpty(string keyPath);
}

public sealed record RegistryKeySnapshot(
    IReadOnlyDictionary<string, RegistryValueSnapshot> Values,
    IReadOnlySet<string> SubKeyNames);

public sealed record RegistryValueSnapshot(object? Value, RegistryValueKind Kind);
