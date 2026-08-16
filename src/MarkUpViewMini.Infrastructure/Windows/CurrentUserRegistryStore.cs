using Microsoft.Win32;

namespace MarkUpViewMini.Infrastructure.Windows;

public sealed class CurrentUserRegistryStore : IRegistryStore
{
    public RegistryKeySnapshot? ReadKey(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null)
        {
            return null;
        }

        var values = key.GetValueNames().ToDictionary(
            name => name,
            name => new RegistryValueSnapshot(
                key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                key.GetValueKind(name)),
            StringComparer.OrdinalIgnoreCase);
        return new RegistryKeySnapshot(
            values,
            key.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public void SetString(string keyPath, string? valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName ?? string.Empty, value, RegistryValueKind.String);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void DeleteKeyIfEmpty(string keyPath)
    {
        if (!keyPath.Contains('\\') || keyPath.EndsWith('\\'))
        {
            throw new ArgumentException("A non-root registry key path is required.", nameof(keyPath));
        }

        using var child = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (child is null ||
            child.GetValueNames().Length != 0 || child.GetSubKeyNames().Length != 0)
        {
            return;
        }

        child.Dispose();
        Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
    }
}
