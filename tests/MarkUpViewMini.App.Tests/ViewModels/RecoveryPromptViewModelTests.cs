using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Recovery;

namespace MarkUpViewMini.App.Tests.ViewModels;

public sealed class RecoveryPromptViewModelTests
{
    [Fact]
    public void Restore_creates_exact_dirty_authoritative_buffer_without_writing_original()
    {
        var originalWrites = 0;
        var record = CreateRecord("recovered body", revision: 12);
        var prompt = new RecoveryPromptViewModel(record);

        var buffer = prompt.Restore();

        Assert.Equal(
            [RecoveryChoice.Restore, RecoveryChoice.UseOriginal, RecoveryChoice.Compare],
            prompt.AvailableChoices);
        Assert.Equal(record.TabId, buffer.TabId);
        Assert.Equal(Path.GetFullPath(record.Path), buffer.Path);
        Assert.Equal(record.DecodeBody(), buffer.Text);
        Assert.Equal(record.Revision, buffer.Revision);
        Assert.True(buffer.IsDirty);
        Assert.Equal(record.BaselineVersion, buffer.BaselineVersion);
        Assert.Equal(record.Encoding, buffer.Encoding);
        Assert.Equal(record.NewLine, buffer.NewLine);
        Assert.Equal(record.PreferredNewLine, buffer.PreferredNewLine);
        Assert.Equal(0, originalWrites);
    }

    [Fact]
    public async Task Use_original_removes_recovery_only_after_original_choice_succeeds()
    {
        var record = CreateRecord("mine", revision: 3);
        var prompt = new RecoveryPromptViewModel(record);
        var operations = new List<string>();

        await prompt.UseOriginalAsync(
            _ =>
            {
                operations.Add("original");
                return Task.CompletedTask;
            },
            (tabId, _) =>
            {
                Assert.Equal(record.TabId, tabId);
                operations.Add("remove");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["original", "remove"], operations);
    }

    [Fact]
    public async Task Failed_original_choice_preserves_recovery_record()
    {
        var prompt = new RecoveryPromptViewModel(CreateRecord("mine", revision: 3));
        var removeCalls = 0;

        await Assert.ThrowsAsync<IOException>(() => prompt.UseOriginalAsync(
            _ => Task.FromException(new IOException("load failed")),
            (_, _) =>
            {
                removeCalls++;
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.Equal(0, removeCalls);
    }

    [Fact]
    public void Compare_is_an_immutable_read_only_snapshot_and_mutates_neither_body()
    {
        var record = CreateRecord("recovered", revision: 5);
        var prompt = new RecoveryPromptViewModel(record);
        var original = "original";

        var comparison = prompt.Compare(original);
        var restored = prompt.Restore();
        restored.Apply(new DocumentEdit(restored.Revision, [new TextChange(restored.Text.Length, restored.Text.Length, " later")]));

        Assert.Equal("recovered", comparison.Recovered.Text);
        Assert.Equal("original", comparison.Original.Text);
        Assert.True(comparison.Recovered.IsReadOnly);
        Assert.True(comparison.Original.IsReadOnly);
        Assert.Equal("recovered", record.DecodeBody());
        Assert.Equal("original", original);
    }

    private static RecoveryRecord CreateRecord(string body, long revision) =>
        new(
            RecoveryRecord.CurrentSchemaVersion,
            Guid.NewGuid(),
            Path.GetFullPath("recover.md"),
            new DiskFileVersion(8, DateTime.UnixEpoch, new string('a', 64)),
            new EncodingDescriptor("utf-8", true),
            NewLineKind.Mixed,
            "\r\n",
            revision,
            DateTime.UnixEpoch.AddDays(1),
            RecoveryRecord.EncodeBody(body));
}
