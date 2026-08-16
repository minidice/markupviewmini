using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarkUpViewMini.Core.Search;
using MarkUpViewMini.PerformanceTests.Support;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MarkUpViewMini.PerformanceTests
{

public sealed class ReaderPerformanceTests
{
    private static readonly TimeSpan ReaderThreshold = TimeSpan.FromMilliseconds(2_000);

    [PerformanceFact]
    public async Task Process_start_to_production_document_rendered_stays_under_two_seconds()
    {
        // Break caught: startup, file loading, WebView initialization, or the production render pipeline regresses past the release budget.
        using var fixture = PerformanceFixture.CreateReader();

        var elapsed = await ReaderProbe.MeasureProcessStartToRenderedAsync(
            fixture.ReaderDocumentPath,
            CancellationToken.None);

        var effectiveThreshold = PerformanceThreshold.Effective(ReaderThreshold);
        Assert.True(
            elapsed < effectiveThreshold,
            $"Reader took {elapsed.TotalMilliseconds:F3} ms; threshold is {effectiveThreshold.TotalMilliseconds:F0} ms.");
        PerformanceResultWriter.Write(
            "reader",
            fixture.ReaderFixtureSha256,
            elapsed,
            ReaderThreshold);
    }
}

public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MARKUPVIEWMINI_RUN_PERF"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set MARKUPVIEWMINI_RUN_PERF=1 to run representative-machine performance gates.";
        }
    }
}

}

namespace MarkUpViewMini.PerformanceTests.Support
{
    internal static class PerformanceThreshold
    {
        public static TimeSpan Effective(TimeSpan exactThreshold) =>
            string.Equals(
                Environment.GetEnvironmentVariable("MARKUPVIEWMINI_PERF_FORCE_THRESHOLD_FAILURE"),
                "1",
                StringComparison.Ordinal)
                ? TimeSpan.Zero
                : exactThreshold;
    }

    internal sealed class PerformanceFixture : IDisposable
    {
        internal const int GeneratorSeed = 20_260_812;
        internal const int SearchFileCount = 1_000;
        internal const int SearchMatchInterval = 10;
        internal const string SearchNeedle = "phase-five-known-match";
        private const int ReaderByteCount = 5 * 1_024 * 1_024;
        private readonly string tempRoot;
        private bool disposed;

        private PerformanceFixture(bool createReader, bool createSearch)
        {
            tempRoot = Path.GetFullPath(Path.GetTempPath());
            RunRoot = Path.GetFullPath(Path.Combine(
                tempRoot,
                $"MarkUpViewMini-Performance-{Guid.NewGuid():N}"));
            if (!IsContained(RunRoot, tempRoot) ||
                !Path.GetFileName(RunRoot).StartsWith("MarkUpViewMini-Performance-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The controlled performance root is invalid.");
            }

            Directory.CreateDirectory(RunRoot);
            if ((File.GetAttributes(RunRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The controlled performance root is a reparse point.");
            }

            if (createReader)
            {
                ReaderDocumentPath = Path.Combine(RunRoot, "reader-5mib.md");
                CreateReaderDocument(ReaderDocumentPath);
                ReaderFixtureSha256 = HashFile(ReaderDocumentPath);
            }

            if (createSearch)
            {
                SearchRoot = Path.Combine(RunRoot, "search-corpus");
                SearchFixtureSha256 = CreateSearchFiles(SearchRoot);
            }
        }

        public string RunRoot { get; }
        public string ReaderDocumentPath { get; } = string.Empty;
        public string ReaderFixtureSha256 { get; } = string.Empty;
        public string SearchRoot { get; } = string.Empty;
        public string SearchFixtureSha256 { get; } = string.Empty;

        public static PerformanceFixture CreateReader() => new(createReader: true, createSearch: false);
        public static PerformanceFixture CreateSearchCorpus() => new(createReader: false, createSearch: true);

        public SearchQuery CreateBodyQuery() => new(
            Guid.NewGuid(),
            SearchRoot,
            SearchNeedle,
            SearchMode.Body,
            MatchCase: true,
            WholeWord: true,
            UseRegex: false,
            Extensions: new HashSet<string>([".md"], StringComparer.OrdinalIgnoreCase),
            MaxBodyBytes: 1_024 * 1_024);

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (Directory.Exists(RunRoot))
            {
                if (!IsContained(RunRoot, tempRoot) ||
                    (File.GetAttributes(RunRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Refusing to clean an uncontrolled performance root.");
                }

                for (var attempt = 0; attempt < 20 && Directory.Exists(RunRoot); attempt++)
                {
                    try
                    {
                        Directory.Delete(RunRoot, recursive: true);
                    }
                    catch (IOException) when (attempt < 19)
                    {
                        Thread.Sleep(250);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 19)
                    {
                        Thread.Sleep(250);
                    }
                }

                if (Directory.Exists(RunRoot))
                {
                    throw new IOException("The controlled performance fixture directory could not be removed.");
                }
            }
        }

        internal static bool IsContained(string path, string parent)
        {
            var fullPath = Path.GetFullPath(path);
            var fullParent = Path.GetFullPath(parent).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var relative = Path.GetRelativePath(fullParent, fullPath);
            return !Path.IsPathRooted(relative) &&
                !string.Equals(relative, "..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }

        private static void CreateReaderDocument(string path)
        {
            var builder = new StringBuilder(ReaderByteCount);
            builder.Append("# Five MiB reader fixture\n\n");
            builder.Append("Seeded performance paragraph for the production Markdown reader.\n\n");
            builder.Append("Inline KaTeX: $E = mc^2$ and display math: $$\\int_0^1 x^2 dx$$.\n\n");
            builder.Append("```mermaid\nflowchart LR\nA[Seed 20260812] --> B[Production render]\n```\n\n");
            builder.Append("## Deterministic payload\n\n");
            var generatorState = (uint)GeneratorSeed;
            var paragraph = 0;
            while (builder.Length < ReaderByteCount)
            {
                generatorState = unchecked((generatorState * 1_664_525) + 1_013_904_223);
                var line = $"Paragraph {paragraph:D5} token {generatorState:X8} preserves deterministic UTF-8 reader load. ";
                var remaining = ReaderByteCount - builder.Length;
                if (line.Length + 2 > remaining)
                {
                    builder.Append('x', remaining);
                    break;
                }

                builder.Append(line);
                builder.Append('p', Math.Min(4_000, ReaderByteCount - builder.Length - 2));
                builder.Append("\n\n");
                paragraph++;
            }

            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            if (bytes.Length != ReaderByteCount)
            {
                throw new InvalidOperationException("The reader fixture is not exactly 5 MiB.");
            }

            File.WriteAllBytes(path, bytes);
        }

        private static string CreateSearchFiles(string root)
        {
            Directory.CreateDirectory(root);
            using var corpusHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var index = 0; index < SearchFileCount; index++)
            {
                var name = $"document-{index:D4}.md";
                var marker = index % SearchMatchInterval == 0
                    ? $"Distribution marker: {SearchNeedle} bucket {index / SearchMatchInterval:D3}."
                    : $"Distribution marker: control-only bucket {index:D4}.";
                var text = $"# Document {index:D4}\n\nSeed: {GeneratorSeed}.\n\n{marker}\n\nDeterministic searchable body.\n";
                var nameBytes = Encoding.UTF8.GetBytes(name);
                var contentBytes = Encoding.UTF8.GetBytes(text);
                corpusHash.AppendData(nameBytes);
                corpusHash.AppendData([0]);
                corpusHash.AppendData(contentBytes);
                File.WriteAllBytes(Path.Combine(root, name), contentBytes);
            }

            return Convert.ToHexString(corpusHash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }

    internal static class ReaderProbe
    {
        public static async Task<TimeSpan> MeasureProcessStartToRenderedAsync(
            string documentPath,
            CancellationToken cancellationToken)
        {
            var configuredApplicationRoot = Environment.GetEnvironmentVariable(
                "MARKUPVIEWMINI_PERF_APP_DIR");
            if (string.IsNullOrWhiteSpace(configuredApplicationRoot))
            {
                throw new InvalidOperationException(
                    "MARKUPVIEWMINI_PERF_APP_DIR must identify the controlled production publish.");
            }

            var applicationRoot = Path.GetFullPath(configuredApplicationRoot);
            var dataRoot = Path.Combine(applicationRoot, "data");
            var executable = Path.Combine(applicationRoot, "MarkUpViewMini.App.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("The production application executable is unavailable.", executable);
            }

            if (!File.Exists(Path.Combine(applicationRoot, "web", "document-surface", "dist", "editor.js")))
            {
                throw new FileNotFoundException("The production document surface assets are unavailable.");
            }

            if (Process.GetProcessesByName("MarkUpViewMini.App").Length != 0)
            {
                throw new InvalidOperationException("Close existing MarkUpViewMini.App processes before running the reader gate.");
            }

            if (Directory.Exists(dataRoot))
            {
                throw new InvalidOperationException("The controlled performance app data directory is not clean.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = applicationRoot,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            };
            startInfo.ArgumentList.Add(documentPath);
            var pipeName = $"MarkUpViewMini.Performance.{Guid.NewGuid():N}";
            await using var rendered = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var process = new Process { StartInfo = startInfo };
            var stopwatch = new Stopwatch();

            var previousPipe = Environment.GetEnvironmentVariable("MARKUPVIEWMINI_PERF_RENDER_PIPE");
            try
            {
                Environment.SetEnvironmentVariable("MARKUPVIEWMINI_PERF_RENDER_PIPE", pipeName);
                stopwatch.Start();
                if (!process.Start())
                {
                    throw new InvalidOperationException("The reader probe process did not start.");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("MARKUPVIEWMINI_PERF_RENDER_PIPE", previousPipe);
            }

            try
            {
                var connected = rendered.WaitForConnectionAsync(cancellationToken);
                var exited = process.WaitForExitAsync(cancellationToken);
                var timedOut = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                var completed = await Task.WhenAny(connected, exited, timedOut);
                if (ReferenceEquals(completed, exited))
                {
                    throw new InvalidOperationException(
                        $"The production application exited before document.rendered with code {process.ExitCode}.");
                }

                if (!ReferenceEquals(completed, connected))
                {
                    throw new TimeoutException(
                        "The production application did not emit document.rendered within 30 seconds.");
                }

                await connected;
                var signal = new byte[1];
                await rendered.ReadExactlyAsync(signal, cancellationToken);
                var elapsed = stopwatch.Elapsed;
                if (signal[0] != 1)
                {
                    throw new InvalidDataException("The production render signal was invalid.");
                }

                return elapsed;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }

                    await process.WaitForExitAsync(CancellationToken.None);
                }

                for (var attempt = 0; attempt < 20 && Directory.Exists(dataRoot); attempt++)
                {
                    try
                    {
                        if ((File.GetAttributes(dataRoot) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidOperationException("The controlled performance app data is a reparse point.");
                        }

                        Directory.Delete(dataRoot, recursive: true);
                    }
                    catch (IOException) when (attempt < 19)
                    {
                        Thread.Sleep(250);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 19)
                    {
                        Thread.Sleep(250);
                    }
                }

                if (Directory.Exists(dataRoot))
                {
                    throw new IOException("The controlled performance app data directory could not be removed.");
                }
            }
        }
    }

    internal static class PerformanceResultWriter
    {
        public static void Write(
            string metric,
            string fixtureSha256,
            TimeSpan elapsed,
            TimeSpan threshold)
        {
            var resultDirectory = Environment.GetEnvironmentVariable("MARKUPVIEWMINI_PERF_RESULT_DIR");
            if (string.IsNullOrWhiteSpace(resultDirectory))
            {
                return;
            }

            var fullDirectory = Path.GetFullPath(resultDirectory);
            Directory.CreateDirectory(fullDirectory);
            var result = new
            {
                metric,
                fixtureSha256,
                elapsedMilliseconds = elapsed.TotalMilliseconds,
                thresholdMilliseconds = threshold.TotalMilliseconds,
                passed = elapsed < threshold,
            };
            File.WriteAllText(
                Path.Combine(fullDirectory, $"{metric}.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
