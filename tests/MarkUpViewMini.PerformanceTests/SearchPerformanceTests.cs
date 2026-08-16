using System.Diagnostics;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.Infrastructure.Search;
using MarkUpViewMini.PerformanceTests.Support;

namespace MarkUpViewMini.PerformanceTests;

public sealed class SearchPerformanceTests
{
    private static readonly TimeSpan FirstResultThreshold = TimeSpan.FromMilliseconds(1_000);
    private static readonly TimeSpan CancellationThreshold = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Cancellation_measurement_includes_synchronous_token_callbacks()
    {
        // Break caught: the cancellation stopwatch moves after Cancel() and omits synchronous token propagation.
        using var fixture = PerformanceFixture.CreateSearchCorpus();
        IDocumentSearchService service = new DocumentSearchService();
        using var cancellation = new CancellationTokenSource();
        await using var events = service.SearchAsync(
                fixture.CreateBodyQuery(),
                cancellation.Token)
            .GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<SearchMatch>(events.Current);
        using var synchronousCallback = cancellation.Token.Register(() => Thread.Sleep(50));

        var measurement = await SearchCancellationProbe.CancelToLastYieldAsync(
            cancellation,
            events);

        Assert.True(
            measurement.LastYieldElapsed >= TimeSpan.FromMilliseconds(40),
            $"Cancellation timing omitted the synchronous callback: {measurement.LastYieldElapsed.TotalMilliseconds:F3} ms.");
        Assert.True(measurement.Summary.WasCancelled);
    }

    [PerformanceFact]
    public async Task Physical_thousand_file_body_search_yields_its_first_known_match_under_one_second()
    {
        // Break caught: physical enumeration, decoding, or body matching delays the first streamed result past the release budget.
        using var fixture = PerformanceFixture.CreateSearchCorpus();
        IDocumentSearchService service = new DocumentSearchService();
        var stopwatch = Stopwatch.StartNew();
        await using var events = service.SearchAsync(
                fixture.CreateBodyQuery(),
                CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        stopwatch.Stop();

        var match = Assert.IsType<SearchMatch>(events.Current);
        Assert.Equal("document-0000.md", Path.GetFileName(match.Path));
        Assert.Contains(PerformanceFixture.SearchNeedle, match.Preview, StringComparison.Ordinal);
        var effectiveThreshold = PerformanceThreshold.Effective(FirstResultThreshold);
        Assert.True(
            stopwatch.Elapsed < effectiveThreshold,
            $"First result took {stopwatch.Elapsed.TotalMilliseconds:F3} ms; threshold is {effectiveThreshold.TotalMilliseconds:F0} ms.");
        PerformanceResultWriter.Write(
            "searchFirstResult",
            fixture.SearchFixtureSha256,
            stopwatch.Elapsed,
            FirstResultThreshold);
    }

    [PerformanceFact]
    public async Task Physical_search_cancellation_reaches_its_last_yield_under_half_a_second()
    {
        // Break caught: cancellation no longer stops physical search promptly or the terminal cancelled summary is delayed or omitted.
        using var fixture = PerformanceFixture.CreateSearchCorpus();
        IDocumentSearchService service = new DocumentSearchService();
        using var cancellation = new CancellationTokenSource();
        await using var events = service.SearchAsync(
                fixture.CreateBodyQuery(),
                cancellation.Token)
            .GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.IsType<SearchMatch>(events.Current);

        var measurement = await SearchCancellationProbe.CancelToLastYieldAsync(
            cancellation,
            events);
        var effectiveThreshold = PerformanceThreshold.Effective(CancellationThreshold);
        Assert.True(
            measurement.LastYieldElapsed < effectiveThreshold,
            $"Cancellation-to-last-yield took {measurement.LastYieldElapsed.TotalMilliseconds:F3} ms; threshold is {effectiveThreshold.TotalMilliseconds:F0} ms.");
        PerformanceResultWriter.Write(
            "searchCancellation",
            fixture.SearchFixtureSha256,
            measurement.LastYieldElapsed,
            CancellationThreshold);
    }
}

internal static class SearchCancellationProbe
{
    public static async Task<SearchCancellationMeasurement> CancelToLastYieldAsync(
        CancellationTokenSource cancellation,
        IAsyncEnumerator<SearchEvent> events)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        SearchEvent? lastYield = null;
        var lastYieldElapsed = TimeSpan.Zero;
        while (await events.MoveNextAsync())
        {
            lastYield = events.Current;
            lastYieldElapsed = stopwatch.Elapsed;
        }

        stopwatch.Stop();
        return new SearchCancellationMeasurement(
            lastYieldElapsed,
            lastYield as SearchSummary ??
                throw new InvalidOperationException("Cancellation did not yield a terminal search summary."));
    }
}

internal sealed record SearchCancellationMeasurement(
    TimeSpan LastYieldElapsed,
    SearchSummary Summary);
