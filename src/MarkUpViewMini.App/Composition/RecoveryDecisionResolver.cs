using System.IO;
using System.Text;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Recovery;

namespace MarkUpViewMini.App.Composition;

internal enum RecoveryDecisionKind
{
    Restore,
    UseOriginal,
    Compare,
    Cancel,
}

internal sealed record RecoveryStartupResolution(
    bool IsCancelled,
    IReadOnlyList<DocumentBuffer> RestoredBuffers)
{
    public static RecoveryStartupResolution Completed(IReadOnlyList<DocumentBuffer>? buffers = null) =>
        new(false, buffers ?? []);

    public static RecoveryStartupResolution Cancelled() => new(true, []);
}

internal interface IRecoveryDecisionResolver
{
    Task<RecoveryStartupResolution> ResolveAsync(CancellationToken cancellationToken);
}

internal interface IRecoveryDecisionDialog
{
    Task<RecoveryDecisionKind> ChooseAsync(
        RecoveryPromptViewModel prompt,
        RecoveryComparisonViewModel? comparison,
        CancellationToken cancellationToken);

    void ShowOriginalReadError();
}

internal sealed class RecoveryDecisionResolver(
    Func<CancellationToken, Task<IReadOnlyList<RecoveryRecord>>> loadAvailable,
    IRecoveryDecisionDialog dialog,
    Func<string, CancellationToken, Task<string>> readOriginal,
    Func<Guid, CancellationToken, Task> removeRecovery) : IRecoveryDecisionResolver
{
    public async Task<RecoveryStartupResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        var records = await loadAvailable(cancellationToken).ConfigureAwait(true);
        var restored = new List<DocumentBuffer>();
        foreach (var record in records)
        {
            var prompt = new RecoveryPromptViewModel(record);
            RecoveryComparisonViewModel? comparison = null;
            while (true)
            {
                var decision = await dialog.ChooseAsync(prompt, comparison, cancellationToken)
                    .ConfigureAwait(true);
                comparison = null;
                switch (decision)
                {
                    case RecoveryDecisionKind.Restore:
                        restored.Add(prompt.Restore());
                        goto Resolved;
                    case RecoveryDecisionKind.UseOriginal:
                        if (!await TryUseOriginalAsync(record, cancellationToken).ConfigureAwait(true))
                        {
                            break;
                        }

                        goto Resolved;
                    case RecoveryDecisionKind.Compare:
                        var original = await TryReadOriginalAsync(record.Path, cancellationToken)
                            .ConfigureAwait(true);
                        if (original is not null)
                        {
                            comparison = prompt.Compare(original);
                        }

                        break;
                    case RecoveryDecisionKind.Cancel:
                        return RecoveryStartupResolution.Cancelled();
                    default:
                        throw new InvalidOperationException("The recovery decision is invalid.");
                }
            }

        Resolved:
            continue;
        }

        return RecoveryStartupResolution.Completed(restored);
    }

    private async Task<bool> TryUseOriginalAsync(
        RecoveryRecord record,
        CancellationToken cancellationToken)
    {
        if (await TryReadOriginalAsync(record.Path, cancellationToken).ConfigureAwait(true) is null)
        {
            return false;
        }

        await removeRecovery(record.TabId, cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async Task<string?> TryReadOriginalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await readOriginal(path, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            dialog.ShowOriginalReadError();
            return null;
        }
    }
}
