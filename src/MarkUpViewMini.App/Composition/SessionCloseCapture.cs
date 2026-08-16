using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.Composition;

internal static class SessionCloseCapture
{
    public static SessionV1 Create(
        IReadOnlyList<SessionWindowV1> windows,
        Guid closingWindowId)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (closingWindowId == Guid.Empty)
        {
            throw new ArgumentException("A closing window ID cannot be empty.", nameof(closingWindowId));
        }

        return new SessionV1
        {
            Windows = windows.Count <= 1
                ? windows.ToArray()
                : windows.Where(window => window.WindowId != closingWindowId).ToArray(),
        };
    }
}
