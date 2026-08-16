namespace MarkUpViewMini.App.Web;

public sealed record WebViewRegistrationStep(Action Add, Action Remove);

public sealed class WebViewHandlerLifetime
{
    private IReadOnlyList<Action> removals = [];

    public bool IsSubscribed { get; private set; }

    public bool TryRegister(IReadOnlyList<WebViewRegistrationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (IsSubscribed)
        {
            return false;
        }

        if (steps.Count == 0)
        {
            throw new ArgumentException("At least one registration step is required.", nameof(steps));
        }

        var completedRemovals = new List<Action>(steps.Count);
        try
        {
            foreach (var step in steps)
            {
                ArgumentNullException.ThrowIfNull(step);
                ArgumentNullException.ThrowIfNull(step.Add);
                ArgumentNullException.ThrowIfNull(step.Remove);
                step.Add();
                completedRemovals.Add(step.Remove);
            }
        }
        catch (Exception registrationException)
        {
            var rollbackExceptions = RunInReverse(completedRemovals);
            if (rollbackExceptions.Count != 0)
            {
                throw new AggregateException(
                    "WebView handler registration and rollback both failed.",
                    [registrationException, .. rollbackExceptions]);
            }

            throw;
        }

        removals = completedRemovals;
        IsSubscribed = true;
        return true;
    }

    public bool TryUnregister()
    {
        if (!IsSubscribed)
        {
            return false;
        }

        var registeredRemovals = removals;
        removals = [];
        IsSubscribed = false;

        var removalExceptions = RunInReverse(registeredRemovals);
        if (removalExceptions.Count != 0)
        {
            throw new AggregateException("One or more WebView handlers could not be removed.", removalExceptions);
        }

        return true;
    }

    private static IReadOnlyList<Exception> RunInReverse(IReadOnlyList<Action> actions)
    {
        List<Exception>? exceptions = null;
        for (var index = actions.Count - 1; index >= 0; index--)
        {
            try
            {
                actions[index]();
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        return exceptions ?? [];
    }
}
