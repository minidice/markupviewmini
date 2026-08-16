using System.Text.Json;

namespace MarkUpViewMini.App.Mermaid;

internal sealed class MermaidDialogBridge : IDisposable
{
    private const int BridgeVersion = 1;
    private readonly object gate = new();
    private readonly MermaidEditRequest request;
    private readonly Action<string> postMessage;
    private readonly TaskCompletionSource<MermaidDialogResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool opened;
    private string currentSource;
    private int currentSourceVersion;
    private string? validatedSource;
    private int? validatedSourceVersion;
    private bool currentSourceSupported;
    private bool terminal;
    private bool disposed;

    public MermaidDialogBridge(MermaidEditRequest request, Action<string> postMessage)
    {
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        this.postMessage = postMessage ?? throw new ArgumentNullException(nameof(postMessage));
        currentSource = request.Snapshot.Source;
    }

    public Task<MermaidDialogResult> Completion => completion.Task;

    public bool CurrentSourceSupported
    {
        get
        {
            lock (gate)
            {
                return !disposed && !terminal && currentSourceSupported;
            }
        }
    }

    public bool TryHandleMessage(string json)
    {
        if (!TryParseEnvelope(json, out var type, out var payload))
        {
            return false;
        }

        string? outbound = null;
        MermaidDialogResult? result = null;
        lock (gate)
        {
            if (disposed || terminal)
            {
                return false;
            }

            if (string.Equals(type, "mermaid.ready", StringComparison.Ordinal))
            {
                if (opened || payload.EnumerateObject().Any())
                {
                    return false;
                }

                opened = true;
                outbound = JsonSerializer.Serialize(new
                {
                    version = BridgeVersion,
                    type = "mermaid.open",
                    payload = new
                    {
                        sessionId = request.Snapshot.SessionId,
                        source = request.Snapshot.Source,
                        language = "mermaid",
                    },
                });
            }
            else if (!opened || !TryReadSessionId(payload, out var sessionId) ||
                     sessionId != request.Snapshot.SessionId)
            {
                return false;
            }
            else if (string.Equals(type, "mermaid.confirm", StringComparison.Ordinal))
            {
                if (!TryReadConfirm(payload, out var source, out var sourceVersion) ||
                    !currentSourceSupported ||
                    sourceVersion != currentSourceVersion ||
                    validatedSourceVersion != currentSourceVersion ||
                    !string.Equals(source, currentSource, StringComparison.Ordinal) ||
                    !string.Equals(source, validatedSource, StringComparison.Ordinal))
                {
                    return false;
                }

                terminal = true;
                result = MermaidDialogResult.Confirmed(source);
            }
            else if (string.Equals(type, "mermaid.validityChanged", StringComparison.Ordinal))
            {
                if (!TryReadValidity(payload, out var source, out var sourceVersion, out var supported) ||
                    sourceVersion != currentSourceVersion ||
                    validatedSourceVersion == sourceVersion ||
                    !string.Equals(source, currentSource, StringComparison.Ordinal))
                {
                    return false;
                }

                validatedSource = source;
                validatedSourceVersion = sourceVersion;
                currentSourceSupported = supported;
            }
            else if (string.Equals(type, "mermaid.changed", StringComparison.Ordinal))
            {
                if (!TryReadChanged(payload, out var source, out var sourceVersion) ||
                    currentSourceVersion == int.MaxValue ||
                    sourceVersion != currentSourceVersion + 1)
                {
                    return false;
                }

                currentSource = source;
                currentSourceVersion = sourceVersion;
                validatedSource = null;
                validatedSourceVersion = null;
                currentSourceSupported = false;
            }
            else if (string.Equals(type, "mermaid.cancel", StringComparison.Ordinal))
            {
                if (payload.EnumerateObject().Count() != 1)
                {
                    return false;
                }

                terminal = true;
                result = MermaidDialogResult.Canceled(request.Snapshot.Source);
            }
            else
            {
                return false;
            }
        }

        if (outbound is not null)
        {
            postMessage(outbound);
        }

        if (result is not null)
        {
            completion.TrySetResult(result);
        }

        return true;
    }

    public bool TryCancel()
    {
        lock (gate)
        {
            if (disposed || terminal)
            {
                return false;
            }

            terminal = true;
        }

        completion.TrySetResult(MermaidDialogResult.Canceled(request.Snapshot.Source));
        return true;
    }

    public void Dispose()
    {
        var resolveCancel = false;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!terminal)
            {
                terminal = true;
                resolveCancel = true;
            }
        }

        if (resolveCancel)
        {
            completion.TrySetResult(MermaidDialogResult.Canceled(request.Snapshot.Source));
        }
    }

    private static bool TryParseEnvelope(
        string json,
        out string type,
        out JsonElement payload)
    {
        type = string.Empty;
        payload = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 3 ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var parsedVersion) ||
                parsedVersion != BridgeVersion ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("payload", out var payloadElement) ||
                payloadElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            type = typeElement.GetString() ?? string.Empty;
            payload = payloadElement.Clone();
            return type.Length != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSessionId(JsonElement payload, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        return payload.TryGetProperty("sessionId", out var sessionElement) &&
               sessionElement.ValueKind == JsonValueKind.String &&
               Guid.TryParse(sessionElement.GetString(), out sessionId) &&
               sessionId != Guid.Empty;
    }

    private static bool TryReadConfirm(
        JsonElement payload,
        out string source,
        out int sourceVersion)
    {
        source = string.Empty;
        sourceVersion = -1;
        if (payload.EnumerateObject().Count() != 4 ||
            !payload.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("language", out var languageElement) ||
            languageElement.ValueKind != JsonValueKind.String ||
            !string.Equals(languageElement.GetString(), "mermaid", StringComparison.Ordinal) ||
            !payload.TryGetProperty("sourceVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out sourceVersion) ||
            sourceVersion < 0)
        {
            return false;
        }

        source = sourceElement.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadValidity(
        JsonElement payload,
        out string source,
        out int sourceVersion,
        out bool supported)
    {
        source = string.Empty;
        sourceVersion = -1;
        supported = false;
        if (payload.EnumerateObject().Count() != 6 ||
            !payload.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("language", out var languageElement) ||
            languageElement.ValueKind != JsonValueKind.String ||
            !string.Equals(languageElement.GetString(), "mermaid", StringComparison.Ordinal) ||
            !payload.TryGetProperty("sourceVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out sourceVersion) ||
            sourceVersion < 0 ||
            !payload.TryGetProperty("supported", out var supportedElement) ||
            supportedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("reason", out var reasonElement) ||
            reasonElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        source = sourceElement.GetString() ?? string.Empty;
        supported = supportedElement.GetBoolean();
        var reason = reasonElement.GetString() ?? string.Empty;
        return supported ? reason.Length == 0 : reason.Length != 0;
    }

    private static bool TryReadChanged(
        JsonElement payload,
        out string source,
        out int sourceVersion)
    {
        source = string.Empty;
        sourceVersion = -1;
        if (payload.EnumerateObject().Count() != 4 ||
            !payload.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("language", out var languageElement) ||
            languageElement.ValueKind != JsonValueKind.String ||
            !string.Equals(languageElement.GetString(), "mermaid", StringComparison.Ordinal) ||
            !payload.TryGetProperty("sourceVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out sourceVersion) ||
            sourceVersion < 0)
        {
            return false;
        }

        source = sourceElement.GetString() ?? string.Empty;
        return true;
    }
}
