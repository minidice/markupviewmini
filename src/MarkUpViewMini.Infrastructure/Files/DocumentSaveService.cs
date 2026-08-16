using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Persistence;

namespace MarkUpViewMini.Infrastructure.Files;

public sealed class DocumentSaveService
{
    private readonly DocumentFormatRegistry formatRegistry;
    private readonly AtomicDocumentWriter writer;
    private readonly IFileAccess files;
    private readonly DocumentSaveArbiter saveArbiter;

    internal DocumentSaveArbiter SaveArbiter => saveArbiter;

    public DocumentSaveService(DocumentFormatRegistry formatRegistry)
        : this(formatRegistry, new DocumentSaveArbiter())
    {
    }

    public DocumentSaveService(
        DocumentFormatRegistry formatRegistry,
        DocumentSaveArbiter saveArbiter)
        : this(formatRegistry, new PhysicalFileAccess(), saveArbiter)
    {
    }

    private DocumentSaveService(
        DocumentFormatRegistry formatRegistry,
        IFileAccess files,
        DocumentSaveArbiter saveArbiter)
        : this(formatRegistry, new AtomicDocumentWriter(files), files, saveArbiter)
    {
    }

    internal DocumentSaveService(
        DocumentFormatRegistry formatRegistry,
        AtomicDocumentWriter writer,
        IFileAccess files,
        DocumentSaveArbiter saveArbiter)
    {
        this.formatRegistry = formatRegistry ?? throw new ArgumentNullException(nameof(formatRegistry));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.saveArbiter = saveArbiter ?? throw new ArgumentNullException(nameof(saveArbiter));
    }

    public async Task<SaveResult> SaveAsync(
        DocumentBuffer buffer,
        SaveDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(decision);

        var snapshot = buffer.CaptureSnapshot();
        var text = snapshot.Text;
        var savedRevision = snapshot.Revision;
        var originalPath = snapshot.Path;
        var baseline = snapshot.BaselineVersion;
        var originalEncoding = snapshot.Encoding;

        var (targetPath, encoding, expectedVersion, verifyVersion) = decision switch
        {
            SaveDecision.Normal => (originalPath, originalEncoding, baseline, true),
            SaveDecision.UseMyVersion overwrite =>
                (originalPath, originalEncoding, overwrite.ObservedCurrent, true),
            SaveDecision.SaveAs saveAs =>
                (Path.GetFullPath(saveAs.TargetPath), saveAs.Encoding, (DiskFileVersion?)null, false),
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };

        ArgumentNullException.ThrowIfNull(encoding);
        if (decision is SaveDecision.SaveAs)
        {
            _ = formatRegistry.Resolve(targetPath);
        }

        await using var saveLease = await saveArbiter
            .AcquireAsync(targetPath, cancellationToken)
            .ConfigureAwait(false);

        var current = await files.ReadVersionAsync(targetPath, cancellationToken);
        if (verifyVersion && !Equals(current, expectedVersion))
        {
            return new SaveResult.Conflict(current);
        }

        var bytes = Encode(text, encoding);
        var write = await writer.WriteAsync(
                targetPath,
                bytes,
                current,
                cancellationToken);
        if (write is AtomicWriteResult.Conflict conflict)
        {
            return new SaveResult.Conflict(conflict.Current);
        }

        var version = ((AtomicWriteResult.Committed)write).Version;
        return new SaveResult.Saved(version, savedRevision);
    }

    private static byte[] Encode(string text, EncodingDescriptor descriptor)
    {
        var encoding = DocumentFileService.CreateStrictEncoding(descriptor);

        var body = encoding.GetBytes(text);
        if (!descriptor.EmitPreamble)
        {
            return body;
        }

        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            throw new InvalidOperationException("The selected encoding does not define a preamble.");
        }

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }
}
