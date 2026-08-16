using MarkUpViewMini.Core.Documents;
using MarkUpViewMini.Core.Persistence;
using MarkUpViewMini.Infrastructure.Files;

namespace MarkUpViewMini.App.ViewModels;

public sealed record ReadOnlyDocumentSnapshot(
    string Path,
    string Text,
    DiskFileVersion Version)
{
    public bool IsReadOnly => true;
}

public sealed record DocumentComparisonViewModel(
    ReadOnlyDocumentSnapshot Mine,
    ReadOnlyDocumentSnapshot External);

public sealed class ConflictBarViewModel : ObservableObject
{
    private static readonly IReadOnlyList<ExternalChangeDecision> ConflictDecisions =
        Array.AsReadOnly(
        [
            ExternalChangeDecision.ReloadExternal,
            ExternalChangeDecision.KeepMine,
            ExternalChangeDecision.Compare,
        ]);
    private static readonly IReadOnlyList<ExternalChangeDecision> NoDecisions =
        Array.Empty<ExternalChangeDecision>();
    private bool isVisible;
    private string? message;
    private IReadOnlyList<ExternalChangeDecision> availableDecisions = NoDecisions;
    private DocumentComparisonViewModel? comparison;

    public bool IsVisible
    {
        get => isVisible;
        private set => SetProperty(ref isVisible, value);
    }

    public string? Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public IReadOnlyList<ExternalChangeDecision> AvailableDecisions
    {
        get => availableDecisions;
        private set
        {
            if (SetProperty(ref availableDecisions, value))
            {
                OnPropertyChanged(nameof(HasDecisions));
            }
        }
    }

    public bool HasDecisions => AvailableDecisions.Count > 0;

    public DocumentComparisonViewModel? Comparison
    {
        get => comparison;
        private set => SetProperty(ref comparison, value);
    }

    internal void ShowConflict()
    {
        Message = "파일이 외부에서 변경되었습니다. 사용할 버전을 선택하세요.";
        AvailableDecisions = ConflictDecisions;
        Comparison = null;
        IsVisible = true;
    }

    internal void ShowPathState(FileChangeNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Message = notice.DisplayMessage;
        AvailableDecisions = NoDecisions;
        Comparison = null;
        IsVisible = true;
    }

    internal void ShowComparison(DocumentComparisonViewModel value)
    {
        Comparison = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal void Clear()
    {
        IsVisible = false;
        Message = null;
        AvailableDecisions = NoDecisions;
        Comparison = null;
    }
}
