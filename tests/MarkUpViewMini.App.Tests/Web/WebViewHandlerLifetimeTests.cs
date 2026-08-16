using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests.Web;

public sealed class WebViewHandlerLifetimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Registration_failure_rolls_back_each_completed_step_in_reverse_order(int failureIndex)
    {
        var active = new HashSet<int>();
        var removalOrder = new List<int>();
        var lifetime = new WebViewHandlerLifetime();
        var steps = CreateSteps(9, active, removalOrder, () => failureIndex);

        Assert.Throws<InvalidOperationException>(() => lifetime.TryRegister(steps));

        Assert.Empty(active);
        Assert.Equal(Enumerable.Range(0, failureIndex).Reverse(), removalOrder);
        Assert.False(lifetime.IsSubscribed);
    }

    [Fact]
    public void Failed_registration_can_retry_once_and_unregisters_once_in_reverse_order()
    {
        var active = new HashSet<int>();
        var removalOrder = new List<int>();
        var addCounts = new int[5];
        var failureIndex = 2;
        var lifetime = new WebViewHandlerLifetime();
        var steps = CreateSteps(5, active, removalOrder, () => failureIndex, addCounts);

        Assert.Throws<InvalidOperationException>(() => lifetime.TryRegister(steps));
        Assert.Empty(active);

        failureIndex = -1;
        removalOrder.Clear();
        Assert.True(lifetime.TryRegister(steps));
        Assert.False(lifetime.TryRegister(steps));
        Assert.Equal(Enumerable.Range(0, 5), active.Order());
        Assert.True(lifetime.IsSubscribed);

        Assert.True(lifetime.TryUnregister());
        Assert.False(lifetime.TryUnregister());

        Assert.Empty(active);
        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, removalOrder);
        Assert.Equal(new[] { 2, 2, 1, 1, 1 }, addCounts);
        Assert.False(lifetime.IsSubscribed);
    }

    private static IReadOnlyList<WebViewRegistrationStep> CreateSteps(
        int count,
        HashSet<int> active,
        List<int> removalOrder,
        Func<int> failureIndex,
        int[]? addCounts = null)
    {
        return Enumerable.Range(0, count)
            .Select(index => new WebViewRegistrationStep(
                Add: () =>
                {
                    if (index == failureIndex())
                    {
                        throw new InvalidOperationException($"Step {index} failed.");
                    }

                    Assert.True(active.Add(index), $"Step {index} was registered more than once.");
                    if (addCounts is not null)
                    {
                        addCounts[index]++;
                    }
                },
                Remove: () =>
                {
                    Assert.True(active.Remove(index), $"Step {index} was not registered.");
                    removalOrder.Add(index);
                }))
            .ToArray();
    }
}
