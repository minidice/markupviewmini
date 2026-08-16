using System.Diagnostics;

namespace MarkUpViewMini.Infrastructure.Windows;

public sealed class ShellProcessLauncher : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo)
    {
        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Windows could not start the requested target.");
        }
    }
}
