using System.Runtime.InteropServices;

namespace MarkUpViewMini.Infrastructure.Windows;

public interface IAssociationChangeNotifier
{
    void NotifyChanged();
}

public sealed class ShellAssociationChangeNotifier : IAssociationChangeNotifier
{
    private const uint AssociationChangedEvent = 0x08000000;
    private const uint IdListAndFlushNoWait = 0x3000;

    public void NotifyChanged() =>
        SHChangeNotify(AssociationChangedEvent, IdListAndFlushNoWait, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
