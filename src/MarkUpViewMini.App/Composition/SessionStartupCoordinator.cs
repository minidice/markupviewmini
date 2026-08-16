using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.App.Services;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.Composition;

internal interface ISessionWindow
{
    void Commit();

    void Abandon();

    Task RestoreRecoveredAsync(
        IReadOnlyList<DocumentBuffer> buffers,
        CancellationToken cancellationToken);

    Task<int> RestoreAsync(SessionWindowV1 state, CancellationToken cancellationToken);

    Task OpenCommandLineTargetsAsync(
        IReadOnlyList<string> arguments,
        string? baseDirectory,
        CancellationToken cancellationToken);
}

internal sealed class SessionStartupCoordinator(
    Func<CancellationToken, Task> loadSettings,
    IRecoveryDecisionResolver recoveryResolver,
    Func<CancellationToken, Task<SessionLoadResult>> loadSession,
    Func<Guid?, ISessionWindow> createWindow,
    Action<int> showSkippedSummary)
{
    public async Task<IReadOnlyList<ISessionWindow>> StartAsync(
        IReadOnlyList<string> commandLineArguments,
        string? commandLineBaseDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandLineArguments);
        await loadSettings(cancellationToken).ConfigureAwait(true);
        var recovery = await recoveryResolver.ResolveAsync(cancellationToken).ConfigureAwait(true);
        if (recovery.IsCancelled)
        {
            return [];
        }

        var loaded = await loadSession(cancellationToken).ConfigureAwait(true);
        var skipped = loaded.SkippedEntries;
        var windows = new List<ISessionWindow>();
        var candidates = new List<(SessionWindowV1 State, ISessionWindow Window)>();
        foreach (var windowState in loaded.Session.Windows)
        {
            var window = createWindow(windowState.WindowId);
            try
            {
                skipped += await window.RestoreAsync(windowState, cancellationToken).ConfigureAwait(true);
                candidates.Add((windowState, window));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                window.Abandon();
                foreach (var candidate in candidates)
                {
                    candidate.Window.Abandon();
                }

                throw;
            }
            catch (Exception)
            {
                window.Abandon();
                skipped++;
            }
        }

        if (candidates.Count == 0)
        {
            var window = createWindow(null);
            candidates.Add((new SessionWindowV1 { WindowId = Guid.NewGuid() }, window));
        }

        foreach (var buffer in recovery.RestoredBuffers)
        {
            var owner = candidates.FirstOrDefault(item =>
                item.State.Tabs.Any(tab => tab.TabId == buffer.TabId)).Window ?? candidates[0].Window;
            try
            {
                await owner.RestoreRecoveredAsync([buffer], cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var candidate in candidates)
                {
                    candidate.Window.Abandon();
                }

                throw;
            }
            catch (RecoverySurfaceRollbackException)
            {
                foreach (var candidate in candidates)
                {
                    candidate.Window.Abandon();
                }

                throw;
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                candidate.Window.Commit();
                windows.Add(candidate.Window);
            }
            catch (Exception)
            {
                candidate.Window.Abandon();
                skipped++;
            }
        }

        if (windows.Count == 0)
        {
            var window = createWindow(null);
            window.Commit();
            windows.Add(window);
        }

        if (commandLineArguments.Count > 0)
        {
            await windows[0].OpenCommandLineTargetsAsync(
                commandLineArguments,
                commandLineBaseDirectory,
                cancellationToken).ConfigureAwait(true);
        }

        if (skipped > 0)
        {
            showSkippedSummary(skipped);
        }

        return windows;
    }
}
