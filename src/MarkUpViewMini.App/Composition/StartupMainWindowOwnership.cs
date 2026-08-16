using System.Windows;

namespace MarkUpViewMini.App.Composition;

internal static class StartupMainWindowOwnership
{
    public static void PreservePrevious(
        Application application,
        Window candidate,
        Window? previous)
    {
        if (ReferenceEquals(application.MainWindow, candidate))
        {
            application.MainWindow = previous;
        }
    }

    public static void Commit(Application application, Window candidate) =>
        application.MainWindow = candidate;

    public static void Abandon(
        Application application,
        Window candidate,
        Window? previous)
    {
        PreservePrevious(application, candidate, previous);
        candidate.Close();
    }
}
