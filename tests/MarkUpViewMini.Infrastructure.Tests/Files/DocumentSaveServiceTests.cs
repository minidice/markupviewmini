using System.Security.Cryptography;
using System.Text;
using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.Infrastructure.Tests.Files;

public sealed class DocumentSaveServiceTests
{
    private static readonly DiskFileVersion Baseline =
        new(4, DateTime.UnixEpoch, new string('a', 64));

    public DocumentSaveServiceTests() => DocumentFileService.RegisterCodePages();

    [Fact]
    public async Task Normal_save_writes_exact_snapshot_without_mutating_buffer_ownership()
    {
        var files = new SaveFileAccess();
        var buffer = Buffer("a\r\nb\nc\rd", new EncodingDescriptor("utf-8", false));
        buffer.Apply(new DocumentEdit(0, [new TextChange(3, 4, "B")]));
        files.CurrentVersion = Baseline;
        var service = Service(files);

        var result = Assert.IsType<SaveResult.Saved>(await service.SaveAsync(
            buffer, new SaveDecision.Normal(), CancellationToken.None));

        Assert.Equal(1, result.SavedRevision);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes("a\r\nB\nc\rd"), files.CommittedBytes);
        Assert.True(buffer.IsDirty);
        Assert.Equal(Baseline, buffer.BaselineVersion);
    }

    [Fact]
    public async Task Changed_disk_version_returns_conflict_without_any_write_operation()
    {
        var current = new DiskFileVersion(5, DateTime.UnixEpoch.AddDays(1), new string('b', 64));
        var files = new SaveFileAccess { CurrentVersion = current };
        var service = Service(files);

        var result = Assert.IsType<SaveResult.Conflict>(await service.SaveAsync(
            Buffer("text"), new SaveDecision.Normal(), CancellationToken.None));

        Assert.Equal(current, result.Current);
        Assert.Empty(files.WriteOperations);
    }

    [Fact]
    public async Task Explicit_overwrite_requires_the_observed_disk_token_to_still_match()
    {
        var observed = new DiskFileVersion(5, DateTime.UnixEpoch.AddDays(1), new string('b', 64));
        var later = new DiskFileVersion(6, DateTime.UnixEpoch.AddDays(2), new string('c', 64));
        var files = new SaveFileAccess { CurrentVersion = later };
        var service = Service(files);

        var result = Assert.IsType<SaveResult.Conflict>(await service.SaveAsync(
            Buffer("text"), new SaveDecision.UseMyVersion(observed), CancellationToken.None));

        Assert.Equal(later, result.Current);
        Assert.Empty(files.WriteOperations);
    }

    [Fact]
    public async Task External_change_after_temp_flush_returns_conflict_without_replacing_target()
    {
        var changed = new DiskFileVersion(7, DateTime.UnixEpoch.AddDays(4), new string('d', 64));
        var files = new SaveFileAccess
        {
            CurrentVersion = Baseline,
            VersionAfterFlush = changed,
        };
        var buffer = Buffer("text");
        var service = Service(files);

        var result = Assert.IsType<SaveResult.Conflict>(await service.SaveAsync(
            buffer, new SaveDecision.Normal(), CancellationToken.None));

        Assert.Equal(changed, result.Current);
        Assert.DoesNotContain("replace", files.WriteOperations);
        Assert.Equal("cleanup", files.WriteOperations[^1]);
        Assert.Equal(Baseline, buffer.BaselineVersion);
    }

    [Theory]
    [MemberData(nameof(EncodedCases))]
    public async Task Save_preserves_strict_encoding_and_preamble_policy(
        string text,
        EncodingDescriptor descriptor,
        byte[] expected)
    {
        var files = new SaveFileAccess { CurrentVersion = Baseline };
        var service = Service(files);

        await service.SaveAsync(Buffer(text, descriptor), new SaveDecision.Normal(), CancellationToken.None);

        Assert.Equal(expected, files.CommittedBytes);
    }

    [Fact]
    public async Task Unencodable_text_fails_before_a_temp_file_is_created()
    {
        var files = new SaveFileAccess { CurrentVersion = Baseline };
        var service = Service(files);

        await Assert.ThrowsAsync<EncoderFallbackException>(() => service.SaveAsync(
            Buffer("emoji 😀", new EncodingDescriptor("ks_c_5601-1987", false)),
            new SaveDecision.Normal(),
            CancellationToken.None));

        Assert.Empty(files.WriteOperations);
    }

    [Fact]
    public async Task Edit_during_writer_wait_commits_the_snapshot_but_keeps_buffer_dirty()
    {
        var files = new SaveFileAccess { CurrentVersion = Baseline, PauseWrite = true };
        var buffer = Buffer("before");
        buffer.Apply(new DocumentEdit(0, [new TextChange(6, 6, " saved")]));
        var service = Service(files);

        var saving = service.SaveAsync(buffer, new SaveDecision.Normal(), CancellationToken.None);
        await files.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        buffer.Apply(new DocumentEdit(1, [new TextChange(0, 0, "later ")]));
        files.ReleaseWrite.SetResult();
        var result = Assert.IsType<SaveResult.Saved>(await saving);

        Assert.Equal("before saved", Encoding.UTF8.GetString(files.CommittedBytes!));
        Assert.Equal(1, result.SavedRevision);
        Assert.Equal(2, buffer.Revision);
        Assert.True(buffer.IsDirty);
        Assert.Equal(Baseline, buffer.BaselineVersion);
    }

    [Fact]
    public async Task SaveAs_rejects_an_unregistered_extension_before_disk_access()
    {
        var files = new SaveFileAccess();
        var service = Service(files);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.SaveAsync(
            Buffer("text"),
            new SaveDecision.SaveAs("document.txt", new EncodingDescriptor("utf-8", false)),
            CancellationToken.None));

        Assert.Equal(0, files.ReadCount);
        Assert.Empty(files.WriteOperations);
    }

    [Fact]
    public async Task Shared_path_arbiter_prevents_an_older_save_from_replacing_a_later_invocation()
    {
        var disk = new ConcurrentSaveDisk(Baseline);
        var firstFiles = new ConcurrentSaveFileAccess(disk, pauseFinalVersionRead: true);
        var secondFiles = new ConcurrentSaveFileAccess(disk, pauseFinalVersionRead: false);
        var arbiter = new DocumentSaveArbiter();
        var firstBuffer = Buffer("revision one");
        firstBuffer.Apply(new DocumentEdit(0, [new TextChange(12, 12, "!")]));
        var secondBuffer = Buffer("revision two");
        secondBuffer.Apply(new DocumentEdit(0, [new TextChange(12, 12, "!")]));
        secondBuffer.Apply(new DocumentEdit(1, [new TextChange(13, 13, "!")]));
        var firstService = Service(firstFiles, arbiter);
        var secondService = Service(secondFiles, arbiter);

        var firstSave = firstService.SaveAsync(
            firstBuffer,
            new SaveDecision.Normal(),
            CancellationToken.None);
        await firstFiles.FinalVersionReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondSave = secondService.SaveAsync(
            secondBuffer,
            new SaveDecision.SaveAs(
                firstBuffer.Path.ToUpperInvariant(),
                secondBuffer.Encoding),
            CancellationToken.None);

        Assert.False(secondFiles.FirstReadStarted.Task.IsCompleted);

        firstFiles.ReleaseFinalVersionRead.SetResult();
        Assert.IsType<SaveResult.Saved>(await firstSave);
        Assert.IsType<SaveResult.Saved>(await secondSave);
        Assert.Equal("revision two!!", Encoding.UTF8.GetString(disk.CommittedBytes!));
    }

    [Fact]
    public async Task Shared_path_arbiter_allows_different_targets_to_save_concurrently()
    {
        var arbiter = new DocumentSaveArbiter();
        await using var first = await arbiter.AcquireAsync(
            Path.GetFullPath("first.md"),
            CancellationToken.None);

        var second = arbiter.AcquireAsync(
            Path.GetFullPath("second.md"),
            CancellationToken.None).AsTask();

        await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancelling_a_same_path_waiter_does_not_release_the_active_save()
    {
        var arbiter = new DocumentSaveArbiter();
        var target = Path.GetFullPath("same.md");
        await using var owner = await arbiter.AcquireAsync(target, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelledWait = arbiter.AcquireAsync(target.ToUpperInvariant(), cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        var nextWait = arbiter.AcquireAsync(target, CancellationToken.None).AsTask();
        Assert.False(nextWait.IsCompleted);

        await owner.DisposeAsync();
        await using var next = await nextWait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public static TheoryData<string, EncodingDescriptor, byte[]> EncodedCases
    {
        get
        {
            DocumentFileService.RegisterCodePages();
            var utf8 = new UTF8Encoding(true, true);
            var utf16 = new UnicodeEncoding(false, true, true);
            var cp949 = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return new TheoryData<string, EncodingDescriptor, byte[]>
            {
                { "한글", new EncodingDescriptor("utf-8", true), utf8.GetPreamble().Concat(utf8.GetBytes("한글")).ToArray() },
                { "한글", new EncodingDescriptor("utf-16", true), utf16.GetPreamble().Concat(utf16.GetBytes("한글")).ToArray() },
                { "한글", new EncodingDescriptor("ks_c_5601-1987", false), cp949.GetBytes("한글") },
            };
        }
    }

    private static DocumentBuffer Buffer(string text, EncodingDescriptor? encoding = null) =>
        DocumentBuffer.Create(
            Guid.NewGuid(),
            Path.GetFullPath("document.md"),
            text,
            encoding ?? new EncodingDescriptor("utf-8", false),
            NewLineKind.Mixed,
            "\r\n",
            Baseline);

    private static DocumentSaveService Service(SaveFileAccess files) =>
        new(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            new AtomicDocumentWriter(files),
            files,
            new DocumentSaveArbiter());

    private static DocumentSaveService Service(
        IFileAccess files,
        DocumentSaveArbiter arbiter) =>
        new(
            new DocumentFormatRegistry([new MarkdownDocumentProvider()]),
            new AtomicDocumentWriter(files),
            files,
            arbiter);

    private sealed class ConcurrentSaveDisk(DiskFileVersion initialVersion)
    {
        private readonly object syncRoot = new();
        private DiskFileVersion currentVersion = initialVersion;

        internal byte[]? CommittedBytes { get; private set; }

        internal DiskFileVersion ReadVersion()
        {
            lock (syncRoot)
            {
                return currentVersion;
            }
        }

        internal void Commit(byte[] bytes)
        {
            lock (syncRoot)
            {
                CommittedBytes = bytes;
                currentVersion = new DiskFileVersion(
                    bytes.Length,
                    DateTime.UnixEpoch.AddDays(10),
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
        }
    }

    private sealed class ConcurrentSaveFileAccess(
        ConcurrentSaveDisk disk,
        bool pauseFinalVersionRead) : IFileAccess
    {
        private readonly Dictionary<string, byte[]> temporaryBytes =
            new(StringComparer.OrdinalIgnoreCase);
        private int readCount;

        internal TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FinalVersionReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFinalVersionRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DiskFileVersion?> ReadVersionAsync(
            string path,
            CancellationToken cancellationToken)
        {
            FirstReadStarted.TrySetResult();
            var captured = disk.ReadVersion();
            if (pauseFinalVersionRead && Interlocked.Increment(ref readCount) == 2)
            {
                FinalVersionReadStarted.SetResult();
                await ReleaseFinalVersionRead.Task.WaitAsync(cancellationToken);
            }

            return captured;
        }

        public string CreateTemporaryPath(string targetPath) =>
            targetPath + $".{Guid.NewGuid():N}.tmp";

        public Task CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            temporaryBytes.Add(path, []);
            return Task.CompletedTask;
        }

        public Task WriteAllBytesAsync(
            string path,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            temporaryBytes[path] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public void FlushToDisk(string path)
        {
        }

        public void Replace(string sourcePath, string destinationPath)
        {
            disk.Commit(temporaryBytes[sourcePath]);
            temporaryBytes.Remove(sourcePath);
        }

        public void Move(string sourcePath, string destinationPath) =>
            Replace(sourcePath, destinationPath);

        public void DeleteIfExists(string path) => temporaryBytes.Remove(path);

        public DateTime GetLastWriteTimeUtc(string path) => DateTime.UnixEpoch.AddDays(10);
    }

    private sealed class SaveFileAccess : IFileAccess
    {
        private readonly Dictionary<string, byte[]> bytes = new(StringComparer.OrdinalIgnoreCase);
        internal DiskFileVersion? CurrentVersion { get; set; }
        internal List<string> WriteOperations { get; } = [];
        internal byte[]? CommittedBytes { get; private set; }
        internal int ReadCount { get; private set; }
        internal bool PauseWrite { get; init; }
        internal DiskFileVersion? VersionAfterFlush { get; init; }
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiskFileVersion?> ReadVersionAsync(string path, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(CurrentVersion);
        }

        public string CreateTemporaryPath(string targetPath)
        {
            WriteOperations.Add("temp");
            return targetPath + ".tmp";
        }

        public Task CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            WriteOperations.Add("create");
            bytes[path] = [];
            return Task.CompletedTask;
        }

        public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            WriteOperations.Add("write");
            if (PauseWrite)
            {
                WriteStarted.SetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }

            bytes[path] = value.ToArray();
        }

        public void FlushToDisk(string path)
        {
            WriteOperations.Add("flush");
            if (VersionAfterFlush is not null)
            {
                CurrentVersion = VersionAfterFlush;
            }
        }

        public void Replace(string sourcePath, string destinationPath)
        {
            WriteOperations.Add("replace");
            CommittedBytes = bytes[sourcePath];
            bytes.Remove(sourcePath);
        }

        public void Move(string sourcePath, string destinationPath)
        {
            WriteOperations.Add("move");
            CommittedBytes = bytes[sourcePath];
            bytes.Remove(sourcePath);
        }

        public void DeleteIfExists(string path)
        {
            WriteOperations.Add("cleanup");
            bytes.Remove(path);
        }

        public DateTime GetLastWriteTimeUtc(string path) => DateTime.UnixEpoch.AddDays(3);
    }
}
