namespace MarkUpViewMini.Core.Workspace;

public static class TabOpenPolicy
{
    public static TabOpenDecision Decide(bool hasActiveTab, bool activeIsDirty, OpenGesture gesture)
    {
        return hasActiveTab && !activeIsDirty && gesture == OpenGesture.Normal
            ? TabOpenDecision.ReplaceActive
            : TabOpenDecision.OpenNewTab;
    }
}
