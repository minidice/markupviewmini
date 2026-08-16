using System.Collections.Concurrent;
using System.Text;
using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.App.Web;

internal sealed class DocumentChangeBatchAssembler : IDisposable
{
    private static readonly TimeSpan MaximumBatchLifetime = TimeSpan.FromSeconds(30);
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan batchLifetime;
    private readonly ConcurrentDictionary<Guid, BatchState> batches = [];
    private bool disposed;

    internal DocumentChangeBatchAssembler(
        Func<DateTimeOffset>? clock = null,
        TimeSpan? batchLifetime = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.batchLifetime = batchLifetime is { } lifetime &&
            lifetime > TimeSpan.Zero &&
            lifetime <= MaximumBatchLifetime
            ? lifetime
            : MaximumBatchLifetime;
    }

    internal bool Start(DocumentChangeBatchStartMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (disposed)
        {
            return false;
        }

        Cancel(message.Owner.TabId);
        var state = new BatchState(
            message.Owner,
            message.BatchId,
            message.ExpectedRevision,
            message.Changes,
            clock());
        batches[message.Owner.TabId] = state;
        state.Expiry = new Timer(
            _ => Expire(message.Owner.TabId, state),
            null,
            batchLifetime,
            Timeout.InfiniteTimeSpan);
        return true;
    }

    internal bool Append(DocumentChangeBatchChunkMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TryGetCurrent(message.Owner.TabId, out var state))
        {
            return false;
        }

        if (state.Owner != message.Owner || state.BatchId != message.BatchId)
        {
            Cancel(message.Owner.TabId);
            return false;
        }

        var expectedIndex = state.NextChangeIndex;
        if (message.ChangeIndex != expectedIndex ||
            expectedIndex >= state.Changes.Count ||
            message.Offset != state.Builders[expectedIndex].Length)
        {
            Cancel(message.Owner.TabId);
            return false;
        }

        var declaration = state.Changes[expectedIndex];
        if (message.Text.Length == 0 ||
            message.Text.Length > declaration.InsertedLength - message.Offset)
        {
            Cancel(message.Owner.TabId);
            return false;
        }

        state.Builders[expectedIndex].Append(message.Text);
        state.AdvancePastCompletedChanges();
        return true;
    }

    internal DocumentChangedMessage? Commit(DocumentChangeBatchCommitMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TryGetCurrent(message.Owner.TabId, out var state))
        {
            return null;
        }

        Cancel(message.Owner.TabId);
        if (state.Owner != message.Owner ||
            state.BatchId != message.BatchId ||
            state.NextChangeIndex != state.Changes.Count)
        {
            return null;
        }

        var changes = new TextChange[state.Changes.Count];
        for (var index = 0; index < changes.Length; index++)
        {
            var declaration = state.Changes[index];
            changes[index] = new TextChange(
                declaration.From,
                declaration.To,
                state.Builders[index].ToString());
        }

        return new DocumentChangedMessage(
            state.Owner,
            new DocumentEdit(state.ExpectedRevision, changes));
    }

    internal void Cancel(Guid tabId)
    {
        if (batches.TryRemove(tabId, out var state))
        {
            state.Expiry?.Dispose();
        }
    }

    internal void CancelAll()
    {
        foreach (var tabId in batches.Keys)
        {
            Cancel(tabId);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CancelAll();
        disposed = true;
    }

    private bool TryGetCurrent(Guid tabId, out BatchState state)
    {
        state = null!;
        if (disposed || !batches.TryGetValue(tabId, out var candidate))
        {
            return false;
        }

        if (clock() - candidate.StartedAt >= batchLifetime)
        {
            Cancel(tabId);
            return false;
        }

        state = candidate;
        return true;
    }

    private void Expire(Guid tabId, BatchState expected)
    {
        var pair = new KeyValuePair<Guid, BatchState>(tabId, expected);
        if (((ICollection<KeyValuePair<Guid, BatchState>>)batches).Remove(pair))
        {
            expected.Expiry?.Dispose();
        }
    }

    private sealed class BatchState
    {
        internal BatchState(
            WebMessageOwner owner,
            Guid batchId,
            long expectedRevision,
            IReadOnlyList<DocumentChangeDeclaration> changes,
            DateTimeOffset startedAt)
        {
            Owner = owner;
            BatchId = batchId;
            ExpectedRevision = expectedRevision;
            Changes = changes;
            StartedAt = startedAt;
            Builders = Enumerable.Range(0, changes.Count)
                .Select(_ => new StringBuilder())
                .ToArray();
            AdvancePastCompletedChanges();
        }

        internal WebMessageOwner Owner { get; }

        internal Guid BatchId { get; }

        internal long ExpectedRevision { get; }

        internal IReadOnlyList<DocumentChangeDeclaration> Changes { get; }

        internal DateTimeOffset StartedAt { get; }

        internal StringBuilder[] Builders { get; }

        internal Timer? Expiry { get; set; }

        internal int NextChangeIndex { get; private set; }

        internal void AdvancePastCompletedChanges()
        {
            while (NextChangeIndex < Changes.Count &&
                   Builders[NextChangeIndex].Length == Changes[NextChangeIndex].InsertedLength)
            {
                NextChangeIndex++;
            }
        }
    }
}
