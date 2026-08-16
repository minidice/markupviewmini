using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Core.Tests.Documents;

public sealed class DocumentBufferTests
{
    private static readonly DiskFileVersion InitialVersion =
        new(6, DateTime.UnixEpoch, new string('a', 64));

    [Fact]
    public void Apply_uses_pre_edit_utf16_ranges_and_increments_once()
    {
        var buffer = CreateBuffer("A\r\nB\nC");

        var revision = buffer.Apply(new DocumentEdit(
            0,
            [new TextChange(3, 4, "Bee"), new TextChange(6, 6, "!")]));

        Assert.Equal("A\r\nBee\nC!", buffer.Text);
        Assert.Equal(1, revision);
        Assert.Equal(1, buffer.Revision);
        Assert.True(buffer.IsDirty);
    }

    [Fact]
    public void Apply_uses_utf16_code_unit_offsets_for_surrogate_pairs()
    {
        var buffer = CreateBuffer("A😀B");

        buffer.Apply(new DocumentEdit(0, [new TextChange(1, 3, "🙂")]));

        Assert.Equal("A🙂B", buffer.Text);
    }

    [Fact]
    public void Apply_rejects_empty_changes_without_mutation()
    {
        var buffer = CreateBuffer("abcd");

        Assert.Throws<ArgumentException>(() =>
            buffer.Apply(new DocumentEdit(0, [])));

        AssertUnchanged(buffer, "abcd");
    }

    [Fact]
    public void Apply_rejects_stale_revision_without_mutation()
    {
        var buffer = CreateBuffer("abcd");

        var exception = Assert.Throws<StaleDocumentRevisionException>(() =>
            buffer.Apply(new DocumentEdit(1, [new TextChange(0, 1, "A")])));

        Assert.Equal(1, exception.ExpectedRevision);
        Assert.Equal(0, exception.ActualRevision);
        AssertUnchanged(buffer, "abcd");
    }

    [Theory]
    [MemberData(nameof(InvalidChanges))]
    public void Apply_rejects_overlapping_unsorted_or_out_of_range_changes(
        IReadOnlyList<TextChange> changes)
    {
        var buffer = CreateBuffer("abcd");

        Assert.Throws<ArgumentException>(() =>
            buffer.Apply(new DocumentEdit(0, changes)));

        AssertUnchanged(buffer, "abcd");
    }

    [Fact]
    public void Apply_validates_every_change_before_mutating()
    {
        var buffer = CreateBuffer("abcd");

        Assert.Throws<ArgumentException>(() => buffer.Apply(new DocumentEdit(
            0,
            [new TextChange(0, 1, "A"), new TextChange(4, 5, "!")])));

        AssertUnchanged(buffer, "abcd");
    }

    [Fact]
    public void Apply_preserves_untouched_mixed_newlines()
    {
        var buffer = CreateBuffer("first\r\nsecond\nthird\rfour");

        buffer.Apply(new DocumentEdit(0, [new TextChange(7, 13, "SECOND")]));

        Assert.Equal("first\r\nSECOND\nthird\rfour", buffer.Text);
        Assert.Equal(NewLineKind.Mixed, buffer.NewLine);
        Assert.Equal("\r\n", buffer.PreferredNewLine);
    }

    [Fact]
    public void MarkSaved_rejects_completion_after_a_later_edit_without_mutation()
    {
        var buffer = CreateBuffer("abcd");
        var savedRevision = buffer.Apply(
            new DocumentEdit(0, [new TextChange(4, 4, "!")]));
        buffer.Apply(new DocumentEdit(1, [new TextChange(0, 1, "A")]));
        var laterVersion = new DiskFileVersion(
            5,
            DateTime.UnixEpoch.AddDays(1),
            new string('b', 64));

        Assert.Throws<StaleDocumentRevisionException>(() =>
            buffer.MarkSaved(savedRevision, laterVersion));

        Assert.Equal(InitialVersion, buffer.BaselineVersion);
        Assert.Equal(2, buffer.Revision);
        Assert.True(buffer.IsDirty);
    }

    [Fact]
    public void MarkSaved_checks_stale_revision_before_validating_new_version()
    {
        var buffer = CreateBuffer("abcd");
        buffer.Apply(new DocumentEdit(0, [new TextChange(4, 4, "!")]));

        var exception = Assert.Throws<StaleDocumentRevisionException>(() =>
            buffer.MarkSaved(0, null!));

        Assert.Equal(0, exception.ExpectedRevision);
        Assert.Equal(1, exception.ActualRevision);
        Assert.Equal("abcd!", buffer.Text);
        Assert.Equal(InitialVersion, buffer.BaselineVersion);
        Assert.Equal(1, buffer.Revision);
        Assert.True(buffer.IsDirty);
    }

    [Fact]
    public void MarkSaved_updates_the_baseline_and_clears_dirty_at_the_current_revision()
    {
        var buffer = CreateBuffer("abcd");
        var revision = buffer.Apply(
            new DocumentEdit(0, [new TextChange(4, 4, "!")]));
        var savedVersion = new DiskFileVersion(
            5,
            DateTime.UnixEpoch.AddDays(1),
            new string('b', 64));

        buffer.MarkSaved(revision, savedVersion);

        Assert.Equal(savedVersion, buffer.BaselineVersion);
        Assert.Equal(1, buffer.Revision);
        Assert.False(buffer.IsDirty);
    }

    [Fact]
    public void ReplaceFromDisk_replaces_authoritative_state_and_advances_revision()
    {
        var buffer = CreateBuffer("old");
        buffer.Apply(new DocumentEdit(0, [new TextChange(3, 3, " dirty")]));
        var replacementVersion = new DiskFileVersion(
            12,
            DateTime.UnixEpoch.AddDays(2),
            new string('c', 64));
        var relativePath = Path.Combine("replacement", "document.md");

        buffer.ReplaceFromDisk(
            relativePath,
            "new\ncontent",
            new EncodingDescriptor("utf-16", true),
            NewLineKind.Lf,
            "\n",
            replacementVersion);

        Assert.Equal(Path.GetFullPath(relativePath), buffer.Path);
        Assert.Equal("new\ncontent", buffer.Text);
        Assert.Equal(new EncodingDescriptor("utf-16", true), buffer.Encoding);
        Assert.Equal(NewLineKind.Lf, buffer.NewLine);
        Assert.Equal("\n", buffer.PreferredNewLine);
        Assert.Equal(replacementVersion, buffer.BaselineVersion);
        Assert.Equal(2, buffer.Revision);
        Assert.False(buffer.IsDirty);
    }

    [Fact]
    public void Create_normalizes_the_document_path()
    {
        var relativePath = Path.Combine("documents", "guide.md");

        var buffer = DocumentBuffer.Create(
            Guid.NewGuid(),
            relativePath,
            "text",
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Lf,
            "\n",
            InitialVersion);

        Assert.Equal(Path.GetFullPath(relativePath), buffer.Path);
    }

    [Fact]
    public void Clone_preserves_an_independent_authoritative_snapshot()
    {
        var buffer = CreateBuffer("before");
        buffer.Apply(new DocumentEdit(0, [new TextChange(6, 6, "!")]));

        var snapshot = buffer.Clone();
        buffer.ReplaceFromDisk(
            "after.md",
            "after",
            new EncodingDescriptor("utf-16", true),
            NewLineKind.Lf,
            "\n",
            new DiskFileVersion(5, DateTime.UnixEpoch.AddDays(3), new string('d', 64)));

        Assert.Equal(buffer.TabId, snapshot.TabId);
        Assert.Equal(Path.GetFullPath("document.md"), snapshot.Path);
        Assert.Equal("before!", snapshot.Text);
        Assert.Equal(1, snapshot.Revision);
        Assert.True(snapshot.IsDirty);
        Assert.Equal(new EncodingDescriptor("utf-8", false), snapshot.Encoding);
        Assert.Equal(InitialVersion, snapshot.BaselineVersion);
    }

    [Fact]
    public async Task CaptureSnapshot_is_coherent_while_apply_and_save_change_all_fields()
    {
        var buffer = DocumentBuffer.Create(
            Guid.NewGuid(),
            "document-0.md",
            string.Empty,
            new EncodingDescriptor("utf-16", true),
            NewLineKind.Mixed,
            "\r\n",
            new DiskFileVersion(0, DateTime.UnixEpoch, 0L.ToString("x64")));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (var revision = 1; revision <= 500; revision++)
            {
                buffer.Apply(new DocumentEdit(
                    revision - 1,
                    [new TextChange(buffer.Text.Length, buffer.Text.Length, "x")]));
                await Task.Yield();
                buffer.CompleteSave(
                    revision,
                    new DiskFileVersion(
                        revision,
                        DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(revision), DateTimeKind.Utc),
                        revision.ToString("x64")),
                    $"document-{revision}.md",
                    revision % 2 == 0
                        ? new EncodingDescriptor("utf-16", true)
                        : new EncodingDescriptor("utf-8", false));
                await Task.Yield();
            }
        });

        start.SetResult();
        var captured = 0;
        while (!writer.IsCompleted)
        {
            var snapshot = buffer.CaptureSnapshot();
            captured++;
            Assert.Equal(snapshot.Revision, snapshot.Text.Length);
            var metadataRevision = snapshot.IsDirty
                ? snapshot.Revision - 1
                : snapshot.Revision;
            Assert.Equal(metadataRevision, snapshot.BaselineVersion.Length);
            Assert.Equal(metadataRevision.ToString("x64"), snapshot.BaselineVersion.Sha256);
            Assert.EndsWith($"document-{metadataRevision}.md", snapshot.Path, StringComparison.Ordinal);
            Assert.Equal(
                metadataRevision % 2 == 0 ? "utf-16" : "utf-8",
                snapshot.Encoding.WebName);
            if (!snapshot.IsDirty && snapshot.Revision > 0)
            {
                Assert.Equal(
                    DateTime.UnixEpoch.AddSeconds(snapshot.Revision),
                    snapshot.BaselineVersion.LastWriteTimeUtc);
            }
        }

        await writer;
        var final = buffer.CaptureSnapshot();
        Assert.Equal(500, final.Revision);
        Assert.Equal(500, final.Text.Length);
        Assert.False(final.IsDirty);
        Assert.True(captured > 0);
    }

    public static TheoryData<IReadOnlyList<TextChange>> InvalidChanges => new()
    {
        { new TextChange[] { new(2, 4, "x"), new(1, 3, "y") } },
        { new TextChange[] { new(0, 3, "x"), new(2, 4, "y") } },
        { new TextChange[] { new(-1, 0, "x") } },
        { new TextChange[] { new(2, 1, "x") } },
        { new TextChange[] { new(4, 5, "x") } },
    };

    private static DocumentBuffer CreateBuffer(string text) =>
        DocumentBuffer.Create(
            Guid.NewGuid(),
            "document.md",
            text,
            new EncodingDescriptor("utf-8", false),
            NewLineKind.Mixed,
            "\r\n",
            InitialVersion);

    private static void AssertUnchanged(DocumentBuffer buffer, string expectedText)
    {
        Assert.Equal(expectedText, buffer.Text);
        Assert.Equal(0, buffer.Revision);
        Assert.False(buffer.IsDirty);
        Assert.Equal(InitialVersion, buffer.BaselineVersion);
    }
}
