using System.Text.Json;
using MarkUpViewMini.Infrastructure.Diagnostics;
using MarkUpViewMini.Infrastructure.Paths;

namespace MarkUpViewMini.Infrastructure.Tests.Diagnostics;

public sealed class SafeFileLoggerTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        nameof(SafeFileLoggerTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_emits_only_allowlisted_exception_details()
    {
        var logger = new SafeFileLogger(new TestPaths(directory));
        const string bodySecret = "SECRET-DOCUMENT-BODY";
        const string querySecret = "SECRET-FIND-QUERY";
        var error = CaptureException(bodySecret);
        error.Data["query"] = querySecret;
        var documentPath = Path.Combine(directory, "document.md");

        logger.Write("WebView", "ProcessFailed", documentPath, error);

        var line = Assert.Single(File.ReadAllLines(logger.LogFilePath));
        using var json = JsonDocument.Parse(line);
        var record = json.RootElement;
        Assert.Equal(
            ["component", "eventName", "path", "exceptionType", "hResult", "stackFrames"],
            record.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("WebView", record.GetProperty("component").GetString());
        Assert.Equal("ProcessFailed", record.GetProperty("eventName").GetString());
        Assert.Equal(Path.GetFullPath(documentPath), record.GetProperty("path").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, record.GetProperty("exceptionType").GetString());
        Assert.Equal(error.HResult, record.GetProperty("hResult").GetInt32());
        Assert.Contains(
            record.GetProperty("stackFrames").EnumerateArray(),
            frame => frame.GetString()!.Contains(nameof(CaptureException), StringComparison.Ordinal));
        Assert.DoesNotContain(bodySecret, line, StringComparison.Ordinal);
        Assert.DoesNotContain(querySecret, line, StringComparison.Ordinal);
        Assert.DoesNotContain("message", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_writers_produce_complete_independent_json_lines()
    {
        var logger = new SafeFileLogger(new TestPaths(directory));

        await Task.WhenAll(Enumerable.Range(0, 128).Select(index => Task.Run(() =>
            logger.Write("WebView", "ProcessFailed", null, null))));

        var lines = File.ReadAllLines(logger.LogFilePath);
        Assert.Equal(128, lines.Length);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("ProcessFailed", json.RootElement.GetProperty("eventName").GetString());
        }
    }

    [Fact]
    public void Unknown_component_and_event_values_cannot_be_used_as_free_form_log_payloads()
    {
        var logger = new SafeFileLogger(new TestPaths(directory));

        logger.Write("SECRET-DOCUMENT-BODY", "SECRET-FIND-QUERY", null, null);

        var line = Assert.Single(File.ReadAllLines(logger.LogFilePath));
        Assert.DoesNotContain("SECRET-DOCUMENT-BODY", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FIND-QUERY", line, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(line);
        Assert.Equal("unknown", json.RootElement.GetProperty("component").GetString());
        Assert.Equal("unknown", json.RootElement.GetProperty("eventName").GetString());
    }

    [Fact]
    public void Fields_are_bounded_and_control_characters_are_removed()
    {
        var logger = new SafeFileLogger(new TestPaths(directory));
        var path = Path.Combine(directory, new string('p', 2048) + "\r\nsecret.md");

        logger.Write(
            new string('c', 300) + "\r\nINJECTED",
            new string('e', 300) + "\u0001INJECTED",
            path,
            CaptureException("not serialized"));

        var line = Assert.Single(File.ReadAllLines(logger.LogFilePath));
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        using var json = JsonDocument.Parse(line);
        var record = json.RootElement;
        Assert.InRange(record.GetProperty("component").GetString()!.Length, 1, 64);
        Assert.InRange(record.GetProperty("eventName").GetString()!.Length, 1, 64);
        Assert.InRange(record.GetProperty("path").GetString()!.Length, 1, 1024);
        Assert.InRange(record.GetProperty("stackFrames").GetArrayLength(), 0, 32);
    }

    [Fact]
    public void Io_failures_are_isolated_from_the_caller()
    {
        var logsPath = Path.Combine(directory, "logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(logsPath, "not a directory");
        var logger = new SafeFileLogger(new TestPaths(directory, logsPath));

        var failure = Record.Exception(() =>
            logger.Write("WebView", "ProcessFailed", null, CaptureException("private")));

        Assert.Null(failure);
    }

    [Fact]
    public void ReadSafeText_rewrites_records_through_the_allowlist_and_skips_invalid_lines()
    {
        var logger = new SafeFileLogger(new TestPaths(directory));
        logger.Write("WebView", "ProcessFailed", Path.Combine(directory, "document.md"), null);
        File.AppendAllText(
            logger.LogFilePath,
            "{\"component\":\"WebView\",\"eventName\":\"RecoveryFailed\",\"path\":null," +
            "\"exceptionType\":null,\"hResult\":null,\"stackFrames\":[]," +
            "\"body\":\"SECRET-DOCUMENT-BODY\",\"message\":\"SECRET-FIND-QUERY\"}\n" +
            "not-json\n");

        var copied = logger.ReadSafeText();

        Assert.DoesNotContain("SECRET-DOCUMENT-BODY", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FIND-QUERY", copied, StringComparison.Ordinal);
        Assert.Equal(2, copied.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        foreach (var line in copied.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal(
                ["component", "eventName", "path", "exceptionType", "hResult", "stackFrames"],
                json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Exception CaptureException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class TestPaths(string root, string? logsDirectory = null) : IAppDataPaths
    {
        public string DataDirectory => root;
        public string SettingsFile => Path.Combine(root, "settings.json");
        public string SessionFile => Path.Combine(root, "session.json");
        public string RecoveryDirectory => Path.Combine(root, "recovery");
        public string LogsDirectory => logsDirectory ?? Path.Combine(root, "logs");
        public string WebView2Directory => Path.Combine(root, "webview2");
    }
}
