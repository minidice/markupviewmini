using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Core.Mermaid;

public static class MermaidBlockUpdater
{
    public static MermaidApplyResult TryApply(
        DocumentBuffer buffer,
        MermaidBlockSnapshot snapshot,
        string replacement)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(snapshot);

        var current = buffer.CaptureSnapshot();
        if (snapshot.From < 0 ||
            snapshot.To < snapshot.From ||
            snapshot.To > current.Text.Length)
        {
            return MermaidApplyResult.InvalidRange;
        }

        if (snapshot.DocumentRevision != current.Revision)
        {
            return MermaidApplyResult.StaleRevision;
        }

        var source = current.Text[snapshot.From..snapshot.To];
        if (!string.Equals(source, snapshot.Source, StringComparison.Ordinal) ||
            !string.Equals(SourceHash(source), snapshot.SourceHash, StringComparison.Ordinal))
        {
            return MermaidApplyResult.RangeChanged;
        }

        if (!HasSamePhysicalContext(source, replacement, out var context) ||
            !IsBlockContent(replacement, context))
        {
            return MermaidApplyResult.InvalidRange;
        }

        try
        {
            buffer.Apply(new DocumentEdit(
                current.Revision,
                [new TextChange(snapshot.From, snapshot.To, replacement)]));
        }
        catch (StaleDocumentRevisionException)
        {
            return MermaidApplyResult.StaleRevision;
        }

        return MermaidApplyResult.Applied;
    }

    private static string SourceHash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static readonly Regex MermaidHeader = new(
        @"^(?:flowchart|graph)(?:\s+(?:TD|TB|LR|RL|BT))?\s*$",
        RegexOptions.CultureInvariant);

    private readonly record struct PhysicalContext(int Column, int[] QuoteColumns);

    private readonly record struct PhysicalLineContext(
        int ContentStart,
        int Indent,
        int[] QuoteColumns);

    private enum PhysicalNewline
    {
        None,
        Lf,
        CrLf,
        Cr,
    }

    private static bool HasSamePhysicalContext(
        string source,
        string? replacement,
        out PhysicalContext context)
    {
        context = default;
        if (replacement is null ||
            !TryReadStructuralContext(source, out context) ||
            !AllContentLinesMatch(source, context) ||
            !AllContentLinesMatch(replacement, context) ||
            !TryReadUniformNewline(source, out var sourceNewline) ||
            !TryReadUniformNewline(replacement, out var replacementNewline) ||
            (sourceNewline != PhysicalNewline.None && sourceNewline != replacementNewline) ||
            EndsWithNewline(source) != EndsWithNewline(replacement))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadStructuralContext(string source, out PhysicalContext context)
    {
        var foundContent = false;
        var foundHeader = false;
        var minimumColumn = int.MaxValue;
        int[]? quoteColumns = null;
        foreach (var line in PhysicalLines(source))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            foundContent = true;
            var lineContext = ReadLineContext(line);
            if (quoteColumns is null)
            {
                quoteColumns = lineContext.QuoteColumns;
            }
            else if (!quoteColumns.SequenceEqual(lineContext.QuoteColumns))
            {
                context = default;
                return false;
            }

            minimumColumn = Math.Min(minimumColumn, lineContext.Indent);

            for (var index = 0; index <= line.Length; index++)
            {
                if (MermaidHeader.IsMatch(line[index..]))
                {
                    foundHeader = true;
                    break;
                }

                if (index == line.Length || line[index] is not (' ' or '\t' or '>'))
                {
                    break;
                }
            }
        }

        context = foundContent
            ? new PhysicalContext(minimumColumn, quoteColumns!)
            : default;
        return foundContent && foundHeader;
    }

    private static PhysicalLineContext ReadLineContext(string line)
    {
        var column = 0;
        var index = 0;
        var lastQuoteIndex = -1;
        var quoteColumns = new List<int>();
        while (index < line.Length && line[index] is ' ' or '\t' or '>')
        {
            switch (line[index])
            {
                case ' ':
                    column++;
                    break;
                case '\t':
                    column = NextTabStop(column);
                    break;
                case '>':
                    quoteColumns.Add(column);
                    column++;
                    lastQuoteIndex = index;
                    break;
            }

            index++;
        }

        var indent = lastQuoteIndex < 0
            ? column
            : ReadQuoteIndent(line, lastQuoteIndex, quoteColumns[^1]);
        return new PhysicalLineContext(index, indent, quoteColumns.ToArray());
    }

    private static int ReadQuoteIndent(
        string line,
        int quoteIndex,
        int quoteColumn)
    {
        var index = quoteIndex + 1;
        var initialColumn = quoteColumn + 1;
        var adjustTab = false;

        // Markdown-it consumes at most one space or tab after the quote marker as
        // optional padding. Any remaining whitespace is logical content indentation.
        if (index < line.Length && line[index] == ' ')
        {
            index++;
            initialColumn++;
        }
        else if (index < line.Length && line[index] == '\t')
        {
            if (initialColumn % 4 == 3)
            {
                index++;
                initialColumn++;
            }
            else
            {
                adjustTab = true;
            }
        }

        var indent = 0;
        while (index < line.Length && line[index] is ' ' or '\t')
        {
            if (line[index] == ' ')
            {
                indent++;
            }
            else
            {
                var virtualColumn = initialColumn + indent + (adjustTab ? 1 : 0);
                indent += NextTabStop(virtualColumn) - virtualColumn;
            }

            index++;
        }

        return indent;
    }

    private static bool AllContentLinesMatch(string source, PhysicalContext context)
    {
        foreach (var line in PhysicalLines(source))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryFindContentStart(line, context, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFindContentStart(
        string line,
        PhysicalContext expected,
        out int contentStart)
    {
        var actual = ReadLineContext(line);
        if (actual.Indent >= expected.Column &&
            actual.QuoteColumns.SequenceEqual(expected.QuoteColumns))
        {
            contentStart = actual.ContentStart;
            return true;
        }

        contentStart = 0;
        return false;
    }

    private static IEnumerable<string> PhysicalLines(string source)
    {
        var lineStart = 0;
        while (lineStart <= source.Length)
        {
            var lineEnd = source.IndexOfAny(['\r', '\n'], lineStart);
            if (lineEnd < 0)
            {
                lineEnd = source.Length;
            }

            yield return source[lineStart..lineEnd];

            if (lineEnd == source.Length)
            {
                yield break;
            }

            lineStart = lineEnd + 1;
            if (source[lineEnd] == '\r' &&
                lineStart < source.Length &&
                source[lineStart] == '\n')
            {
                lineStart++;
            }
        }

    }

    private static bool TryReadUniformNewline(string source, out PhysicalNewline newline)
    {
        newline = PhysicalNewline.None;
        for (var index = 0; index < source.Length; index++)
        {
            PhysicalNewline current;
            if (source[index] == '\n')
            {
                current = PhysicalNewline.Lf;
            }
            else if (source[index] == '\r')
            {
                current = index + 1 < source.Length && source[index + 1] == '\n'
                    ? PhysicalNewline.CrLf
                    : PhysicalNewline.Cr;
                if (current == PhysicalNewline.CrLf)
                {
                    index++;
                }
            }
            else
            {
                continue;
            }

            if (newline != PhysicalNewline.None && newline != current)
            {
                return false;
            }

            newline = current;
        }

        return true;
    }

    private static bool EndsWithNewline(string source) =>
        source.EndsWith('\r') || source.EndsWith('\n');

    private static bool IsBlockContent(string? replacement, PhysicalContext context)
    {
        if (replacement is null)
        {
            return false;
        }

        var lineStart = 0;
        while (lineStart <= replacement.Length)
        {
            var lineEnd = replacement.IndexOfAny(['\r', '\n'], lineStart);
            if (lineEnd < 0)
            {
                lineEnd = replacement.Length;
            }

            var line = replacement[lineStart..lineEnd];
            if (!string.IsNullOrWhiteSpace(line) &&
                !IsSafeContentLine(replacement, lineStart, lineEnd, line, context))
            {
                return false;
            }

            if (lineEnd == replacement.Length)
            {
                return true;
            }

            lineStart = lineEnd + 1;
            if (replacement[lineEnd] == '\r' &&
                lineStart < replacement.Length &&
                replacement[lineStart] == '\n')
            {
                lineStart++;
            }
        }

        return true;
    }

    private static bool IsSafeContentLine(
        string replacement,
        int lineStart,
        int lineEnd,
        string line,
        PhysicalContext context)
    {
        if (!TryFindContentStart(line, context, out var relativeContentStart))
        {
            return false;
        }

        var delimiterStart = lineStart + relativeContentStart;
        var indent = ReadLineContext(line).Indent;
        var delimiterIndent = context.QuoteColumns.Length > 0
            ? indent
            : indent - context.Column;
        if (delimiterIndent > 3)
        {
            return true;
        }

        return delimiterStart + 3 > lineEnd ||
               replacement[delimiterStart] is not ('`' or '~') ||
               replacement[delimiterStart + 1] != replacement[delimiterStart] ||
               replacement[delimiterStart + 2] != replacement[delimiterStart];
    }

    private static int NextTabStop(int column) =>
        ((column / 4) + 1) * 4;
}
