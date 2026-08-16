using System.Text.Json;

namespace MarkUpViewMini.App.Web;

public sealed record WebMessageEnvelope(
    int Version,
    string Type,
    Guid RequestId,
    Guid WindowId,
    Guid TabId,
    long DocumentRevision,
    JsonElement Payload);

public sealed class WebMessageValidationException : Exception
{
    public WebMessageValidationException(string message)
        : base(message)
    {
    }
}
