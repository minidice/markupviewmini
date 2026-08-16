using System.IO;
using System.Text.Json;
using MarkUpViewMini.App.ViewModels;
using MarkUpViewMini.Core.Workspace;

namespace MarkUpViewMini.App.Web;

public static class WebViewPolicy
{
    public const string AppHostName = "app.markupviewmini.local";
    public const string DocumentAssetsHostName = "document-assets.local";

    public static Uri DocumentAssetsBaseUri { get; } =
        new($"https://{DocumentAssetsHostName}/");

    public static Uri BuildBootstrapUri(Guid windowId, Guid tabId)
    {
        if (windowId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty window ID is required.", nameof(windowId));
        }

        if (tabId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty tab ID is required.", nameof(tabId));
        }

        var windowValue = Uri.EscapeDataString(windowId.ToString("D"));
        var tabValue = Uri.EscapeDataString(tabId.ToString("D"));
        return new Uri($"https://{AppHostName}/index.html?windowId={windowValue}&tabId={tabValue}");
    }

    public static bool IsAllowedTopLevelNavigation(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.IsAbsoluteUri &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, AppHostName, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    public static bool IsAllowedDocumentAssetUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.IsAbsoluteUri &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, DocumentAssetsHostName, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    public static string GetDocumentAssetsDirectory(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || !Path.IsPathFullyQualified(documentPath))
        {
            throw new ArgumentException("A fully qualified document path is required.", nameof(documentPath));
        }

        var fullPath = Path.GetFullPath(documentPath);
        return Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("The document path has no containing directory.", nameof(documentPath));
    }

    public static bool TryResolveDocumentAssetPath(
        string documentPath,
        string reference,
        out string? resolvedPath)
    {
        resolvedPath = null;
        if (!TryDecodeSafeRelativeAssetPath(reference, out var decoded))
        {
            return false;
        }

        string directory;
        try
        {
            directory = GetDocumentAssetsDirectory(documentPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or UriFormatException)
        {
            return false;
        }

        var localReference = decoded.Replace('/', Path.DirectorySeparatorChar);

        string candidate;
        try
        {
            candidate = Path.GetFullPath(localReference, directory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var relative = Path.GetRelativePath(directory, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }

    public static bool TryResolveDocumentAssetRequest(
        string documentPath,
        string requestAddress,
        out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(requestAddress) ||
            requestAddress.Any(char.IsControl) ||
            !Uri.TryCreate(requestAddress, UriKind.Absolute, out var uri) ||
            !IsAllowedDocumentAssetUri(uri) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var schemeSeparator = requestAddress.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        var pathStart = requestAddress.IndexOf('/', schemeSeparator + 3);
        if (pathStart < 0 || pathStart == requestAddress.Length - 1)
        {
            return false;
        }

        var escapedPath = requestAddress[(pathStart + 1)..];
        return TryResolveDocumentAssetPath(documentPath, escapedPath, out resolvedPath);
    }

    private static bool TryDecodeSafeRelativeAssetPath(string reference, out string decoded)
    {
        decoded = string.Empty;
        if (string.IsNullOrWhiteSpace(reference) ||
            reference.Any(char.IsControl) ||
            reference.IndexOfAny(['?', '#']) >= 0)
        {
            return false;
        }

        if (!HasValidPercentEncoding(reference) || ContainsEncodedSeparator(reference))
        {
            return false;
        }

        string decodedOnce;
        try
        {
            decodedOnce = Uri.UnescapeDataString(reference);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (decodedOnce.Contains('%') ||
            decodedOnce.Any(char.IsControl) ||
            decodedOnce.IndexOfAny(['\\', '?', '#', ':']) >= 0 ||
            decodedOnce.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(decodedOnce.Replace('/', Path.DirectorySeparatorChar)) ||
            Uri.TryCreate(decodedOnce, UriKind.Absolute, out _) ||
            decodedOnce.Split('/').Any(segment => segment.Equals("..", StringComparison.Ordinal)))
        {
            return false;
        }

        decoded = decodedOnce;
        return true;
    }

    private static bool ContainsEncodedSeparator(string value)
    {
        for (var index = 0; index + 2 < value.Length; index++)
        {
            if (value[index] == '%' &&
                ((value[index + 1] == '2' && char.ToLowerInvariant(value[index + 2]) == 'f') ||
                 (value[index + 1] == '5' && char.ToLowerInvariant(value[index + 2]) == 'c')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    public static bool IsMatchingReady(
        WebMessageEnvelope message,
        Guid windowId,
        Guid tabId) =>
        string.Equals(message.Type, "surface.ready", StringComparison.Ordinal) &&
        message.WindowId == windowId &&
        message.TabId == tabId &&
        message.DocumentRevision == 0;

    public static bool IsCurrentActivationResponse(
        WebMessageEnvelope message,
        Guid requestId,
        Guid windowId,
        Guid tabId,
        long revision) =>
        (string.Equals(message.Type, "document.rendered", StringComparison.Ordinal) ||
         string.Equals(message.Type, "surface.error", StringComparison.Ordinal)) &&
        message.RequestId == requestId &&
        message.WindowId == windowId &&
        message.TabId == tabId &&
        message.DocumentRevision == revision;

    public static bool IsCurrentDocumentMessage(
        WebMessageEnvelope message,
        WebResponseContext response,
        Guid windowId)
    {
        var isActivationResponse =
            string.Equals(message.Type, "document.outline", StringComparison.Ordinal) &&
            message.RequestId == response.RequestId;
        var isInteractiveRequest =
            string.Equals(message.Type, "link.open", StringComparison.Ordinal) ||
            string.Equals(message.Type, "link.contextMenu", StringComparison.Ordinal);

        var isCorrelatedEditRequest =
            (string.Equals(message.Type, "document.changed", StringComparison.Ordinal) ||
             string.Equals(message.Type, "document.changeBatchStart", StringComparison.Ordinal) ||
             string.Equals(message.Type, "document.changeBatchChunk", StringComparison.Ordinal) ||
             string.Equals(message.Type, "document.changeBatchCommit", StringComparison.Ordinal) ||
             string.Equals(message.Type, "document.modeChanged", StringComparison.Ordinal) ||
             string.Equals(message.Type, "document.uiHintsChanged", StringComparison.Ordinal) ||
             string.Equals(message.Type, "mermaid.editRequested", StringComparison.Ordinal) ||
             string.Equals(message.Type, "mermaid.focusCompleted", StringComparison.Ordinal)) &&
            message.RequestId == response.RequestId;

        return (isActivationResponse || isInteractiveRequest || isCorrelatedEditRequest) &&
            message.WindowId == windowId &&
            message.TabId == response.TabId &&
            message.DocumentRevision == response.Revision;
    }

    public static string CreateGoToLineMessage(
        WebResponseContext response,
        Guid windowId,
        int line)
    {
        if (line <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        return SerializeHostMessage(response, windowId, "navigation.goToLine", new { line });
    }

    public static string CreateGoToAnchorMessage(
        WebResponseContext response,
        Guid windowId,
        string anchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
        return SerializeHostMessage(response, windowId, "navigation.goToAnchor", new { anchor });
    }

    public static string CreateFindMessage(
        WebResponseContext response,
        Guid windowId,
        string type)
    {
        if (type is not "find.open" and
            not "find.next" and
            not "find.previous" and
            not "find.close")
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        return SerializeHostMessage(response, windowId, type, new { });
    }

    public static string CreateEditorCommandMessage(
        WebResponseContext response,
        Guid windowId,
        string type)
    {
        if (type is not "editor.undo" and not "editor.redo")
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        return SerializeHostMessage(response, windowId, type, new { });
    }

    public static string CreateDocumentActivationMessage(
        DocumentTabViewModel tab,
        Guid requestId,
        Guid windowId)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (requestId == Guid.Empty || windowId == Guid.Empty || tab.Id == Guid.Empty || tab.Revision < 0)
        {
            throw new ArgumentException("A complete activation owner is required.");
        }

        return SerializeHostMessage(
            requestId,
            windowId,
            tab.Id,
            tab.Revision,
            "document.activate",
            new
            {
                path = tab.Path,
                text = tab.Text,
                dirty = tab.IsDirty,
                mode = tab.Mode == DocumentMode.Edit ? "edit" : "read",
                line = tab.TargetLine,
                anchor = tab.TargetAnchor,
                assetBaseUrl = DocumentAssetsBaseUri.AbsoluteUri,
                preferredNewline = tab.PreferredNewLine,
                selection = new
                {
                    anchor = tab.UiHints.SelectionAnchor,
                    head = tab.UiHints.SelectionHead,
                },
                scrollTop = tab.UiHints.ScrollTop,
                splitRatio = tab.UiHints.SplitRatio,
                find = new
                {
                    matchCase = tab.UiHints.FindMatchCase,
                    wholeWord = tab.UiHints.FindWholeWord,
                    useRegex = tab.UiHints.FindUseRegex,
                },
            });
    }

    internal static string CreateDocumentRecoveryMessage(
        WebViewRecoveryTabSnapshot snapshot,
        Guid requestId,
        Guid windowId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (requestId == Guid.Empty ||
            windowId == Guid.Empty ||
            snapshot.TabId == Guid.Empty ||
            snapshot.Revision < 0)
        {
            throw new ArgumentException("A complete recovery owner is required.");
        }

        return SerializeHostMessage(
            requestId,
            windowId,
            snapshot.TabId,
            snapshot.Revision,
            "document.activate",
            new
            {
                path = snapshot.Path,
                text = snapshot.Text,
                dirty = snapshot.IsDirty,
                mode = snapshot.Mode == DocumentMode.Edit ? "edit" : "read",
                line = (int?)null,
                anchor = (string?)null,
                assetBaseUrl = DocumentAssetsBaseUri.AbsoluteUri,
                preferredNewline = snapshot.PreferredNewLine,
                selection = new
                {
                    anchor = snapshot.UiHints.SelectionAnchor,
                    head = snapshot.UiHints.SelectionHead,
                },
                scrollTop = snapshot.UiHints.ScrollTop,
                splitRatio = snapshot.UiHints.SplitRatio,
                find = new
                {
                    matchCase = snapshot.UiHints.FindMatchCase,
                    wholeWord = snapshot.UiHints.FindWholeWord,
                    useRegex = snapshot.UiHints.FindUseRegex,
                },
            });
    }

    public static string CreateDocumentChangeAcceptedMessage(
        WebResponseContext response,
        Guid windowId,
        long acceptedRevision)
    {
        if (acceptedRevision != checked(response.Revision + 1))
        {
            throw new ArgumentOutOfRangeException(nameof(acceptedRevision));
        }

        return SerializeHostMessage(
            response.RequestId,
            windowId,
            response.TabId,
            acceptedRevision,
            "document.changeAccepted",
            new { });
    }

    public static string CreateSetEditorPreferencesMessage(
        WebResponseContext response,
        Guid windowId,
        DocumentUiHints hints) =>
        SerializeHostMessage(
            response.RequestId,
            windowId,
            response.TabId,
            response.Revision,
            "document.setEditorPreferences",
            new
            {
                splitRatio = hints.SplitRatio,
                find = new
                {
                    matchCase = hints.FindMatchCase,
                    wholeWord = hints.FindWholeWord,
                    useRegex = hints.FindUseRegex,
                },
            });

    public static string CreateDocumentChangeRejectedMessage(
        WebResponseContext response,
        Guid windowId,
        long currentRevision,
        Guid resyncRequestId)
    {
        if (currentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentRevision));
        }

        if (resyncRequestId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty resync request ID is required.", nameof(resyncRequestId));
        }

        return SerializeHostMessage(
            response.RequestId,
            windowId,
            response.TabId,
            currentRevision,
            "document.changeRejected",
            new { resyncRequestId = resyncRequestId.ToString("D") });
    }

    public static string CreateSetModeMessage(
        WebResponseContext response,
        Guid windowId,
        DocumentMode mode) =>
        SerializeHostMessage(
            response,
            windowId,
            "document.setMode",
            new { mode = mode == DocumentMode.Edit ? "edit" : "read" });

    public static string CreateSaveCompletedMessage(
        WebResponseContext response,
        Guid windowId) =>
        SerializeHostMessage(response, windowId, "document.saveCompleted", new { });

    private static string SerializeHostMessage(
        WebResponseContext response,
        Guid windowId,
        string type,
        object payload)
    {
        if (response.RequestId == Guid.Empty || response.TabId == Guid.Empty || response.Revision < 0)
        {
            throw new ArgumentException("A current document response is required.", nameof(response));
        }

        if (windowId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty window ID is required.", nameof(windowId));
        }

        return SerializeHostMessage(
            response.RequestId,
            windowId,
            response.TabId,
            response.Revision,
            type,
            payload);
    }

    private static string SerializeHostMessage(
        Guid requestId,
        Guid windowId,
        Guid tabId,
        long revision,
        string type,
        object payload)
    {
        return JsonSerializer.Serialize(new
        {
            version = 1,
            type,
            requestId = requestId.ToString("D"),
            windowId = windowId.ToString("D"),
            tabId = tabId.ToString("D"),
            documentRevision = revision,
            payload,
        });
    }
}
