using MarkUpViewMini.Core.Navigation;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.Infrastructure.State;

public sealed record SearchOptionsV1(bool MatchCase, bool WholeWord, bool UseRegex);

public sealed record FindOptionsV1(bool MatchCase, bool WholeWord, bool UseRegex);

public sealed record SettingsV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public RootFollowMode RootMode { get; init; } = RootFollowMode.KeepRoot;

    public double SidebarWidth { get; init; } = 280;

    public double EditorSplitRatio { get; init; } = 0.5;

    public SearchMode SidebarSearchMode { get; init; } = SearchMode.FileName;

    public SearchOptionsV1 SidebarSearchOptions { get; init; } = new(false, false, false);

    public FindOptionsV1 FindOptions { get; init; } = new(false, false, false);

    public IReadOnlyList<RecentDocumentEntry> RecentDocuments { get; init; } = [];

    public static SettingsV1 CreateDefault() => new();

    public bool Equals(SettingsV1? other) =>
        other is not null &&
        SchemaVersion == other.SchemaVersion &&
        RootMode == other.RootMode &&
        SidebarWidth.Equals(other.SidebarWidth) &&
        EditorSplitRatio.Equals(other.EditorSplitRatio) &&
        SidebarSearchMode == other.SidebarSearchMode &&
        Equals(SidebarSearchOptions, other.SidebarSearchOptions) &&
        Equals(FindOptions, other.FindOptions) &&
        RecentDocuments.SequenceEqual(other.RecentDocuments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(RootMode);
        hash.Add(SidebarWidth);
        hash.Add(EditorSplitRatio);
        hash.Add(SidebarSearchMode);
        hash.Add(SidebarSearchOptions);
        hash.Add(FindOptions);
        foreach (var entry in RecentDocuments)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }
}
