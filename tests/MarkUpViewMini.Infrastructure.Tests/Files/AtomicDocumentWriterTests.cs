using System.Security.Cryptography;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.Infrastructure.Tests.Files;

public sealed class AtomicDocumentWriterTests
{
    [Fact]
    public async Task Existing_target_is_created_written_flushed_replaced_and_cleaned_in_order()
    {
        var files = new RecordingFileAccess();
        var writer = new AtomicDocumentWriter(files);
        var target = Path.GetFullPath("existing.md");
        files.Seed(target, [1]);

        files.CurrentVersion = Version('a');
        var result = Assert.IsType<AtomicWriteResult.Committed>(await writer.WriteAsync(
            target, new byte[] { 2, 3 }, files.CurrentVersion, CancellationToken.None));

        Assert.Equal(
            ["temp", "create", "write", "flush", "metadata", "read", "replace", "cleanup"],
            files.Operations);
        Assert.Equal([2, 3], files.Bytes[target]);
        Assert.Equal(2, result.Version.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([2, 3])).ToLowerInvariant(), result.Version.Sha256);
    }

    [Fact]
    public async Task New_target_uses_atomic_move_after_durable_temp_write()
    {
        var files = new RecordingFileAccess();
        var writer = new AtomicDocumentWriter(files);
        var target = Path.GetFullPath("new.md");

        _ = await writer.WriteAsync(target, new byte[] { 4, 5 }, null, CancellationToken.None);

        Assert.Equal(
            ["temp", "create", "write", "flush", "metadata", "read", "move", "cleanup"],
            files.Operations);
        Assert.Equal([4, 5], files.Bytes[target]);
    }

    [Theory]
    [InlineData(true, "replace")]
    [InlineData(false, "move")]
    public async Task Committed_version_is_computed_before_final_revalidation_and_immediate_commit(
        bool targetExists,
        string commitOperation)
    {
        var files = new RecordingFileAccess();
        var target = Path.GetFullPath(targetExists ? "existing.md" : "new.md");
        if (targetExists)
        {
            files.Seed(target, [1]);
        }

        var writer = new AtomicDocumentWriter(
            files,
            (bytes, lastWriteTimeUtc) =>
            {
                files.Operations.Add("hash");
                return new DiskFileVersion(
                    bytes.Length,
                    lastWriteTimeUtc,
                    Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant());
            });

        _ = await writer.WriteAsync(
            target,
            new byte[] { 2, 3 },
            files.CurrentVersion,
            CancellationToken.None);

        Assert.Equal(
            ["temp", "create", "write", "flush", "metadata", "hash", "read", commitOperation, "cleanup"],
            files.Operations);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("replace")]
    public async Task Existing_target_survives_each_failure_and_temp_cleanup_is_attempted(string failure)
    {
        var files = new RecordingFileAccess { FailAt = failure };
        var writer = new AtomicDocumentWriter(files);
        var target = Path.GetFullPath("existing.md");
        files.Seed(target, [9]);

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(target, new byte[] { 1, 2 }, files.CurrentVersion, CancellationToken.None));

        Assert.Equal([9], files.Bytes[target]);
        Assert.Equal("cleanup", files.Operations[^1]);
        Assert.DoesNotContain(files.TemporaryPath, files.Bytes.Keys);
    }

    [Fact]
    public async Task Metadata_failure_happens_before_replace_and_preserves_the_original()
    {
        var files = new RecordingFileAccess { FailAt = "metadata" };
        var writer = new AtomicDocumentWriter(files);
        var target = Path.GetFullPath("existing.md");
        files.Seed(target, [9]);

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(target, new byte[] { 1, 2 }, files.CurrentVersion, CancellationToken.None));

        Assert.Equal([9], files.Bytes[target]);
        Assert.DoesNotContain("replace", files.Operations);
        Assert.Equal("cleanup", files.Operations[^1]);
    }

    [Fact]
    public async Task Cleanup_failure_does_not_mask_the_primary_write_failure()
    {
        var files = new RecordingFileAccess { FailAt = "write+cleanup" };
        var writer = new AtomicDocumentWriter(files);
        var target = Path.GetFullPath("existing.md");
        files.Seed(target, [9]);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(target, new byte[] { 1, 2 }, files.CurrentVersion, CancellationToken.None));

        Assert.Equal("write", error.Message);
        Assert.Equal([9], files.Bytes[target]);
        Assert.Equal("cleanup", files.Operations[^1]);
    }

    private sealed class RecordingFileAccess : IFileAccess
    {
        internal Dictionary<string, byte[]> Bytes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Operations { get; } = [];
        internal string? FailAt { get; init; }
        internal string TemporaryPath { get; private set; } = string.Empty;

        internal DiskFileVersion? CurrentVersion { get; set; }

        public Task<DiskFileVersion?> ReadVersionAsync(string path, CancellationToken cancellationToken)
        {
            Operations.Add("read");
            return Task.FromResult(CurrentVersion);
        }

        public string CreateTemporaryPath(string targetPath)
        {
            Operations.Add("temp");
            TemporaryPath = Path.Combine(Path.GetDirectoryName(targetPath)!, $".{Path.GetFileName(targetPath)}.test.tmp");
            return TemporaryPath;
        }

        public Task CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            Operations.Add("create");
            ThrowIf("create");
            Bytes.Add(path, []);
            return Task.CompletedTask;
        }

        public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Operations.Add("write");
            ThrowIf("write");
            Bytes[path] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public void FlushToDisk(string path)
        {
            Operations.Add("flush");
            ThrowIf("flush");
        }

        public void Replace(string sourcePath, string destinationPath)
        {
            Operations.Add("replace");
            ThrowIf("replace");
            Bytes[destinationPath] = Bytes[sourcePath];
            Bytes.Remove(sourcePath);
        }

        public void Move(string sourcePath, string destinationPath)
        {
            Operations.Add("move");
            ThrowIf("move");
            Bytes.Add(destinationPath, Bytes[sourcePath]);
            Bytes.Remove(sourcePath);
        }

        public void DeleteIfExists(string path)
        {
            Operations.Add("cleanup");
            ThrowIf("cleanup");
            Bytes.Remove(path);
        }

        public DateTime GetLastWriteTimeUtc(string path)
        {
            Operations.Add("metadata");
            ThrowIf("metadata");
            return DateTime.UnixEpoch.AddDays(1);
        }

        internal void Seed(string path, byte[] bytes)
        {
            Bytes[path] = bytes;
            CurrentVersion ??= Version('a');
        }

        private void ThrowIf(string operation)
        {
            if (FailAt?.Split('+').Contains(operation, StringComparer.Ordinal) == true)
            {
                throw new IOException(operation);
            }
        }
    }

    private static DiskFileVersion Version(char hash) =>
        new(1, DateTime.UnixEpoch, new string(hash, 64));
}
