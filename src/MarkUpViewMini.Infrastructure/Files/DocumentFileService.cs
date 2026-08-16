using System.Security.Cryptography;
using System.Text;
using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Infrastructure.Files;

public enum DocumentProcessingCheckpoint
{
    BeforeDecode,
    DecodeChunk,
    AfterDecode,
    BeforeHash,
    HashChunk,
    AfterHash,
    BeforeNewLineScan,
    NewLineChunk,
    NewLineCharacter,
    AfterNewLineScan,
}

public sealed class DocumentFileService
{
    private const int ProcessingChunkSize = 64 * 1024;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false, true);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true, true);
    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(false, true, true);
    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(true, true, true);
    private readonly Action<DocumentProcessingCheckpoint>? observeCheckpoint;

    public DocumentFileService()
    {
    }

    internal DocumentFileService(Action<DocumentProcessingCheckpoint> observeCheckpoint)
    {
        this.observeCheckpoint = observeCheckpoint ??
            throw new ArgumentNullException(nameof(observeCheckpoint));
    }

    public static void RegisterCodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal static Encoding CreateStrictEncoding(EncodingDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        RegisterCodePages();
        return Encoding.GetEncoding(
            descriptor.WebName,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    internal static DiskFileVersion CreateVersion(
        ReadOnlySpan<byte> bytes,
        DateTime lastWriteTimeUtc) =>
        new(
            bytes.Length,
            lastWriteTimeUtc,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    public Task<LoadedDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        return LoadAsyncCore(path, Utf8WithoutBom, cancellationToken);
    }

    public Task<LoadedDocument> LoadAsync(
        string path,
        Encoding selectedEncoding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedEncoding);
        var strictEncoding = (Encoding)selectedEncoding.Clone();
        strictEncoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        return LoadAsyncCore(path, strictEncoding, cancellationToken);
    }

    private async Task<LoadedDocument> LoadAsyncCore(
        string path,
        Encoding fallbackEncoding,
        CancellationToken cancellationToken)
    {
        var (bytes, lastWriteTimeUtc) = await ReadSnapshotAsync(path, cancellationToken)
            .ConfigureAwait(false);

        var (encoding, preambleLength) = DetectEncoding(bytes, fallbackEncoding);
        return await Task.Run(
                () => ProcessSnapshot(
                    bytes,
                    lastWriteTimeUtc,
                    encoding,
                    preambleLength,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<(byte[] Bytes, DateTime LastWriteTimeUtc)> ReadSnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var length = stream.Length;
        if (length > Array.MaxLength)
        {
            throw new IOException("The file is too large to load into memory.");
        }

        var lastWriteTimeBeforeRead = File.GetLastWriteTimeUtc(path);
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var lastWriteTimeAfterRead = File.GetLastWriteTimeUtc(path);
        if (stream.Length != bytes.LongLength ||
            lastWriteTimeBeforeRead != lastWriteTimeAfterRead)
        {
            throw new IOException("The file changed while it was being read.");
        }

        return (bytes, lastWriteTimeAfterRead);
    }

    private LoadedDocument ProcessSnapshot(
        byte[] bytes,
        DateTime lastWriteTimeUtc,
        Encoding encoding,
        int preambleLength,
        CancellationToken cancellationToken)
    {
        observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.BeforeDecode);
        cancellationToken.ThrowIfCancellationRequested();
        var text = Decode(bytes, preambleLength, encoding, cancellationToken);
        observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.AfterDecode);
        cancellationToken.ThrowIfCancellationRequested();

        observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.BeforeHash);
        cancellationToken.ThrowIfCancellationRequested();
        var hash = Hash(bytes, cancellationToken);
        observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.AfterHash);
        cancellationToken.ThrowIfCancellationRequested();

        observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.BeforeNewLineScan);
        cancellationToken.ThrowIfCancellationRequested();
        var (newLine, preferredNewLine) = DetectNewLines(text, cancellationToken);
        observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.AfterNewLineScan);
        cancellationToken.ThrowIfCancellationRequested();

        var version = new DiskFileVersion(bytes.LongLength, lastWriteTimeUtc, hash);
        var encodingDescriptor = new EncodingDescriptor(encoding.WebName, preambleLength > 0);
        return new LoadedDocument(text, encodingDescriptor, newLine, preferredNewLine, version);
    }

    private string Decode(
        byte[] bytes,
        int preambleLength,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        if (preambleLength == bytes.Length)
        {
            return string.Empty;
        }

        var decoder = encoding.GetDecoder();
        var builder = new StringBuilder(bytes.Length - preambleLength);
        var charBuffer = new char[encoding.GetMaxCharCount(ProcessingChunkSize)];
        var offset = preambleLength;
        while (offset < bytes.Length)
        {
            observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.DecodeChunk);
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(ProcessingChunkSize, bytes.Length - offset);
            var flush = offset + count == bytes.Length;
            decoder.Convert(
                bytes,
                offset,
                count,
                charBuffer,
                0,
                charBuffer.Length,
                flush,
                out var bytesUsed,
                out var charsUsed,
                out _);
            if (bytesUsed == 0)
            {
                throw new DecoderFallbackException("The selected encoding could not decode the document.");
            }

            builder.Append(charBuffer, 0, charsUsed);
            offset += bytesUsed;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    private string Hash(byte[] bytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < bytes.Length; offset += ProcessingChunkSize)
        {
            observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.HashChunk);
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(bytes.AsSpan(offset, Math.Min(ProcessingChunkSize, bytes.Length - offset)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(
        ReadOnlySpan<byte> bytes,
        Encoding fallbackEncoding)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return (Utf8WithBom, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Utf16LittleEndian, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Utf16BigEndian, 2);
        }

        return (fallbackEncoding, 0);
    }

    private (NewLineKind Kind, string Preferred) DetectNewLines(
        string text,
        CancellationToken cancellationToken)
    {
        long crLfCount = 0;
        long lfCount = 0;
        long crCount = 0;
        var firstCrLf = int.MaxValue;
        var firstLf = int.MaxValue;
        var firstCr = int.MaxValue;
        long nextCancellationCheck = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (index >= nextCancellationCheck)
            {
                observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.NewLineChunk);
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = (long)index + ProcessingChunkSize;
            }

            observeCheckpoint?.Invoke(DocumentProcessingCheckpoint.NewLineCharacter);
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crLfCount++;
                    firstCrLf = Math.Min(firstCrLf, index);
                    index++;
                }
                else
                {
                    crCount++;
                    firstCr = Math.Min(firstCr, index);
                }
            }
            else if (text[index] == '\n')
            {
                lfCount++;
                firstLf = Math.Min(firstLf, index);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var hasCrLf = crLfCount > 0;
        var hasLf = lfCount > 0;
        var hasCr = crCount > 0;
        NewLineKind kind;
        if ((hasCrLf && (hasLf || hasCr)) || (hasLf && hasCr))
        {
            kind = NewLineKind.Mixed;
        }
        else if (hasCrLf)
        {
            kind = NewLineKind.CrLf;
        }
        else
        {
            kind = hasCr ? NewLineKind.Cr : NewLineKind.Lf;
        }

        var highestCount = Math.Max(crLfCount, Math.Max(lfCount, crCount));
        if (highestCount == 0)
        {
            return (kind, "\n");
        }

        var firstPreferred = int.MaxValue;
        var preferred = "\n";
        if (crLfCount == highestCount && firstCrLf < firstPreferred)
        {
            firstPreferred = firstCrLf;
            preferred = "\r\n";
        }

        if (lfCount == highestCount && firstLf < firstPreferred)
        {
            firstPreferred = firstLf;
            preferred = "\n";
        }

        if (crCount == highestCount && firstCr < firstPreferred)
        {
            preferred = "\r";
        }

        return (kind, preferred);
    }
}
