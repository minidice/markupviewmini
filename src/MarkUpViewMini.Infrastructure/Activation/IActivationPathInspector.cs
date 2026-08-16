using System.Runtime.InteropServices;

namespace MarkUpViewMini.Infrastructure.Activation;

internal interface IActivationPathInspector
{
    DriveType GetDriveType(string rootPath);

    FileAttributes? GetExistingAttributes(string path);
}

internal sealed class WindowsActivationPathInspector : IActivationPathInspector
{
    internal static WindowsActivationPathInspector Instance { get; } = new();

    private WindowsActivationPathInspector()
    {
    }

    public DriveType GetDriveType(string rootPath) => (DriveType)GetDriveTypeW(rootPath);

    public FileAttributes? GetExistingAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);
}
