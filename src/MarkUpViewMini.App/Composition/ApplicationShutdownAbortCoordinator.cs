namespace MarkUpViewMini.App.Composition;

internal static class ApplicationShutdownAbortCoordinator
{
    internal static void AbortCurrentWindows<TWindow>(
        IEnumerable<TWindow> currentWindows,
        Action<TWindow> abortApplicationShutdown)
        where TWindow : class
    {
        ArgumentNullException.ThrowIfNull(currentWindows);
        ArgumentNullException.ThrowIfNull(abortApplicationShutdown);

        var snapshot = currentWindows
            .Distinct<TWindow>(ReferenceEqualityComparer.Instance)
            .ToArray();
        foreach (var window in snapshot)
        {
            abortApplicationShutdown(window);
        }
    }
}
