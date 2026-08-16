using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Infrastructure.Files;

internal abstract record AtomicWriteResult
{
    internal sealed record Committed(DiskFileVersion Version) : AtomicWriteResult;

    internal sealed record Conflict(DiskFileVersion? Current) : AtomicWriteResult;
}

internal sealed class AtomicDocumentWriter
{
    private readonly IFileAccess files;
    private readonly Func<ReadOnlyMemory<byte>, DateTime, DiskFileVersion> createVersion;

    internal AtomicDocumentWriter(IFileAccess files)
        : this(
            files,
            (bytes, lastWriteTimeUtc) =>
                DocumentFileService.CreateVersion(bytes.Span, lastWriteTimeUtc))
    {
    }

    internal AtomicDocumentWriter(
        IFileAccess files,
        Func<ReadOnlyMemory<byte>, DateTime, DiskFileVersion> createVersion)
    {
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.createVersion = createVersion ?? throw new ArgumentNullException(nameof(createVersion));
    }

    internal async Task<AtomicWriteResult> WriteAsync(
        string targetPath,
        ReadOnlyMemory<byte> bytes,
        DiskFileVersion? expectedCurrent,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(targetPath);
        var temporary = files.CreateTemporaryPath(target);
        if (!string.Equals(
                Path.GetDirectoryName(target),
                Path.GetDirectoryName(Path.GetFullPath(temporary)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The temporary file must be in the target directory.");
        }

        try
        {
            await files.CreateNewAsync(temporary, cancellationToken).ConfigureAwait(false);
            await files.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            files.FlushToDisk(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            var version = createVersion(bytes, files.GetLastWriteTimeUtc(temporary));
            var current = await files.ReadVersionAsync(target, cancellationToken).ConfigureAwait(false);
            if (!Equals(current, expectedCurrent))
            {
                return new AtomicWriteResult.Conflict(current);
            }

            if (current is not null)
            {
                files.Replace(temporary, target);
            }
            else
            {
                files.Move(temporary, target);
            }

            return new AtomicWriteResult.Committed(version);
        }
        finally
        {
            try
            {
                files.DeleteIfExists(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
