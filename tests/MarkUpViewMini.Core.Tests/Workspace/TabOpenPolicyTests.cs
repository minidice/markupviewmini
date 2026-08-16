using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.Core.Tests.Workspace;

public sealed class TabOpenPolicyTests
{
    [Theory]
    [InlineData(false, false, OpenGesture.Normal, TabOpenDecision.OpenNewTab)]
    [InlineData(false, false, OpenGesture.ControlClick, TabOpenDecision.OpenNewTab)]
    [InlineData(false, false, OpenGesture.MiddleClick, TabOpenDecision.OpenNewTab)]
    [InlineData(false, false, OpenGesture.ExplicitNewTab, TabOpenDecision.OpenNewTab)]
    [InlineData(true, false, OpenGesture.Normal, TabOpenDecision.ReplaceActive)]
    [InlineData(true, false, OpenGesture.ControlClick, TabOpenDecision.OpenNewTab)]
    [InlineData(true, false, OpenGesture.MiddleClick, TabOpenDecision.OpenNewTab)]
    [InlineData(true, false, OpenGesture.ExplicitNewTab, TabOpenDecision.OpenNewTab)]
    [InlineData(true, true, OpenGesture.Normal, TabOpenDecision.OpenNewTab)]
    [InlineData(true, true, OpenGesture.ControlClick, TabOpenDecision.OpenNewTab)]
    [InlineData(true, true, OpenGesture.MiddleClick, TabOpenDecision.OpenNewTab)]
    [InlineData(true, true, OpenGesture.ExplicitNewTab, TabOpenDecision.OpenNewTab)]
    public void Decide_matches_approved_tab_rules(
        bool hasActiveTab,
        bool activeIsDirty,
        OpenGesture gesture,
        TabOpenDecision expected)
    {
        Assert.Equal(expected, TabOpenPolicy.Decide(hasActiveTab, activeIsDirty, gesture));
    }
}
