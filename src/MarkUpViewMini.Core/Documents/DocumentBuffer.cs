using System.Text;

namespace MarkUpViewMini.Core.Documents;

public sealed class DocumentBuffer
{
    private readonly object syncRoot = new();

    private DocumentBuffer(
        Guid tabId,
        string path,
        string text,
        EncodingDescriptor encoding,
        NewLineKind newLine,
        string preferredNewLine,
        DiskFileVersion baselineVersion)
    {
        TabId = tabId;
        Path = path;
        Text = text;
        Encoding = encoding;
        NewLine = newLine;
        PreferredNewLine = preferredNewLine;
        BaselineVersion = baselineVersion;
    }

    public Guid TabId { get; }

    public string Path { get; private set; }

    public string Text { get; private set; }

    public long Revision { get; private set; }

    public bool IsDirty { get; private set; }

    public EncodingDescriptor Encoding { get; private set; }

    public NewLineKind NewLine { get; private set; }

    public string PreferredNewLine { get; private set; }

    public DiskFileVersion BaselineVersion { get; private set; }

    public static DocumentBuffer Create(
        Guid tabId,
        string path,
        string text,
        EncodingDescriptor encoding,
        NewLineKind newLine,
        string preferredNewLine,
        DiskFileVersion baselineVersion)
    {
        return new DocumentBuffer(
            tabId,
            NormalizePath(path),
            text ?? throw new ArgumentNullException(nameof(text)),
            encoding ?? throw new ArgumentNullException(nameof(encoding)),
            newLine,
            ValidatePreferredNewLine(preferredNewLine),
            baselineVersion ?? throw new ArgumentNullException(nameof(baselineVersion)));
    }

    public static DocumentBuffer Restore(
        Guid tabId,
        string path,
        string text,
        EncodingDescriptor encoding,
        NewLineKind newLine,
        string preferredNewLine,
        DiskFileVersion baselineVersion,
        long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        return new DocumentBuffer(
            tabId,
            NormalizePath(path),
            text ?? throw new ArgumentNullException(nameof(text)),
            encoding ?? throw new ArgumentNullException(nameof(encoding)),
            newLine,
            ValidatePreferredNewLine(preferredNewLine),
            baselineVersion ?? throw new ArgumentNullException(nameof(baselineVersion)))
        {
            Revision = revision,
            IsDirty = true,
        };
    }

    public DocumentBuffer Clone()
    {
        lock (syncRoot)
        {
            return new DocumentBuffer(
                TabId,
                Path,
                Text,
                Encoding,
                NewLine,
                PreferredNewLine,
                BaselineVersion)
            {
                Revision = Revision,
                IsDirty = IsDirty,
            };
        }
    }

    public DocumentBufferSnapshot CaptureSnapshot()
    {
        lock (syncRoot)
        {
            return new DocumentBufferSnapshot(
                TabId,
                Path,
                Text,
                Revision,
                IsDirty,
                Encoding,
                NewLine,
                PreferredNewLine,
                BaselineVersion);
        }
    }

    public long Apply(DocumentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (syncRoot)
        {
            if (edit.ExpectedRevision != Revision)
            {
                throw new StaleDocumentRevisionException(edit.ExpectedRevision, Revision);
            }

            var changes = edit.Changes ??
                throw new ArgumentException("Changes cannot be null.", nameof(edit));
            if (changes.Count == 0)
            {
                throw new ArgumentException("At least one change is required.", nameof(edit));
            }

            ValidateChanges(changes);

            var updatedText = new StringBuilder(Text);
            for (var index = changes.Count - 1; index >= 0; index--)
            {
                var change = changes[index];
                updatedText.Remove(change.From, change.To - change.From);
                updatedText.Insert(change.From, change.InsertedText);
            }

            var nextRevision = checked(Revision + 1);
            Text = updatedText.ToString();
            Revision = nextRevision;
            IsDirty = true;
            return Revision;
        }
    }

    public void MarkSaved(long expectedRevision, DiskFileVersion newVersion)
    {
        lock (syncRoot)
        {
            if (expectedRevision != Revision)
            {
                throw new StaleDocumentRevisionException(expectedRevision, Revision);
            }

            ArgumentNullException.ThrowIfNull(newVersion);
            BaselineVersion = newVersion;
            IsDirty = false;
        }
    }

    public void CompleteSave(
        long savedRevision,
        DiskFileVersion newVersion,
        string? savedPath = null,
        EncodingDescriptor? savedEncoding = null)
    {
        lock (syncRoot)
        {
            if (savedRevision < 0 || savedRevision > Revision)
            {
                throw new ArgumentOutOfRangeException(nameof(savedRevision));
            }

            ArgumentNullException.ThrowIfNull(newVersion);
            if (savedPath is not null)
            {
                Path = NormalizePath(savedPath);
            }

            if (savedEncoding is not null)
            {
                Encoding = savedEncoding;
            }

            BaselineVersion = newVersion;
            IsDirty = Revision != savedRevision;
        }
    }

    public void ReplaceFromDisk(
        string path,
        string text,
        EncodingDescriptor encoding,
        NewLineKind newLine,
        string preferredNewLine,
        DiskFileVersion version)
    {
        lock (syncRoot)
        {
            var normalizedPath = NormalizePath(path);
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(encoding);
            var validatedPreferredNewLine = ValidatePreferredNewLine(preferredNewLine);
            ArgumentNullException.ThrowIfNull(version);
            var nextRevision = checked(Revision + 1);

            Path = normalizedPath;
            Text = text;
            Encoding = encoding;
            NewLine = newLine;
            PreferredNewLine = validatedPreferredNewLine;
            BaselineVersion = version;
            Revision = nextRevision;
            IsDirty = false;
        }
    }

    private void ValidateChanges(IReadOnlyList<TextChange> changes)
    {
        var previousFrom = -1;
        var previousTo = 0;
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index] ??
                throw new ArgumentException("Changes cannot contain null values.", nameof(changes));
            if (change.From < 0 || change.To < change.From || change.To > Text.Length)
            {
                throw new ArgumentException("A change range is outside the current text.", nameof(changes));
            }

            if (index > 0 && (change.From < previousFrom || change.From < previousTo))
            {
                throw new ArgumentException("Changes must be ascending and non-overlapping.", nameof(changes));
            }

            if (change.InsertedText is null)
            {
                throw new ArgumentException("Inserted text cannot be null.", nameof(changes));
            }

            previousFrom = change.From;
            previousTo = change.To;
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return System.IO.Path.GetFullPath(path);
    }

    private static string ValidatePreferredNewLine(string preferredNewLine)
    {
        ArgumentNullException.ThrowIfNull(preferredNewLine);
        if (preferredNewLine is not ("\r\n" or "\n" or "\r"))
        {
            throw new ArgumentException(
                "The preferred newline must be CRLF, LF, or CR.",
                nameof(preferredNewLine));
        }

        return preferredNewLine;
    }
}
