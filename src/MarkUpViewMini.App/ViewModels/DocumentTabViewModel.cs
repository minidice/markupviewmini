using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Core.Workspace;
using MarkUpViewMini.App.Web;
using MarkUpViewMini.Infrastructure.State;

namespace MarkUpViewMini.App.ViewModels;

public sealed class DocumentTabViewModel : ObservableObject
{
    private string path;
    private DocumentBuffer? buffer;
    private IDocumentFormatProvider? formatProvider;
    private DocumentMode mode = DocumentMode.Read;
    private int? targetLine;
    private string? targetAnchor;
    private DocumentOpenErrorViewModel? error;
    private bool isLoading = true;
    private DocumentUiHints uiHints = new(0, 0, 0);

    internal DocumentTabViewModel(DocumentTarget target)
        : this(target, Guid.NewGuid())
    {
    }

    internal DocumentTabViewModel(DocumentTarget target, Guid id)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A session tab ID cannot be empty.", nameof(id));
        }

        Id = id;
        path = target.Path;
        targetLine = target.Line;
        targetAnchor = target.Anchor;
    }

    public Guid Id { get; }

    public NavigationHistory NavigationHistory { get; } = new();

    public DocumentBuffer? Buffer => isLoading ? null : buffer;

    public IDocumentFormatProvider? FormatProvider => isLoading ? null : formatProvider;

    public string Path
    {
        get => path;
        private set
        {
            if (SetProperty(ref path, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string Text => Buffer?.Text ?? string.Empty;

    public DocumentMode Mode
    {
        get => mode;
        private set => SetProperty(ref mode, value);
    }

    public bool IsDirty => Buffer?.IsDirty ?? false;

    public bool CanEdit =>
        FormatProvider?.Descriptor.Capabilities.HasFlag(DocumentCapabilities.Edit) == true;

    public long Revision => buffer?.Revision ?? 0;

    public int? TargetLine
    {
        get => targetLine;
        private set => SetProperty(ref targetLine, value);
    }

    public string? TargetAnchor
    {
        get => targetAnchor;
        private set => SetProperty(ref targetAnchor, value);
    }

    public EncodingDescriptor? Encoding => Buffer?.Encoding;

    public NewLineKind NewLine => buffer?.NewLine ?? default;

    public string PreferredNewLine => buffer?.PreferredNewLine ?? "\n";

    public DiskFileVersion? DiskVersion => Buffer?.BaselineVersion;

    public DocumentUiHints UiHints => uiHints;

    public DocumentOpenErrorViewModel? Error
    {
        get => error;
        internal set => SetProperty(ref error, value);
    }

    public string DisplayTitle => $"{System.IO.Path.GetFileName(Path)}{(IsDirty ? " *" : string.Empty)}";

    internal void PrepareForLoad(DocumentTarget target)
    {
        var previous = CaptureProjection();
        var hadBuffer = Buffer is not null;
        Path = target.Path;
        TargetLine = target.Line;
        TargetAnchor = target.Anchor;
        isLoading = true;
        if (hadBuffer)
        {
            OnPropertyChanged(nameof(Buffer));
        }

        if (!string.Equals(previous.Text, Text, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Text));
        }

        Mode = DocumentMode.Read;
        ApplyUiHints(new DocumentUiHints(0, 0, 0));
        if (previous.IsDirty != IsDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DisplayTitle));
        }

        if (!Equals(previous.Encoding, Encoding))
        {
            OnPropertyChanged(nameof(Encoding));
        }

        if (!Equals(previous.DiskVersion, DiskVersion))
        {
            OnPropertyChanged(nameof(DiskVersion));
        }

        Error = null;
    }

    internal void ApplyLoaded(
        LoadedDocument document,
        IDocumentFormatProvider? loadedFormatProvider = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var previous = CaptureProjection();
        if (buffer is null)
        {
            buffer = DocumentBuffer.Create(
                Id,
                Path,
                document.Text,
                document.Encoding,
                document.NewLine,
                document.PreferredNewLine,
                document.Version);
        }

        buffer.ReplaceFromDisk(
            Path,
            document.Text,
            document.Encoding,
            document.NewLine,
            document.PreferredNewLine,
            document.Version);
        formatProvider = loadedFormatProvider ?? formatProvider;
        isLoading = false;
        OnPropertyChanged(nameof(Buffer));
        OnPropertyChanged(nameof(FormatProvider));
        NotifyBufferProjectionChanges(previous, includeRevision: true);
        Mode = DocumentMode.Read;
        Error = null;
    }

    internal void ApplyExternalLoaded(LoadedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var current = Buffer ?? throw new InvalidOperationException("The document is not loaded.");
        var previous = CaptureProjection();
        current.ReplaceFromDisk(
            Path,
            document.Text,
            document.Encoding,
            document.NewLine,
            document.PreferredNewLine,
            document.Version);
        NotifyBufferProjectionChanges(previous, includeRevision: true);
        Error = null;
    }

    internal void ApplyRecovered(
        DocumentBuffer recovered,
        IDocumentFormatProvider recoveredFormatProvider)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        ArgumentNullException.ThrowIfNull(recoveredFormatProvider);
        if (recovered.TabId != Id)
        {
            throw new ArgumentException("The recovered buffer belongs to another tab.", nameof(recovered));
        }

        var previous = CaptureProjection();
        Path = recovered.Path;
        buffer = recovered.Clone();
        formatProvider = recoveredFormatProvider;
        isLoading = false;
        OnPropertyChanged(nameof(Buffer));
        OnPropertyChanged(nameof(FormatProvider));
        NotifyBufferProjectionChanges(previous, includeRevision: true);
        Mode = CanEdit ? DocumentMode.Edit : DocumentMode.Read;
        Error = null;
    }

    internal void ApplyNavigationTarget(int? line, string? anchor)
    {
        TargetLine = line;
        TargetAnchor = anchor;
    }

    internal long ApplyEdit(DocumentEdit edit)
    {
        var current = Buffer ?? throw new InvalidOperationException("The document is not loaded.");
        var previous = CaptureProjection();
        var revision = current.Apply(edit);
        NotifyBufferProjectionChanges(previous, includeRevision: true);
        return revision;
    }

    internal void CompleteSave(
        SaveResult.Saved saved,
        SaveDecision decision,
        IDocumentFormatProvider? savedFormatProvider = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(decision);
        var current = Buffer ?? throw new InvalidOperationException("The document is not loaded.");
        current.CompleteSave(
            saved.SavedRevision,
            saved.Version,
            decision is SaveDecision.SaveAs saveAs ? saveAs.TargetPath : null,
            decision is SaveDecision.SaveAs saveAsEncoding ? saveAsEncoding.Encoding : null);

        if (decision is SaveDecision.SaveAs)
        {
            Path = current.Path;
            formatProvider = savedFormatProvider ??
                throw new ArgumentNullException(nameof(savedFormatProvider));
            OnPropertyChanged(nameof(FormatProvider));
        }

        OnPropertyChanged(nameof(Encoding));
        OnPropertyChanged(nameof(DiskVersion));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    internal void SetMode(DocumentMode nextMode)
    {
        if (nextMode == DocumentMode.Edit && !CanEdit)
        {
            return;
        }

        Mode = nextMode;
    }

    internal void ApplyUiHints(DocumentUiHints hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        if (hints.SelectionAnchor < 0 ||
            hints.SelectionHead < 0 ||
            !double.IsFinite(hints.ScrollTop) ||
            hints.ScrollTop < 0 ||
            hints.ScrollTop > 1_000_000_000 ||
            !double.IsFinite(hints.SplitRatio) ||
            hints.SplitRatio is < 0.1 or > 0.9)
        {
            throw new ArgumentOutOfRangeException(nameof(hints));
        }

        if (!Equals(uiHints, hints))
        {
            uiHints = hints;
            OnPropertyChanged(nameof(UiHints));
        }
    }

    internal void ApplyEditorPreferences(double splitRatio, FindOptionsV1 findOptions) =>
        ApplyUiHints(UiHints with
        {
            SplitRatio = splitRatio,
            FindMatchCase = findOptions.MatchCase,
            FindWholeWord = findOptions.WholeWord,
            FindUseRegex = findOptions.UseRegex,
        });

    internal NavigationSnapshot CaptureNavigationSnapshot() =>
        new(
            Path,
            buffer?.Clone(),
            formatProvider,
            isLoading,
            Mode,
            UiHints,
            TargetLine,
            TargetAnchor,
            Error);

    internal void RestoreNavigationSnapshot(NavigationSnapshot snapshot, Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (!isCurrent())
        {
            return;
        }

        var previous = CaptureProjection();
        Path = snapshot.Path;
        if (!isCurrent())
        {
            return;
        }

        buffer = snapshot.Buffer?.Clone();
        formatProvider = snapshot.FormatProvider;
        isLoading = snapshot.IsLoading;
        OnPropertyChanged(nameof(Buffer));
        OnPropertyChanged(nameof(FormatProvider));
        if (!isCurrent())
        {
            return;
        }

        if (!string.Equals(previous.Text, Text, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Text));
        }

        if (!isCurrent())
        {
            return;
        }

        Mode = snapshot.Mode;
        if (!isCurrent())
        {
            return;
        }

        ApplyUiHints(snapshot.UiHints);
        if (!isCurrent())
        {
            return;
        }

        if (previous.IsDirty != IsDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DisplayTitle));
        }

        if (!isCurrent())
        {
            return;
        }

        if (previous.Revision != Revision)
        {
            OnPropertyChanged(nameof(Revision));
        }

        if (!isCurrent())
        {
            return;
        }

        TargetLine = snapshot.TargetLine;
        if (!isCurrent())
        {
            return;
        }

        TargetAnchor = snapshot.TargetAnchor;
        if (!isCurrent())
        {
            return;
        }

        if (!Equals(previous.Encoding, Encoding))
        {
            OnPropertyChanged(nameof(Encoding));
        }

        if (!isCurrent())
        {
            return;
        }

        if (previous.NewLine != NewLine)
        {
            OnPropertyChanged(nameof(NewLine));
        }

        if (!isCurrent())
        {
            return;
        }

        if (!string.Equals(previous.PreferredNewLine, PreferredNewLine, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(PreferredNewLine));
        }

        if (!isCurrent())
        {
            return;
        }

        if (!Equals(previous.DiskVersion, DiskVersion))
        {
            OnPropertyChanged(nameof(DiskVersion));
        }

        if (!isCurrent())
        {
            return;
        }

        Error = snapshot.Error;
    }

    private BufferProjection CaptureProjection() =>
        new(
            Text,
            IsDirty,
            Revision,
            Encoding,
            NewLine,
            PreferredNewLine,
            DiskVersion);

    private void NotifyBufferProjectionChanges(BufferProjection previous, bool includeRevision)
    {
        if (!string.Equals(previous.Text, Text, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Text));
        }

        if (!Equals(previous.Encoding, Encoding))
        {
            OnPropertyChanged(nameof(Encoding));
        }

        if (previous.NewLine != NewLine)
        {
            OnPropertyChanged(nameof(NewLine));
        }

        if (!string.Equals(previous.PreferredNewLine, PreferredNewLine, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(PreferredNewLine));
        }

        if (!Equals(previous.DiskVersion, DiskVersion))
        {
            OnPropertyChanged(nameof(DiskVersion));
        }

        if (previous.IsDirty != IsDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DisplayTitle));
        }

        if (includeRevision && previous.Revision != Revision)
        {
            OnPropertyChanged(nameof(Revision));
        }
    }

    internal sealed record NavigationSnapshot(
        string Path,
        DocumentBuffer? Buffer,
        IDocumentFormatProvider? FormatProvider,
        bool IsLoading,
        DocumentMode Mode,
        DocumentUiHints UiHints,
        int? TargetLine,
        string? TargetAnchor,
        DocumentOpenErrorViewModel? Error);

    private sealed record BufferProjection(
        string Text,
        bool IsDirty,
        long Revision,
        EncodingDescriptor? Encoding,
        NewLineKind NewLine,
        string PreferredNewLine,
        DiskFileVersion? DiskVersion);
}
