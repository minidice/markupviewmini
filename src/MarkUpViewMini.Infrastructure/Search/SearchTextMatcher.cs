using System.Text;
using System.Text.RegularExpressions;
using MarkUpViewMini.Core.Search;

namespace MarkUpViewMini.Infrastructure.Search;

internal sealed class SearchTextMatcher
{
    private const int MaxPreviewLength = 160;
    private const int CancellationCheckInterval = 4096;
    private readonly Regex regex;
    private readonly Func<string, CancellationToken, IEnumerable<SearchTextSpan>> findMatches;

    public SearchTextMatcher(
        SearchQuery query,
        Func<string, IEnumerable<SearchTextSpan>>? findMatches = null)
        : this(
            query,
            findMatches is null
                ? null
                : (text, _) => findMatches(text))
    {
    }

    internal SearchTextMatcher(
        SearchQuery query,
        Func<string, CancellationToken, IEnumerable<SearchTextSpan>>? findMatches)
    {
        var pattern = query.UseRegex ? query.Text : Regex.Escape(query.Text);
        if (query.WholeWord)
        {
            pattern = $"\\b(?:{pattern})\\b";
        }

        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (!query.MatchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            regex = new Regex(pattern, options);
            this.findMatches = findMatches ?? FindRegexMatches;
        }
        catch (ArgumentException exception)
        {
            throw new SearchQueryException("The search query contains an invalid regular expression.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new SearchQueryException("The search query contains an invalid regular expression.", exception);
        }
    }

    public Match Match(string text)
    {
        return regex.Match(text);
    }

    public IEnumerable<SearchTextMatch> MatchLines(
        string text,
        CancellationToken cancellationToken)
    {
        var lineNumber = 1;
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n'))
            {
                if ((lineEnd - lineStart) % CancellationCheckInterval == 0 &&
                    cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                lineEnd++;
            }

            var line = text[lineStart..lineEnd];
            foreach (var match in MatchLine(line, lineNumber, cancellationToken))
            {
                yield return match;
            }

            if (cancellationToken.IsCancellationRequested || lineEnd == text.Length)
            {
                yield break;
            }

            lineStart = lineEnd + 1;
            if (text[lineEnd] == '\r' && lineStart < text.Length && text[lineStart] == '\n')
            {
                lineStart++;
            }

            lineNumber++;
        }
    }

    private IEnumerable<SearchTextMatch> MatchLine(
        string line,
        int lineNumber,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(line, cancellationToken, out var normalizedLine, out var displayPositions))
        {
            yield break;
        }

        using var matches = findMatches(line, cancellationToken).GetEnumerator();
        while (!cancellationToken.IsCancellationRequested && matches.MoveNext())
        {
            var match = matches.Current;
            if (TryMapMatch(match, normalizedLine.Length, displayPositions, out var start, out var length))
            {
                yield return CreatePreview(lineNumber, normalizedLine, start, length);
            }
        }
    }

    private IEnumerable<SearchTextSpan> FindRegexMatches(
        string text,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        var match = regex.Match(text);
        while (match.Success)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            yield return new SearchTextSpan(match.Index, match.Length);
            match = match.NextMatch();
        }
    }

    private static bool TryNormalize(
        string line,
        CancellationToken cancellationToken,
        out string normalized,
        out int[] displayPositions)
    {
        var builder = new StringBuilder(line.Length);
        displayPositions = new int[line.Length];
        Array.Fill(displayPositions, -1);

        for (var characterIndex = 0; characterIndex < line.Length; characterIndex++)
        {
            if (characterIndex % CancellationCheckInterval == 0 && cancellationToken.IsCancellationRequested)
            {
                normalized = string.Empty;
                return false;
            }

            if (char.IsWhiteSpace(line[characterIndex]))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                if (builder.Length > 0)
                {
                    displayPositions[characterIndex] = builder.Length - 1;
                }
            }
            else
            {
                displayPositions[characterIndex] = builder.Length;
                builder.Append(line[characterIndex]);
            }
        }

        if (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        normalized = builder.ToString();
        return true;
    }

    private static bool TryMapMatch(
        SearchTextSpan match,
        int normalizedLength,
        IReadOnlyList<int> displayPositions,
        out int start,
        out int length)
    {
        if (match.Length == 0)
        {
            start = MapBoundary(match.Index, normalizedLength, displayPositions);
            length = 0;
            return true;
        }

        start = -1;
        var end = -1;
        for (var index = match.Index; index < match.Index + match.Length; index++)
        {
            var position = displayPositions[index];
            if (position < 0 || position >= normalizedLength)
            {
                continue;
            }

            if (start < 0)
            {
                start = position;
            }

            end = position;
        }

        if (start < 0)
        {
            length = 0;
            return false;
        }

        length = end - start + 1;
        return true;
    }

    private static int MapBoundary(
        int rawIndex,
        int normalizedLength,
        IReadOnlyList<int> displayPositions)
    {
        for (var index = rawIndex; index < displayPositions.Count; index++)
        {
            if (displayPositions[index] >= 0)
            {
                return Math.Min(displayPositions[index], normalizedLength);
            }
        }

        return normalizedLength;
    }

    private static SearchTextMatch CreatePreview(
        int lineNumber,
        string normalizedLine,
        int matchStart,
        int matchLength)
    {
        if (normalizedLine.Length <= MaxPreviewLength)
        {
            return new SearchTextMatch(lineNumber, normalizedLine, matchStart, matchLength);
        }

        if (matchLength >= MaxPreviewLength)
        {
            return new SearchTextMatch(
                lineNumber,
                normalizedLine.Substring(matchStart, MaxPreviewLength),
                0,
                MaxPreviewLength);
        }

        var contextBefore = (MaxPreviewLength - matchLength) / 2;
        var previewStart = Math.Clamp(
            matchStart - contextBefore,
            0,
            normalizedLine.Length - MaxPreviewLength);
        return new SearchTextMatch(
            lineNumber,
            normalizedLine.Substring(previewStart, MaxPreviewLength),
            matchStart - previewStart,
            matchLength);
    }
}

internal readonly record struct SearchTextMatch(
    int LineNumber,
    string Preview,
    int MatchStart,
    int MatchLength);

internal readonly record struct SearchTextSpan(int Index, int Length);
