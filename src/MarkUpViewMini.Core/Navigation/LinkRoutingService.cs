using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.Core.Navigation;

public sealed class LinkRoutingService
{
    private readonly DocumentFormatRegistry formatRegistry;

    public LinkRoutingService(DocumentFormatRegistry formatRegistry)
    {
        ArgumentNullException.ThrowIfNull(formatRegistry);
        this.formatRegistry = formatRegistry;
    }

    public LinkRoute Route(
        string link,
        string currentDocumentPath,
        LinkOpenDisposition requested)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(currentDocumentPath);

        if (link.Length == 0)
        {
            throw new FormatException("Link targets cannot be empty.");
        }

        if (link.Contains('\0') || currentDocumentPath.Contains('\0'))
        {
            throw new FormatException("Link targets cannot contain NUL characters.");
        }

        if (!Enum.IsDefined(requested))
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }

        if (TryRouteWebLink(link, requested, out var webRoute))
        {
            return webRoute;
        }

        if (link.StartsWith("//", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Protocol-relative links are not supported.");
        }

        if (HasUriScheme(link) && !Path.IsPathFullyQualified(link))
        {
            throw new NotSupportedException("Only HTTP and HTTPS URI schemes are supported.");
        }

        if (!Path.IsPathFullyQualified(currentDocumentPath))
        {
            throw new FormatException("The current document path must be absolute.");
        }

        var target = ParseLocalTarget(link, currentDocumentPath);
        var supportedInternally = IsSupportedInternally(target.Path);

        if (requested == LinkOpenDisposition.Internal && !supportedInternally)
        {
            throw new NotSupportedException("The target format cannot be opened internally.");
        }

        var kind = requested switch
        {
            LinkOpenDisposition.WindowsDefault => LinkRouteKind.WindowsAssociatedApp,
            LinkOpenDisposition.NewTab when supportedInternally => LinkRouteKind.InternalNewTab,
            LinkOpenDisposition.Default or LinkOpenDisposition.Internal when supportedInternally =>
                LinkRouteKind.InternalCurrentTab,
            _ => LinkRouteKind.WindowsAssociatedApp
        };

        return new LinkRoute(kind, target.Path, target.Line, target.Anchor);
    }

    private static bool TryRouteWebLink(
        string link,
        LinkOpenDisposition requested,
        out LinkRoute route)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            if (requested == LinkOpenDisposition.Internal)
            {
                throw new NotSupportedException("Web links cannot be opened internally.");
            }

            route = new LinkRoute(LinkRouteKind.DefaultBrowser, link, null, null);
            return true;
        }

        route = null!;
        return false;
    }

    private static DocumentTarget ParseLocalTarget(string link, string currentDocumentPath)
    {
        if (link[0] == '#')
        {
            var anchor = DecodeUriComponent(link[1..]);
            if (anchor.Length == 0)
            {
                throw new FormatException("Same-document links must contain an anchor.");
            }

            return new DocumentTarget(Path.GetFullPath(currentDocumentPath), null, anchor);
        }

        var anchorIndex = link.IndexOf('#');
        var encodedPath = anchorIndex >= 0 ? link[..anchorIndex] : link;
        var encodedAnchor = anchorIndex >= 0 ? link[(anchorIndex + 1)..] : null;
        if (encodedPath.Contains('?'))
        {
            throw new FormatException("Local link targets cannot contain a query.");
        }

        var decodedPath = DecodeUriComponent(encodedPath);
        var decodedAnchor = encodedAnchor is null ? null : DecodeUriComponent(encodedAnchor);
        if (decodedPath.StartsWith("//", StringComparison.Ordinal)
            || decodedPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new NotSupportedException("UNC link targets are not supported.");
        }

        if (Path.IsPathRooted(decodedPath))
        {
            throw new FormatException("Local link targets must be relative.");
        }

        if (decodedPath.Contains('?'))
        {
            throw new FormatException("Local link targets cannot contain a query.");
        }

        var baseDirectory = Path.GetDirectoryName(currentDocumentPath);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            throw new FormatException("The relative link target cannot be resolved.");
        }

        try
        {
            var hashPlaceholder = "__MARKUPVIEWMINI_HASH__";
            while (decodedPath.Contains(hashPlaceholder, StringComparison.Ordinal)
                || (baseDirectory?.Contains(hashPlaceholder, StringComparison.Ordinal) ?? false))
            {
                hashPlaceholder += "_";
            }

            var parserSafePath = decodedPath.Replace("#", hashPlaceholder, StringComparison.Ordinal);
            var target = DocumentTargetParser.Parse(parserSafePath, baseDirectory);
            return target with
            {
                Path = target.Path.Replace(hashPlaceholder, "#", StringComparison.Ordinal),
                Anchor = decodedAnchor
            };
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("The link target is not a valid local path.", exception);
        }
    }

    private bool IsSupportedInternally(string path)
    {
        try
        {
            var capabilities = formatRegistry.Resolve(path).Descriptor.Capabilities;
            return capabilities.HasFlag(DocumentCapabilities.InternalLinks);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private bool HasUriScheme(string link)
    {
        if (HasTerminalLineSuffix(link))
        {
            return false;
        }

        var colonIndex = link.IndexOf(':');
        if (colonIndex <= 0 || !char.IsAsciiLetter(link[0]))
        {
            return false;
        }

        for (var index = 1; index < colonIndex; index++)
        {
            var character = link[index];
            if (!char.IsAsciiLetterOrDigit(character) && character is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private bool HasTerminalLineSuffix(string link)
    {
        var endIndex = link.IndexOf('#');
        if (endIndex < 0)
        {
            endIndex = link.Length;
        }

        if (endIndex == 0)
        {
            return false;
        }

        var colonIndex = link.LastIndexOf(':', endIndex - 1);
        if (colonIndex < 0 || colonIndex == endIndex - 1)
        {
            return false;
        }

        var pathPrefix = link[..colonIndex];
        if (!IsRegisteredFormat(pathPrefix)
            && !pathPrefix.Contains(Path.DirectorySeparatorChar)
            && !pathPrefix.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        for (var index = colonIndex + 1; index < endIndex; index++)
        {
            if (!char.IsAsciiDigit(link[index]))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsRegisteredFormat(string path)
    {
        try
        {
            formatRegistry.Resolve(path);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string DecodeUriComponent(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1])
                || !Uri.IsHexDigit(value[index + 2]))
            {
                throw new FormatException("Link targets contain malformed percent encoding.");
            }

            index += 2;
        }

        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Contains('\0'))
        {
            throw new FormatException("Link targets cannot contain NUL characters.");
        }

        return decoded;
    }
}
