using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.App.ViewModels;

internal sealed record DirtyCloseRequest(
    ShellViewModel Shell,
    IEnumerable<DocumentTabViewModel> Tabs,
    Func<DocumentTabViewModel, DirtyCloseChoice> Choose);

internal sealed record DirtyClosePlan(
    ShellViewModel Shell,
    IReadOnlyList<DirtyCloseTabSnapshot> Tabs,
    IReadOnlyList<DirtyClosePlanEntry> Entries,
    bool RequiresExactTabSet);

internal sealed record ShellShutdownOwnership(IReadOnlyList<DirtyCloseTabSnapshot> Tabs);

internal sealed record DirtyCloseTabSnapshot(
    DocumentTabViewModel Tab,
    Guid TabId,
    DocumentBuffer? Buffer,
    long Revision,
    bool IsDirty,
    long LoadGeneration,
    long NavigationGeneration,
    long SaveGeneration);

internal sealed class DirtyClosePlanEntry(
    DirtyCloseTabSnapshot snapshot,
    DirtyCloseChoice choice)
{
    internal DirtyCloseTabSnapshot Snapshot { get; } = snapshot;

    internal DirtyCloseChoice Choice { get; } = choice;

    internal bool SaveCompleted { get; set; }
}

internal static class DirtyCloseCoordinator
{
    internal static Task<bool> TryResolveAsync(
        IEnumerable<DirtyCloseRequest> requests,
        CancellationToken cancellationToken) =>
        TryResolveAsync(requests, static () => true, cancellationToken);

    internal static async Task<bool> TryResolveAsync(
        IEnumerable<DirtyCloseRequest> requests,
        Func<bool> validateGlobalOwnership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(validateGlobalOwnership);
        var plans = new List<DirtyClosePlan>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = request.Shell.TryCreateDirtyClosePlan(
                request.Tabs,
                request.Choose,
                cancellationToken);
            if (plan is null)
            {
                return false;
            }

            plans.Add(plan);
        }

        if (!ValidateAll(plans, validateGlobalOwnership))
        {
            return false;
        }

        try
        {
            foreach (var plan in plans)
            {
                foreach (var entry in plan.Entries.Where(entry => entry.Choice == DirtyCloseChoice.Save))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ValidateAll(plans, validateGlobalOwnership) ||
                        !await plan.Shell.ExecuteDirtyCloseSaveAsync(plan, entry, cancellationToken))
                    {
                        RescheduleDirtyRecovery(plans);
                        return false;
                    }

                    entry.SaveCompleted = true;
                    if (!ValidateAll(plans, validateGlobalOwnership))
                    {
                        RescheduleDirtyRecovery(plans);
                        return false;
                    }
                }
            }

            if (!ValidateAll(plans, validateGlobalOwnership))
            {
                RescheduleDirtyRecovery(plans);
                return false;
            }

            foreach (var plan in plans)
            {
                foreach (var entry in plan.Entries.Where(entry => entry.Choice == DirtyCloseChoice.Discard))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ValidateAll(plans, validateGlobalOwnership) ||
                        !await plan.Shell.ExecuteDirtyCloseDiscardAsync(entry, cancellationToken) ||
                        !ValidateAll(plans, validateGlobalOwnership))
                    {
                        RescheduleDirtyRecovery(plans);
                        return false;
                    }
                }
            }

            return ValidateAll(plans, validateGlobalOwnership);
        }
        catch
        {
            RescheduleDirtyRecovery(plans);
            throw;
        }
    }

    private static bool ValidateAll(
        IReadOnlyList<DirtyClosePlan> plans,
        Func<bool> validateGlobalOwnership)
    {
        if (!validateGlobalOwnership())
        {
            foreach (var plan in plans)
            {
                plan.Shell.ShowShutdownOwnershipChanged();
            }

            return false;
        }

        return plans.All(plan => plan.Shell.ValidateDirtyClosePlan(plan));
    }

    private static void RescheduleDirtyRecovery(IEnumerable<DirtyClosePlan> plans)
    {
        foreach (var plan in plans)
        {
            plan.Shell.RescheduleDirtyRecovery(plan);
        }
    }
}
