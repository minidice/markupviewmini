using System.Runtime.InteropServices;
using System.Text;

namespace MarkUpViewMini.Infrastructure.Windows;

internal sealed record ShellLinkDefinition(
    string TargetPath,
    string WorkingDirectory,
    string Description,
    string IconPath,
    int IconIndex,
    string AppUserModelId);

internal sealed record ShellLinkSnapshot(
    string TargetPath,
    string WorkingDirectory,
    string Description,
    string IconPath,
    int IconIndex,
    string AppUserModelId,
    ushort AppUserModelIdValueType = 0,
    bool AppUserModelIdKeyPresent = false);

internal interface IShellLinkAccessor
{
    void Write(string path, ShellLinkDefinition link);

    ShellLinkSnapshot Read(string path);
}

internal sealed class ComShellLinkAccessor : IShellLinkAccessor
{
    private const int LongPathCapacity = 32768;
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    public void Write(string path, ShellLinkDefinition link)
    {
        ArgumentNullException.ThrowIfNull(link);
        object? shellLinkObject = null;
        try
        {
            shellLinkObject = new ShellLinkComObject();
            var shellLink = (IShellLinkW)shellLinkObject;
            shellLink.SetPath(link.TargetPath);
            shellLink.SetWorkingDirectory(link.WorkingDirectory);
            shellLink.SetDescription(link.Description);
            shellLink.SetIconLocation(link.IconPath, link.IconIndex);

            var propertyStore = new ComWritablePropertyStore(
                (IPropertyStore)shellLinkObject,
                AppUserModelIdKey);
            AppUserModelIdPropertyWriter.Write(propertyStore, link.AppUserModelId);

            ((IPersistFile)shellLinkObject).Save(path, remember: true);
        }
        finally
        {
            ReleaseComObject(shellLinkObject);
        }
    }

    public ShellLinkSnapshot Read(string path)
    {
        object? shellLinkObject = null;
        try
        {
            shellLinkObject = new ShellLinkComObject();
            ((IPersistFile)shellLinkObject).Load(path, 0);
            var shellLink = (IShellLinkW)shellLinkObject;

            var target = new StringBuilder(LongPathCapacity);
            shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var workingDirectory = new StringBuilder(LongPathCapacity);
            shellLink.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            var description = new StringBuilder(1024);
            shellLink.GetDescription(description, description.Capacity);
            var iconPath = new StringBuilder(LongPathCapacity);
            shellLink.GetIconLocation(iconPath, iconPath.Capacity, out var iconIndex);

            var propertyStore = (IPropertyStore)shellLinkObject;
            var key = AppUserModelIdKey;
            Marshal.ThrowExceptionForHR(propertyStore.GetCount(out var propertyCount));
            var keyPresent = false;
            for (uint index = 0; index < propertyCount; index++)
            {
                Marshal.ThrowExceptionForHR(propertyStore.GetAt(index, out var storedKey));
                keyPresent |= storedKey.Equals(key);
            }

            var identity = AppUserModelIdPropertyReader.Read(
                new ComPropertyValueReader(propertyStore, key));
            return new ShellLinkSnapshot(
                target.ToString(),
                workingDirectory.ToString(),
                description.ToString(),
                iconPath.ToString(),
                iconIndex,
                identity.Value ?? string.Empty,
                identity.ValueType,
                keyPresent);
        }
        finally
        {
            ReleaseComObject(shellLinkObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal interface IWritablePropertyStore
{
    void SetAppUserModelId(string value);

    void Commit();
}

internal static class AppUserModelIdPropertyWriter
{
    public static void Write(IWritablePropertyStore propertyStore, string appUserModelId)
    {
        propertyStore.SetAppUserModelId(appUserModelId);
        propertyStore.Commit();
    }
}

internal sealed class ComWritablePropertyStore(
    IPropertyStore propertyStore,
    PropertyKey appUserModelIdKey) : IWritablePropertyStore
{
    public void SetAppUserModelId(string appUserModelId)
    {
        var key = appUserModelIdKey;
        var value = PropVariant.FromString(appUserModelId);
        try
        {
            Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, ref value));
        }
        finally
        {
            value.Dispose();
        }
    }

    public void Commit() => Marshal.ThrowExceptionForHR(propertyStore.Commit());
}

internal readonly record struct AppUserModelIdValue(string? Value, ushort ValueType);

internal interface IPropertyValueReader
{
    int GetValue(out IPropertyValue value);
}

internal interface IPropertyValue : IDisposable
{
    ushort ValueType { get; }

    string? GetString();
}

internal static class AppUserModelIdPropertyReader
{
    public static AppUserModelIdValue Read(IPropertyValueReader reader)
    {
        IPropertyValue? value = null;
        try
        {
            Marshal.ThrowExceptionForHR(reader.GetValue(out value));
            return new AppUserModelIdValue(value.GetString(), value.ValueType);
        }
        finally
        {
            value?.Dispose();
        }
    }
}

internal sealed class ComPropertyValueReader(
    IPropertyStore propertyStore,
    PropertyKey propertyKey) : IPropertyValueReader
{
    public int GetValue(out IPropertyValue value)
    {
        var variant = default(PropVariant);
        try
        {
            var key = propertyKey;
            var result = propertyStore.GetValue(ref key, out variant);
            value = new ComPropertyValue(variant);
            variant = default;
            return result;
        }
        catch
        {
            variant.Dispose();
            throw;
        }
    }
}

internal sealed class ComPropertyValue(PropVariant value) : IPropertyValue
{
    private PropVariant value = value;

    public ushort ValueType => value.ValueType;

    public string? GetString() => value.GetString();

    public void Dispose() => value.Dispose();
}

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkComObject;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
        int capacity,
        IntPtr findData,
        uint flags);

    void GetIdList(out IntPtr itemIdList);

    void SetIdList(IntPtr itemIdList);

    void GetDescription(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description,
        int capacity);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

    void GetWorkingDirectory(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
        int capacity);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

    void GetArguments(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
        int capacity);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

    void GetHotkey(out short hotkey);

    void SetHotkey(short hotkey);

    void GetShowCommand(out int showCommand);

    void SetShowCommand(int showCommand);

    void GetIconLocation(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
        int capacity,
        out int iconIndex);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

    void Resolve(IntPtr windowHandle, uint flags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal interface IPersistFile
{
    void GetClassId(out Guid classId);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

    void Save(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct PropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;

    public bool Equals(PropertyKey other) =>
        FormatId == other.FormatId && PropertyId == other.PropertyId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant : IDisposable
{
    private const ushort UnicodeStringType = 31;

    private ushort valueType;
    private ushort reserved1;
    private ushort reserved2;
    private ushort reserved3;
    private PropVariantUnion value;

    public readonly ushort ValueType => valueType;

    public static PropVariant FromString(string value) => new()
    {
        valueType = UnicodeStringType,
        value = new PropVariantUnion
        {
            PointerValue = Marshal.StringToCoTaskMemUni(value),
        },
    };

    public string? GetString() => valueType == UnicodeStringType
        ? Marshal.PtrToStringUni(value.PointerValue)
        : null;

    public void Dispose()
    {
        if (valueType != 0)
        {
            PropVariantClear(ref this);
            valueType = 0;
            value.PointerValue = IntPtr.Zero;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariantUnion
{
    [FieldOffset(0)]
    public IntPtr PointerValue;

    [FieldOffset(0)]
    private CountedPointer countedPointer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CountedPointer
{
    private uint elementCount;
    private IntPtr elements;
}
