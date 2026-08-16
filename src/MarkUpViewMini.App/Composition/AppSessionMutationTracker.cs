using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.Composition;

internal sealed class AppSessionMutationTracker
{
    private bool restoringStartup;

    public long MutationGeneration { get; private set; }

    public void BeginStartup() => restoringStartup = true;

    public void CompleteStartup() => restoringStartup = false;

    public SessionSaveReason RecordMutation()
    {
        if (restoringStartup)
        {
            return SessionSaveReason.AutomaticRestore;
        }

        MutationGeneration++;
        return SessionSaveReason.UserMutation;
    }

    public SessionSaveReason CaptureWithoutMutation() => SessionSaveReason.AutomaticRestore;

    public SessionSaveReason CaptureLastWindowClose() => SessionSaveReason.AutomaticRestore;
}
