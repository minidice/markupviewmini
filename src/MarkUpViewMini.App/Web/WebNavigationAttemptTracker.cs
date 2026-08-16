namespace MarkUpViewMini.App.Web;

public sealed class WebNavigationAttemptTracker
{
    private Uri? expectedUri;
    private ulong? currentNavigationId;

    public long? CurrentGeneration { get; private set; }

    public void Begin(long generation, Uri bootstrapUri)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        ArgumentNullException.ThrowIfNull(bootstrapUri);
        if (!bootstrapUri.IsAbsoluteUri)
        {
            throw new ArgumentException("An absolute bootstrap URI is required.", nameof(bootstrapUri));
        }

        CurrentGeneration = generation;
        expectedUri = bootstrapUri;
        currentNavigationId = null;
    }

    public bool TryRecordStarting(long generation, Uri uri, ulong navigationId)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (CurrentGeneration != generation ||
            expectedUri is null ||
            currentNavigationId is not null ||
            navigationId == 0 ||
            !string.Equals(uri.AbsoluteUri, expectedUri.AbsoluteUri, StringComparison.Ordinal))
        {
            return false;
        }

        currentNavigationId = navigationId;
        return true;
    }

    public bool IsCurrentCompletion(long generation, ulong navigationId) =>
        CurrentGeneration == generation &&
        navigationId != 0 &&
        currentNavigationId == navigationId;

    public void Clear()
    {
        CurrentGeneration = null;
        expectedUri = null;
        currentNavigationId = null;
    }
}
