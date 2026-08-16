namespace MarkUpViewMini.Core.Navigation;

public sealed class NavigationHistory
{
    private readonly List<NavigationEntry> entries = [];
    private int currentIndex = -1;

    public bool CanMoveBack => currentIndex > 0;

    public bool CanMoveForward => currentIndex >= 0 && currentIndex < entries.Count - 1;

    public NavigationHistorySnapshot Capture() => new(entries.ToArray(), currentIndex);

    public void Push(NavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (currentIndex >= 0 && entries[currentIndex] == entry)
        {
            return;
        }

        var forwardIndex = currentIndex + 1;
        if (forwardIndex < entries.Count)
        {
            entries.RemoveRange(forwardIndex, entries.Count - forwardIndex);
        }

        entries.Add(entry);
        currentIndex = entries.Count - 1;
    }

    public bool TryMoveBack(out NavigationEntry entry)
    {
        if (currentIndex <= 0)
        {
            entry = null!;
            return false;
        }

        entry = entries[--currentIndex];
        return true;
    }

    public bool TryMoveForward(out NavigationEntry entry)
    {
        if (currentIndex < 0 || currentIndex >= entries.Count - 1)
        {
            entry = null!;
            return false;
        }

        entry = entries[++currentIndex];
        return true;
    }

    public void Restore(NavigationHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.CurrentIndex < -1 ||
            snapshot.CurrentIndex >= snapshot.Entries.Count ||
            (snapshot.Entries.Count == 0 && snapshot.CurrentIndex != -1))
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        }

        entries.Clear();
        entries.AddRange(snapshot.Entries);
        currentIndex = snapshot.CurrentIndex;
    }
}

public sealed record NavigationHistorySnapshot(
    IReadOnlyList<NavigationEntry> Entries,
    int CurrentIndex);
