using System.Diagnostics;

namespace MarkUpViewMini.Infrastructure.Windows;

public interface IProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}
