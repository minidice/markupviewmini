using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MarkUpViewMini.Infrastructure.Paths;

namespace MarkUpViewMini.Infrastructure.Diagnostics;

public sealed class SafeFileLogger : ISafeLogger
{
    private const int MaxPathLength = 1024;
    private const int MaxTypeLength = 256;
    private const int MaxFrameCount = 32;
    private const int MaxFrameLength = 256;
    private const int MaxReadBytes = 1024 * 1024;
    private const int MaxCopiedCharacters = 256 * 1024;
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object fileGate;

    public SafeFileLogger(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        LogFilePath = Path.GetFullPath(Path.Combine(paths.LogsDirectory, "diagnostics.jsonl"));
        fileGate = FileGates.GetOrAdd(LogFilePath, static _ => new object());
    }

    public string LogFilePath { get; }

    public void Write(string component, string eventName, string? path, Exception? error)
    {
        try
        {
            var record = new SafeLogRecord(
                AllowComponent(component),
                AllowEvent(eventName),
                NormalizePath(path),
                Bound(error?.GetType().FullName, MaxTypeLength),
                error?.HResult,
                GetStackFrames(error));
            var json = JsonSerializer.Serialize(record, SafeLogJsonContext.Default.SafeLogRecord);
            var bytes = Utf8WithoutBom.GetBytes(json + "\n");

            lock (fileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                using var stream = new FileStream(
                    LogFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Diagnostics must never affect the application path that is being diagnosed.
        }
    }

    public string ReadSafeText()
    {
        try
        {
            lock (fileGate)
            {
                if (!File.Exists(LogFilePath))
                {
                    return string.Empty;
                }

                using var stream = new FileStream(
                    LogFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var startsMidFile = stream.Length > MaxReadBytes;
                if (startsMidFile)
                {
                    stream.Seek(-MaxReadBytes, SeekOrigin.End);
                }

                using var reader = new StreamReader(
                    stream,
                    Utf8WithoutBom,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096,
                    leaveOpen: false);
                if (startsMidFile)
                {
                    _ = reader.ReadLine();
                }

                var records = new Queue<string>();
                var characterCount = 0;
                while (reader.ReadLine() is { } line)
                {
                    var rewritten = RewriteAllowlistedRecord(line);
                    if (rewritten is null)
                    {
                        continue;
                    }

                    records.Enqueue(rewritten);
                    characterCount += rewritten.Length + 1;
                    while (characterCount > MaxCopiedCharacters && records.Count > 1)
                    {
                        characterCount -= records.Dequeue().Length + 1;
                    }
                }

                return records.Count == 0
                    ? string.Empty
                    : string.Join('\n', records) + "\n";
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string AllowComponent(string? value) =>
        value is "WebView" ? value : "unknown";

    private static string AllowEvent(string? value) =>
        value is "ProcessFailed" or "RecoveryFailed" or "RecoverySucceeded"
            ? value
            : "unknown";

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Bound(RemoveControls(Path.GetFullPath(path)), MaxPathLength);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetStackFrames(Exception? error)
    {
        if (error is null)
        {
            return [];
        }

        var frames = new StackTrace(error, fNeedFileInfo: false).GetFrames();
        if (frames is null)
        {
            return [];
        }

        return frames
            .Take(MaxFrameCount)
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => Bound(
                $"{method!.DeclaringType?.FullName ?? "unknown"}.{method.Name}",
                MaxFrameLength)!)
            .ToArray();
    }

    private static string? RewriteAllowlistedRecord(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize(
                json,
                SafeLogJsonContext.Default.SafeLogRecord);
            if (record is null)
            {
                return null;
            }

            var frames = (record.StackFrames ?? [])
                .Take(MaxFrameCount)
                .Select(frame => Bound(frame, MaxFrameLength))
                .Where(frame => !string.IsNullOrWhiteSpace(frame))
                .Cast<string>()
                .ToArray();
            var safe = new SafeLogRecord(
                AllowComponent(record.Component),
                AllowEvent(record.EventName),
                NormalizePath(record.Path),
                Bound(record.ExceptionType, MaxTypeLength),
                record.HResult,
                frames);
            return JsonSerializer.Serialize(safe, SafeLogJsonContext.Default.SafeLogRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Bound(string? value, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        var sanitized = RemoveControls(value);
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength];
    }

    private static string RemoveControls(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    internal sealed record SafeLogRecord(
        string Component,
        string EventName,
        string? Path,
        string? ExceptionType,
        int? HResult,
        IReadOnlyList<string> StackFrames);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SafeFileLogger.SafeLogRecord))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SafeLogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
