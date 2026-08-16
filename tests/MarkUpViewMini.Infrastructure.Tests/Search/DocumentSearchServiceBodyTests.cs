using System.Text;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Search;

namespace MarkUpViewMini.Infrastructure.Tests.Search;

public sealed class DocumentSearchServiceBodyTests : IDisposable
{
    private readonly string _root;
    private readonly DocumentSearchService _service = new();

    public DocumentSearchServiceBodyTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            nameof(DocumentSearchServiceBodyTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SearchAsync_returns_one_match_per_literal_occurrence_with_one_based_mixed_line_numbers()
    {
        // Break caught: matching once per line or splitting on only one newline convention loses matches or reports wrong line numbers.
        var path = Path.Combine(_root, "mixed.md");
        await File.WriteAllTextAsync(
            path,
            "first needle and needle\r\nsecond line\nthird needle\rfourth needle");

        var events = await CollectAsync(CreateBodyQuery("needle"));

        var matches = events.OfType<SearchMatch>().ToList();
        Assert.Equal([1, 1, 3, 4], matches.Select(match => match.LineNumber));
        Assert.Equal([6, 17, 6, 7], matches.Select(match => match.MatchStart));
        Assert.All(matches, match => Assert.Equal(6, match.MatchLength));
        Assert.Equal(
            ["first needle and needle", "first needle and needle", "third needle", "fourth needle"],
            matches.Select(match => match.Preview));
        Assert.Single(events.OfType<SearchSummary>());
    }

    [Fact]
    public async Task SearchAsync_respects_case_sensitive_body_matching()
    {
        // Break caught: adding IgnoreCase in body mode returns a lowercase false-positive for a case-sensitive query.
        await File.WriteAllTextAsync(Path.Combine(_root, "case.md"), "Needle needle");

        var events = await CollectAsync(CreateBodyQuery("Needle", matchCase: true));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal(0, match.MatchStart);
        Assert.Equal(6, match.MatchLength);
    }

    [Fact]
    public async Task SearchAsync_respects_whole_word_body_matching()
    {
        // Break caught: substring matching in whole-word mode returns the embedded occurrence in "scatter".
        await File.WriteAllTextAsync(Path.Combine(_root, "words.md"), "cat scatter cat");

        var events = await CollectAsync(CreateBodyQuery("cat", wholeWord: true));

        Assert.Equal([0, 12], events.OfType<SearchMatch>().Select(match => match.MatchStart));
    }

    [Fact]
    public async Task SearchAsync_applies_regular_expressions_to_document_bodies()
    {
        // Break caught: escaping a regex body query turns its operators into literals and produces no matches.
        await File.WriteAllTextAsync(Path.Combine(_root, "items.markdown"), "item-12 and item-345");

        var events = await CollectAsync(CreateBodyQuery(@"item-[0-9]+", useRegex: true));

        var matches = events.OfType<SearchMatch>().ToList();
        Assert.Equal([0, 12], matches.Select(match => match.MatchStart));
        Assert.Equal([7, 8], matches.Select(match => match.MatchLength));
    }

    [Fact]
    public async Task SearchAsync_normalizes_whitespace_and_caps_body_previews_at_160_characters()
    {
        // Break caught: returning the raw long line leaks tabs/repeated whitespace and violates the bounded preview contract.
        await File.WriteAllTextAsync(
            Path.Combine(_root, "preview.md"),
            $"\tneedle\t  {new string('x', 200)}");

        var events = await CollectAsync(CreateBodyQuery("needle"));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal($"needle {new string('x', 153)}", match.Preview);
        Assert.Equal(160, match.Preview.Length);
        Assert.Equal(0, match.MatchStart);
        Assert.Equal(6, match.MatchLength);
    }

    [Fact]
    public async Task SearchAsync_maps_each_match_in_collapsed_whitespace_to_the_displayed_space()
    {
        // Break caught: boundary-only normalization maps the second raw space to a zero-length range pointing at the following character.
        await File.WriteAllTextAsync(Path.Combine(_root, "collapsed.md"), "x  y");

        var events = await CollectAsync(CreateBodyQuery(" "));

        var matches = events.OfType<SearchMatch>().ToList();
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.Equal("x y", match.Preview));
        Assert.All(matches, match => Assert.Equal(1, match.MatchStart));
        Assert.All(matches, match => Assert.Equal(1, match.MatchLength));
    }

    [Fact]
    public async Task SearchAsync_omits_a_whitespace_match_trimmed_out_of_the_preview()
    {
        // Break caught: emitting a trailing-whitespace match after trim produces an unhighlightable zero-length range at the preview end.
        await File.WriteAllTextAsync(Path.Combine(_root, "trailing.md"), "start  middle   ");

        var events = await CollectAsync(CreateBodyQuery(@"\s+", useRegex: true));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal("start middle", match.Preview);
        Assert.Equal(5, match.MatchStart);
        Assert.Equal(1, match.MatchLength);
        Assert.Single(events.OfType<SearchSummary>());
    }

    [Fact]
    public async Task SearchAsync_counts_strict_utf8_decoding_failures_and_continues_to_other_files()
    {
        // Break caught: permissive decoding creates mojibake, while treating a per-file decoder failure as fatal prevents later results.
        await File.WriteAllBytesAsync(Path.Combine(_root, "invalid.md"), [0xC3, 0x28]);
        var readablePath = Path.Combine(_root, "readable.md");
        await File.WriteAllTextAsync(readablePath, "needle");

        var events = await CollectAsync(CreateBodyQuery("needle"));

        var match = Assert.IsType<SearchMatch>(Assert.Single(events.OfType<SearchMatch>()));
        Assert.Equal(readablePath, match.Path);
        var summary = Assert.IsType<SearchSummary>(Assert.Single(events.OfType<SearchSummary>()));
        Assert.Equal(2, summary.FilesScanned);
        Assert.Equal(1, summary.UnreadableFiles);
        Assert.False(summary.WasCancelled);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private async Task<List<SearchEvent>> CollectAsync(SearchQuery query)
    {
        var events = new List<SearchEvent>();
        await foreach (var searchEvent in _service.SearchAsync(query, CancellationToken.None))
        {
            events.Add(searchEvent);
        }

        return events;
    }

    private SearchQuery CreateBodyQuery(
        string text,
        bool matchCase = false,
        bool wholeWord = false,
        bool useRegex = false)
    {
        return new SearchQuery(
            Guid.NewGuid(), _root, text, SearchMode.Body,
            matchCase, wholeWord, useRegex, MarkdownExtensions, 10 * 1024 * 1024);
    }

    private static IReadOnlySet<string> MarkdownExtensions { get; } =
        new HashSet<string>([".md", ".markdown"], StringComparer.OrdinalIgnoreCase);
}
