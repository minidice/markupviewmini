using System.Buffers.Binary;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkUpViewMini.Core.Activation;

namespace MarkUpViewMini.Infrastructure.Activation;

internal static class ActivationProtocol
{
    internal const int MaximumPathCount = 32;
    internal const int MaximumPayloadBytes = 256 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static byte[] Serialize(ActivationRequest request) =>
        Serialize(request, WindowsActivationPathInspector.Instance);

    internal static byte[] Serialize(
        ActivationRequest request,
        IActivationPathInspector pathInspector)
    {
        var normalized = ValidateAndNormalize(request, pathInspector);
        var payload = JsonSerializer.SerializeToUtf8Bytes(normalized, SerializerOptions);
        if (payload.Length is < 1 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The activation payload is outside the supported size range.");
        }

        return payload;
    }

    internal static ActivationRequest ValidateAndNormalize(ActivationRequest request) =>
        ValidateAndNormalize(request, WindowsActivationPathInspector.Instance);

    internal static ActivationRequest Deserialize(ReadOnlySpan<byte> payload) =>
        Deserialize(payload, WindowsActivationPathInspector.Instance);

    internal static ActivationRequest Deserialize(
        ReadOnlySpan<byte> payload,
        IActivationPathInspector pathInspector)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The activation payload is outside the supported size range.");
        }

        try
        {
            var request = JsonSerializer.Deserialize<ActivationRequest>(payload, SerializerOptions)
                ?? throw new InvalidDataException("The activation payload is empty.");
            return ValidateAndNormalize(request, pathInspector);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The activation payload is not valid JSON.", exception);
        }
    }

    internal static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The activation payload is outside the supported size range.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The activation frame length is outside the supported range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    internal static ActivationRequest ValidateAndNormalize(
        ActivationRequest request,
        IActivationPathInspector pathInspector)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pathInspector);
        if (request.Version != 1 ||
            request.Kind != ActivationKind.FileOpen ||
            request.SenderProcessId <= 0 ||
            request.Paths is null ||
            request.Paths.Count > MaximumPathCount)
        {
            throw new InvalidDataException("The activation request does not match version 1 of the file-open schema.");
        }

        var normalizedPaths = new string[request.Paths.Count];
        for (var index = 0; index < request.Paths.Count; index++)
        {
            normalizedPaths[index] = NormalizeLocalPath(request.Paths[index], pathInspector);
        }

        return request with { Paths = normalizedPaths };
    }

    private static string NormalizeLocalPath(
        string? path,
        IActivationPathInspector pathInspector)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\0') ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Activation paths must be absolute local paths.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException("Activation paths must be absolute local paths.");
            }

            var normalized = Path.GetFullPath(path);
            var root = Path.GetPathRoot(normalized);
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal) ||
                root is null ||
                root.Length < 2 ||
                root[1] != ':')
            {
                throw new InvalidDataException("Activation paths must be absolute local paths.");
            }

            var driveType = pathInspector.GetDriveType(root);
            if (driveType is not DriveType.Fixed and
                not DriveType.Removable and
                not DriveType.CDRom and
                not DriveType.Ram)
            {
                throw new InvalidDataException("Activation paths must use a local drive.");
            }

            var current = root;
            var rootAttributes = pathInspector.GetExistingAttributes(current);
            if (rootAttributes?.HasFlag(FileAttributes.ReparsePoint) is true)
            {
                throw new InvalidDataException("Activation paths cannot contain reparse points.");
            }

            foreach (var component in normalized[root.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                var attributes = pathInspector.GetExistingAttributes(current);
                if (attributes is null)
                {
                    break;
                }

                if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Activation paths cannot contain reparse points.");
                }
            }

            return normalized;
        }
        catch (Exception exception) when (
            exception is not InvalidDataException &&
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException or
                IOException or
                UnauthorizedAccessException or
                SecurityException)
        {
            throw new InvalidDataException("Activation paths must be absolute local paths.", exception);
        }
    }
}
