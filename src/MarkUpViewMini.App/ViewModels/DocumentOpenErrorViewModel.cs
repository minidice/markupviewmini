using System.IO;
using System.Text;

namespace MarkUpViewMini.App.ViewModels;

public sealed class DocumentOpenErrorViewModel
{
    private DocumentOpenErrorViewModel(
        string message,
        bool canRetry,
        bool canChooseEncoding)
    {
        Message = message;
        CanRetry = canRetry;
        CanChooseEncoding = canChooseEncoding;
    }

    public string Message { get; }

    public bool CanRetry { get; }

    public bool CanChooseEncoding { get; }

    public bool CanSaveAs => false;

    public bool CanClose => true;

    public static DocumentOpenErrorViewModel From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            DecoderFallbackException => new(
                "The document is not valid UTF-8. Choose an encoding to try again.",
                canRetry: false,
                canChooseEncoding: true),
            FileNotFoundException or DirectoryNotFoundException => new(
                "The document could not be found. You can retry or close this tab.",
                canRetry: true,
                canChooseEncoding: false),
            UnauthorizedAccessException => new(
                "Access to the document was denied. You can retry or close this tab.",
                canRetry: true,
                canChooseEncoding: false),
            IOException => new(
                "The document could not be read. You can retry or close this tab.",
                canRetry: true,
                canChooseEncoding: false),
            _ => new(
                "The document could not be opened.",
                canRetry: false,
                canChooseEncoding: false),
        };
    }
}
