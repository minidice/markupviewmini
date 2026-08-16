using System.Text.RegularExpressions;

namespace MarkUpViewMini.Core.Navigation;

public static partial class DocumentTargetParser
{
    [GeneratedRegex(@":(?<line>[1-9][0-9]*)$")]
    private static partial Regex TerminalLineSuffix();

    [GeneratedRegex(@":0+$")]
    private static partial Regex ZeroLineSuffix();

    public static DocumentTarget Parse(string input, string? baseDirectory)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (input.Contains('\0'))
        {
            throw new FormatException("Document targets cannot contain NUL characters.");
        }

        var anchorIndex = input.IndexOf('#');
        var pathAndLine = anchorIndex >= 0 ? input[..anchorIndex] : input;
        var anchor = anchorIndex >= 0 ? input[(anchorIndex + 1)..] : null;

        if (pathAndLine.Length == 0)
        {
            throw new FormatException("Document target paths cannot be empty.");
        }

        if (ZeroLineSuffix().IsMatch(pathAndLine))
        {
            throw new FormatException("Line numbers must be positive.");
        }

        var lineMatch = TerminalLineSuffix().Match(pathAndLine);
        int? line = null;
        if (lineMatch.Success)
        {
            if (!int.TryParse(lineMatch.Groups["line"].Value, out var parsedLine))
            {
                throw new FormatException("Line numbers must be valid integers.");
            }

            line = parsedLine;
            pathAndLine = pathAndLine[..lineMatch.Index];
        }

        if (pathAndLine.Length == 0)
        {
            throw new FormatException("Document target paths cannot be empty.");
        }

        var path = baseDirectory is null
            ? Path.GetFullPath(pathAndLine)
            : Path.GetFullPath(pathAndLine, baseDirectory);

        return new DocumentTarget(path, line, anchor);
    }
}
