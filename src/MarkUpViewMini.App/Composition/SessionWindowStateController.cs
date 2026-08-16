using System.IO;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.Composition;

internal sealed class SessionWindowStateController(
    ShellViewModel shell,
    Func<string, bool> fileExists,
    Func<SessionWindowLayoutV1> captureLayout,
    Action<SessionWindowLayoutV1> applyLayout)
{
    public SessionWindowV1 Capture(Guid windowId)
    {
        if (windowId == Guid.Empty)
        {
            throw new ArgumentException("A window ID cannot be empty.", nameof(windowId));
        }

        return new SessionWindowV1
        {
            WindowId = windowId,
            Tabs = shell.Tabs
                .Where(tab => tab.Buffer is not null)
                .Select(CaptureTab)
                .ToArray(),
            ActiveTabId = shell.ActiveTab?.Buffer is null ? null : shell.ActiveTab.Id,
            RootPath = NormalizeOptionalPath(shell.Sidebar?.RootPath),
            Layout = captureLayout(),
        };
    }

    public async Task<int> RestoreAsync(
        SessionWindowV1 state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        applyLayout(state.Layout);
        var skipped = 0;
        var restored = new Dictionary<Guid, DocumentTabViewModel>();
        foreach (var tabState in state.Tabs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fileExists(tabState.Path) || restored.ContainsKey(tabState.TabId))
            {
                skipped++;
                continue;
            }

            try
            {
                var tab = await shell.RestoreSessionTabAsync(tabState, cancellationToken)
                    .ConfigureAwait(true);
                if (tab is null)
                {
                    skipped++;
                    continue;
                }

                restored.Add(tabState.TabId, tab);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException or InvalidOperationException)
            {
                var partial = shell.Tabs.FirstOrDefault(tab => tab.Id == tabState.TabId);
                if (partial is not null)
                {
                    shell.CloseTab(partial);
                }

                skipped++;
            }
        }

        if (shell.Sidebar is not null)
        {
            shell.Sidebar.RootPath = state.RootPath;
        }

        if (state.ActiveTabId is { } activeId && restored.TryGetValue(activeId, out var active))
        {
            await shell.ActivateAsync(active, cancellationToken).ConfigureAwait(true);
        }

        return skipped;
    }

    private static SessionTabV1 CaptureTab(DocumentTabViewModel tab)
    {
        var history = tab.NavigationHistory.Capture();
        return new SessionTabV1
        {
            TabId = tab.Id,
            Path = Path.GetFullPath(tab.Path),
            Mode = tab.Mode,
            History = history.Entries.Select(entry => new SessionNavigationEntryV1(
                Path.GetFullPath(entry.Path),
                entry.Line,
                entry.Anchor,
                entry.Mode,
                entry.ScrollOffset)).ToArray(),
            HistoryIndex = history.CurrentIndex,
            Hints = new SessionEditorHintsV1(
                tab.UiHints.SelectionAnchor,
                tab.UiHints.SelectionHead,
                tab.UiHints.ScrollTop,
                tab.UiHints.SplitRatio),
        };
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}
