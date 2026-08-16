using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Recovery;

namespace MarkUpViewMini.App.ViewModels;

public enum RecoveryChoice
{
    Restore,
    UseOriginal,
    Compare,
}

public sealed record RecoveryReadOnlySnapshot(string Path, string Text)
{
    public bool IsReadOnly => true;
}

public sealed record RecoveryComparisonViewModel(
    RecoveryReadOnlySnapshot Recovered,
    RecoveryReadOnlySnapshot Original);

public sealed class RecoveryPromptViewModel
{
    private static readonly IReadOnlyList<RecoveryChoice> Choices = Array.AsReadOnly(
        [RecoveryChoice.Restore, RecoveryChoice.UseOriginal, RecoveryChoice.Compare]);

    public RecoveryPromptViewModel(RecoveryRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public RecoveryRecord Record { get; }

    public IReadOnlyList<RecoveryChoice> AvailableChoices => Choices;

    public DocumentBuffer Restore() =>
        DocumentBuffer.Restore(
            Record.TabId,
            Record.Path,
            Record.DecodeBody(),
            Record.Encoding,
            Record.NewLine,
            Record.PreferredNewLine,
            Record.BaselineVersion,
            Record.Revision);

    public async Task UseOriginalAsync(
        Func<CancellationToken, Task> useOriginal,
        Func<Guid, CancellationToken, Task> removeRecovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(useOriginal);
        ArgumentNullException.ThrowIfNull(removeRecovery);
        await useOriginal(cancellationToken).ConfigureAwait(false);
        await removeRecovery(Record.TabId, cancellationToken).ConfigureAwait(false);
    }

    public RecoveryComparisonViewModel Compare(string originalText)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        return new RecoveryComparisonViewModel(
            new RecoveryReadOnlySnapshot(Record.Path, Record.DecodeBody()),
            new RecoveryReadOnlySnapshot(Record.Path, originalText));
    }
}
