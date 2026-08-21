using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarkUpViewMini.App.Web;

namespace MarkUpViewMini.App.Tests;

public sealed partial class OfflineAssetTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentSourcePath())!, "..", ".."));
    private static readonly string PublishDirectory = Path.Combine(
        RepositoryRoot,
        "artifacts",
        "portable",
        "MarkUpViewMini");
    private static readonly string MsixPackagePath = Path.Combine(
        RepositoryRoot,
        "artifacts",
        "msix",
        "MarkUpViewMini-win-x64.msix");

    [Fact]
    public void Msix_package_contains_the_document_surface_startup_assets()
    {
        Assert.True(
            File.Exists(MsixPackagePath),
            "Run scripts/publish-msix.ps1 before the MSIX package audit.");

        using var archive = ZipFile.OpenRead(MsixPackagePath);
        string[] requiredEntries =
        [
            "MarkUpViewMini.App/web/document-surface/index.html",
            "MarkUpViewMini.App/web/document-surface/dist/editor.js",
            "MarkUpViewMini.App/web/document-surface/dist/editor.css",
            "MarkUpViewMini.App/web/document-surface/dist/runtime-components.json",
            "MarkUpViewMini.App/web/mermaid-editor/index.html",
            "MarkUpViewMini.App/web/mermaid-editor/dist/editor.js",
            "MarkUpViewMini.App/web/mermaid-editor/dist/editor.css",
            "MarkUpViewMini.App/web/mermaid-editor/dist/runtime-components.json",
        ];

        foreach (var requiredEntry in requiredEntries)
        {
            Assert.Contains(
                archive.Entries,
                entry => string.Equals(entry.FullName, requiredEntry, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Portable_publish_contains_the_root_mit_license()
    {
        if (SkipPackageAuditDuringPrePublishTests())
        {
            return;
        }

        RequirePublishDirectory();

        var path = Path.Combine(PublishDirectory, "LICENSE");
        Assert.True(File.Exists(path));
        Assert.Contains("Copyright (c) 2026 MiniDice", File.ReadAllText(path));
    }

    [Fact]
    public void Portable_publish_embeds_readable_about_content_without_network_access()
    {
        if (SkipPackageAuditDuringPrePublishTests())
        {
            return;
        }

        RequirePublishDirectory();

        var applicationPath = Path.Combine(PublishDirectory, "MarkUpViewMini.App.dll");
        var notices = ReadEmbeddedResource(
            applicationPath,
            "MarkUpViewMini.App.About.Resources.runtime-notices.json");
        var applicationLicense = ReadEmbeddedResource(
            applicationPath,
            "MarkUpViewMini.App.About.Resources.app-license.txt");
        var expectedNotices = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "MarkUpViewMini.App",
            "About",
            "Resources",
            "runtime-notices.json"));
        var expectedApplicationLicense = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "MarkUpViewMini.App",
            "About",
            "Resources",
            "app-license.txt"));
        var rootLicense = File.ReadAllText(Path.Combine(RepositoryRoot, "LICENSE"));
        Assert.Equal(expectedNotices, notices, ignoreLineEndingDifferences: true);
        Assert.Equal(expectedApplicationLicense, applicationLicense, ignoreLineEndingDifferences: true);
        Assert.EndsWith(rootLicense.TrimEnd(), applicationLicense.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("WebView2", notices, StringComparison.Ordinal);
        Assert.Contains("This application is provided free of charge under the MIT License.", applicationLicense);
        Assert.Contains("https://github.com/minidice/markupviewmini", applicationLicense);
    }

    [Fact]
    public void Portable_publish_notices_match_exact_web_and_dotnet_runtime_closure()
    {
        if (SkipPackageAuditDuringPrePublishTests())
        {
            return;
        }

        RequirePublishDirectory();

        var applicationPath = Path.Combine(PublishDirectory, "MarkUpViewMini.App.dll");
        var noticeJson = ReadEmbeddedResource(
            applicationPath,
            "MarkUpViewMini.App.About.Resources.runtime-notices.json");
        using var noticeDocument = JsonDocument.Parse(noticeJson);
        var actual = noticeDocument.RootElement.EnumerateArray()
            .Select(static item => $"{item.GetProperty("name").GetString()}@{item.GetProperty("version").GetString()}")
            .ToHashSet(StringComparer.Ordinal);

        var expected = new HashSet<string>(StringComparer.Ordinal);
        string[] componentManifestPaths =
        [
            Path.Combine(PublishDirectory, "web", "document-surface", "dist", "runtime-components.json"),
            Path.Combine(PublishDirectory, "web", "mermaid-editor", "dist", "runtime-components.json"),
        ];
        foreach (var manifestPath in componentManifestPaths)
        {
            Assert.True(File.Exists(manifestPath), $"The portable bundle is missing {manifestPath}.");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var package in manifest.RootElement.GetProperty("packages").EnumerateArray())
            {
                expected.Add($"{package.GetProperty("name").GetString()}@{package.GetProperty("version").GetString()}");
            }
        }

        var manualSourcePath = Path.Combine(
            RepositoryRoot,
            "src",
            "MarkUpViewMini.App",
            "About",
            "Resources",
            "runtime-notice-sources.json");
        using (var manualSources = JsonDocument.Parse(File.ReadAllText(manualSourcePath)))
        {
            foreach (var component in manualSources.RootElement.EnumerateArray())
            {
                expected.Add($"{component.GetProperty("name").GetString()}@{component.GetProperty("version").GetString()}");
            }
        }

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            PublishDirectory,
            "MarkUpViewMini.App.deps.json")));
        var libraries = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        AssertRuntimeLibraryNotice(
            libraries,
            actual,
            "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/",
            ".NET Runtime (win-x64)");
        AssertRuntimeLibraryNotice(
            libraries,
            actual,
            "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/",
            ".NET Windows Desktop Runtime (win-x64)");
        AssertRuntimeLibraryNotice(
            libraries,
            actual,
            "Microsoft.Web.WebView2/",
            "Microsoft.Web.WebView2");
    }

    [Fact]
    public void Portable_publish_provenance_binds_every_file_and_embedded_revision_to_the_clean_commit()
    {
        if (SkipPackageAuditDuringPrePublishTests())
        {
            return;
        }

        RequirePublishDirectory();
        var provenancePath = Path.Combine(PublishDirectory, "release-provenance.json");
        Assert.True(File.Exists(provenancePath), "The portable publish has no release provenance.");

        var provenance = JsonSerializer.Deserialize<ReleaseProvenance>(
            File.ReadAllText(provenancePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(provenance);
        Assert.Equal(1, provenance.SchemaVersion);
        Assert.Matches("^[0-9a-f]{40}$", provenance.SourceCommit);
        Assert.Matches("^[0-9a-f]{40}$", provenance.SourceTree);
        Assert.Equal(provenance.SourceCommit, RunGit("rev-parse", "HEAD"));
        Assert.Equal(provenance.SourceTree, RunGit("rev-parse", "HEAD^{tree}"));
        var sourceStatus = RunGit("status", "--porcelain", "--untracked-files=all");
        Assert.True(
            string.IsNullOrWhiteSpace(sourceStatus),
            $"Portable provenance requires an exact clean source worktree. Git status: {sourceStatus}");

        var applicationDll = Path.Combine(PublishDirectory, "MarkUpViewMini.App.dll");
        var productVersion = FileVersionInfo.GetVersionInfo(applicationDll).ProductVersion;
        Assert.False(string.IsNullOrWhiteSpace(productVersion));
        Assert.EndsWith($"+{provenance.SourceCommit}", productVersion, StringComparison.Ordinal);
        Assert.Equal(productVersion, provenance.ApplicationProductVersion);

        var actualFiles = Directory.GetFiles(PublishDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(provenancePath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new ReleaseProvenanceFile(
                Path.GetRelativePath(PublishDirectory, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                HashFile(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(actualFiles, provenance.Files);
    }

    [Fact]
    public void Portable_publish_contains_only_complete_runtime_assets()
    {
        if (SkipPackageAuditDuringPrePublishTests())
        {
            return;
        }

        RequirePublishDirectory();

        string[] requiredFiles =
        [
            "MarkUpViewMini.App.exe",
            "portable.marker",
            @"web\document-surface\index.html",
            @"web\document-surface\dist\editor.js",
            @"web\document-surface\dist\editor.css",
            @"web\document-surface\dist\manifest.txt",
            @"web\document-surface\dist\runtime-components.json",
            @"web\mermaid-editor\index.html",
            @"web\mermaid-editor\dist\editor.js",
            @"web\mermaid-editor\dist\editor.css",
            @"web\mermaid-editor\dist\runtime-components.json",
        ];

        foreach (var relativePath in requiredFiles)
        {
            Assert.True(
                File.Exists(Path.Combine(PublishDirectory, relativePath)),
                $"The portable publish is missing '{relativePath}'.");
        }

        var publishedFiles = Directory.GetFiles(PublishDirectory, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(publishedFiles, path =>
            path.EndsWith(".map", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(publishedFiles, path =>
        {
            var relative = Path.GetRelativePath(PublishDirectory, path);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(part => part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                                     part.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                                     part.Equals("tests", StringComparison.OrdinalIgnoreCase)) ||
                Path.GetFileName(relative) is "package.json" or "package-lock.json";
        });
    }

    [Fact]
    public void Every_published_html_css_font_and_module_reference_resolves_locally()
    {
        if (SkipPackageAuditDuringPrePublishTests())
        {
            return;
        }

        RequirePublishDirectory();

        var webRoot = Path.Combine(PublishDirectory, "web");
        var references = new List<(string Source, string Reference)>();
        foreach (var htmlPath in Directory.GetFiles(webRoot, "*.html", SearchOption.AllDirectories))
        {
            var html = File.ReadAllText(htmlPath);
            references.AddRange(HtmlAssetReferenceRegex().Matches(html)
                .Select(match => (htmlPath, match.Groups[1].Value)));
        }

        foreach (var cssPath in Directory.GetFiles(webRoot, "*.css", SearchOption.AllDirectories))
        {
            var css = File.ReadAllText(cssPath);
            references.AddRange(CssAssetReferenceRegex().Matches(css)
                .Select(match => (cssPath, match.Groups[1].Value)));
        }

        foreach (var modulePath in Directory.GetFiles(webRoot, "*.js", SearchOption.AllDirectories))
        {
            var module = File.ReadAllText(modulePath);
            references.AddRange(JavaScriptStaticModuleReferenceRegex().Matches(module)
                .Select(match => (modulePath, match.Groups[1].Value)));
            references.AddRange(JavaScriptDynamicModuleReferenceRegex().Matches(module)
                .Select(match => (modulePath, match.Groups[1].Value)));
        }

        Assert.NotEmpty(references);
        foreach (var (source, reference) in references)
        {
            AssertLocalReferenceResolves(webRoot, source, reference);
        }
    }

    // Excluded from scripts/publish-portable.ps1's OfflineAssetTests filter: on GitHub Actions
    // windows-latest, the published app's WebView2 never opens its CDP debug port within the
    // retry budget (process stays alive, no crash, no output - a runner-environment issue, not
    // a code regression). Run this one manually before tagging a release.
    [Fact]
    public async Task Published_surfaces_render_in_production_WebViews_without_network_and_route_external_clicks()
    {
        if (SkipPackageAuditDuringPrePublishTests())
        {
            return;
        }

        RequirePublishDirectory();

        var external = new Uri("https://example.invalid/outside");
        Assert.False(WebViewPolicy.IsAllowedTopLevelNavigation(external));
        Assert.False(WebViewPolicy.IsAllowedDocumentAssetUri(external));
        Assert.True(WebViewPolicy.IsAllowedTopLevelNavigation(
            WebViewPolicy.BuildBootstrapUri(Guid.NewGuid(), Guid.NewGuid())));
        Assert.True(WebViewPolicy.IsAllowedDocumentAssetUri(
            new Uri("https://document-assets.local/image.png")));

        Assert.Empty(Process.GetProcessesByName("MarkUpViewMini.App"));
        Assert.False(
            Directory.Exists(Path.Combine(PublishDirectory, "data")),
            "The production WebView probe requires a clean portable package.");

        var runRoot = Path.Combine(
            Path.GetTempPath(),
            $"MarkUpViewMini-ProductionWebView-{Guid.NewGuid():N}");
        var isolatedPublish = Path.Combine(runRoot, "app");
        var mermaidProbeRoot = Path.Combine(runRoot, "mermaid-probe");
        var scriptPath = Path.Combine(runRoot, "probe.mjs");
        var fixturePath = Path.Combine(runRoot, "offline-probe.md");
        Process? application = null;
        Process? mermaidApplication = null;
        try
        {
            CopyDirectoryWithoutLinks(PublishDirectory, isolatedPublish);
            var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))!.Name;
            var probeOutput = Path.Combine(
                RepositoryRoot,
                "tests",
                "MarkUpViewMini.WebViewProbe",
                "bin",
                configuration,
                "net10.0-windows");
            CopyDirectoryWithoutLinks(probeOutput, mermaidProbeRoot);
            var probeWeb = Path.Combine(mermaidProbeRoot, "web");
            if (Directory.Exists(probeWeb))
            {
                Directory.Delete(probeWeb, recursive: true);
            }
            CopyDirectoryWithoutLinks(Path.Combine(isolatedPublish, "web"), probeWeb);
            foreach (var productAssembly in new[]
                     {
                         "MarkUpViewMini.App.dll",
                         "MarkUpViewMini.Core.dll",
                         "MarkUpViewMini.Infrastructure.dll",
                     })
            {
                File.Copy(
                    Path.Combine(isolatedPublish, productAssembly),
                    Path.Combine(mermaidProbeRoot, productAssembly),
                    overwrite: true);
            }
            File.WriteAllText(scriptPath, BrowserProbeScript, new UTF8Encoding(false));
            File.WriteAllText(
                fixturePath,
                "# Offline production probe\n\n" +
                "[external](https://offline-probe.invalid/outside)\n\n" +
                "```mermaid\nflowchart LR\nA --> B\n```\n",
                new UTF8Encoding(false));

            var debuggingPort = GetAvailableLoopbackPort();
            var applicationStart = new ProcessStartInfo(
                Path.Combine(isolatedPublish, "MarkUpViewMini.App.exe"))
            {
                WorkingDirectory = isolatedPublish,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            applicationStart.ArgumentList.Add(fixturePath);
            applicationStart.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] =
                $"--remote-debugging-port={debuggingPort} --remote-allow-origins=*";
            application = Process.Start(applicationStart) ??
                throw new InvalidOperationException("The isolated published application did not start.");
            var applicationOutput = new StringBuilder();
            application.OutputDataReceived += (_, e) => { if (e.Data is not null) applicationOutput.AppendLine(e.Data); };
            application.ErrorDataReceived += (_, e) => { if (e.Data is not null) applicationOutput.AppendLine(e.Data); };
            application.BeginOutputReadLine();
            application.BeginErrorReadLine();

            var mermaidDebuggingPort = GetAvailableLoopbackPort();
            var mermaidStart = new ProcessStartInfo(
                Path.Combine(mermaidProbeRoot, "MarkUpViewMini.WebViewProbe.exe"))
            {
                WorkingDirectory = mermaidProbeRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            mermaidStart.ArgumentList.Add(Path.Combine(mermaidProbeRoot, "data"));
            mermaidStart.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] =
                $"--remote-debugging-port={mermaidDebuggingPort} --remote-allow-origins=*";
            mermaidApplication = Process.Start(mermaidStart) ??
                throw new InvalidOperationException("The production Mermaid WebView probe did not start.");
            var mermaidOutput = new StringBuilder();
            mermaidApplication.OutputDataReceived += (_, e) => { if (e.Data is not null) mermaidOutput.AppendLine(e.Data); };
            mermaidApplication.ErrorDataReceived += (_, e) => { if (e.Data is not null) mermaidOutput.AppendLine(e.Data); };
            mermaidApplication.BeginOutputReadLine();
            mermaidApplication.BeginErrorReadLine();

            var startInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = runRoot,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(debuggingPort.ToString());
            startInfo.ArgumentList.Add(mermaidDebuggingPort.ToString());

            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("The offline browser probe did not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The offline browser probe timed out.");
            }

            var output = await standardOutput;
            var error = await standardError;
            var logsDirectory = Path.Combine(isolatedPublish, "data", "logs");
            var logs = Directory.Exists(logsDirectory)
                ? string.Join(Environment.NewLine, Directory.GetFiles(logsDirectory).Select(File.ReadAllText))
                : string.Empty;
            var applicationStatus = application.HasExited
                ? $"exited with code {application.ExitCode}"
                : "still running";
            var mermaidStatus = mermaidApplication.HasExited
                ? $"exited with code {mermaidApplication.ExitCode}"
                : "still running";
            Assert.True(
                process.ExitCode == 0,
                $"The offline browser probe failed.{Environment.NewLine}{output}{Environment.NewLine}{error}{Environment.NewLine}{logs}" +
                $"{Environment.NewLine}application ({applicationStatus}):{Environment.NewLine}{applicationOutput}" +
                $"{Environment.NewLine}mermaid probe ({mermaidStatus}):{Environment.NewLine}{mermaidOutput}");
            Assert.Contains("production-webview2-offline-ok", output, StringComparison.Ordinal);
            Assert.Contains("documentRequests=", output, StringComparison.Ordinal);
            Assert.Contains("mermaidRequests=", output, StringComparison.Ordinal);
        }
        finally
        {
            if (mermaidApplication is not null)
            {
                try
                {
                    if (!mermaidApplication.HasExited)
                    {
                        mermaidApplication.Kill(entireProcessTree: true);
                        mermaidApplication.WaitForExit(10_000);
                    }
                }
                finally
                {
                    mermaidApplication.Dispose();
                }
            }

            if (application is not null)
            {
                try
                {
                    if (!application.HasExited)
                    {
                        application.Kill(entireProcessTree: true);
                        application.WaitForExit(10_000);
                    }
                }
                finally
                {
                    application.Dispose();
                }
            }

            DeleteOwnedDirectoryWithRetries(runRoot);
        }
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void CopyDirectoryWithoutLinks(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos(
                     "*",
                     SearchOption.AllDirectories))
        {
            Assert.False(
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
                $"The publish contains a reparse point: {entry.FullName}");
            var relative = Path.GetRelativePath(source, entry.FullName);
            var target = Path.Combine(destination, relative);
            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry.FullName, target, overwrite: false);
            }
        }
    }

    private static void DeleteOwnedDirectoryWithRetries(string directory)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
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

        Assert.False(Directory.Exists(directory), "The production WebView probe left its isolated run directory.");
    }

    private static string ReadEmbeddedResource(string assemblyPath, string resourceName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!string.Equals(metadata.GetString(resource.Name), resourceName, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(resource.Implementation.IsNil, $"'{resourceName}' is not embedded in the portable app.");
            var corHeader = peReader.PEHeaders.CorHeader ??
                throw new InvalidDataException("The portable app has no CLR header.");
            var resourcesDirectory = corHeader.ResourcesDirectory;
            var resourceSection = peReader.GetSectionData(resourcesDirectory.RelativeVirtualAddress);
            var resourceReader = resourceSection.GetReader(
                (int)resource.Offset,
                resourceSection.Length - (int)resource.Offset);
            return Encoding.UTF8.GetString(resourceReader.ReadBytes(resourceReader.ReadInt32()));
        }

        Assert.Fail($"The portable app is missing embedded resource '{resourceName}'.");
        return string.Empty;
    }

    private static void AssertRuntimeLibraryNotice(
        IEnumerable<string> libraries,
        IReadOnlySet<string> notices,
        string libraryPrefix,
        string noticeName)
    {
        var library = Assert.Single(libraries, item => item.StartsWith(libraryPrefix, StringComparison.Ordinal));
        var version = library[libraryPrefix.Length..];
        Assert.Contains($"{noticeName}@{version}", notices);
    }

    private static void RequirePublishDirectory() =>
        Assert.True(
            Directory.Exists(PublishDirectory),
            "Run scripts/publish-portable.ps1 before the offline package audit.");

    private static string RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Git did not start for the provenance audit.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Git provenance query failed: {error}");
        return output.Trim();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool SkipPackageAuditDuringPrePublishTests() =>
        string.Equals(
            Environment.GetEnvironmentVariable("MARKUPVIEWMINI_PREPUBLISH_TESTS"),
            "1",
            StringComparison.Ordinal) &&
        !string.Equals(
            Environment.GetEnvironmentVariable("MARKUPVIEWMINI_PACKAGE_AUDIT_REQUIRED"),
            "1",
            StringComparison.Ordinal);

    private sealed record ReleaseProvenance(
        int SchemaVersion,
        string SourceCommit,
        string SourceTree,
        string ApplicationProductVersion,
        ReleaseProvenanceFile[] Files);

    private sealed record ReleaseProvenanceFile(string Path, long Length, string Sha256);

    private static void AssertLocalReferenceResolves(
        string webRoot,
        string source,
        string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith('#'))
        {
            return;
        }

        Assert.False(
            Uri.TryCreate(reference, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https",
            $"Network asset reference '{reference}' was found in '{Path.GetRelativePath(webRoot, source)}'.");

        var cleanReference = reference.Split('?', '#')[0];
        var candidate = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(source)!, cleanReference.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(webRoot, candidate);
        Assert.False(
            Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            $"Asset reference '{reference}' escapes the published web root.");
        Assert.True(
            File.Exists(candidate),
            $"Asset reference '{reference}' from '{Path.GetRelativePath(webRoot, source)}' is missing.");
    }

    private static string CurrentSourcePath([CallerFilePath] string sourcePath = "") => sourcePath;

    [GeneratedRegex("<(?:script|link)\\b[^>]*?\\b(?:src|href)\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAssetReferenceRegex();

    [GeneratedRegex("(?:url\\(\\s*|@import\\s+)[\"']?([^\"')\\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CssAssetReferenceRegex();

    [GeneratedRegex("(?:^|[;\\r\\n])\\s*(?:import\\s+(?:[^\"'();]*?\\s+from\\s*)?|export\\s+[^\"'();]*?\\s+from\\s*)[\"']([^\"']+)[\"']", RegexOptions.Multiline)]
    private static partial Regex JavaScriptStaticModuleReferenceRegex();

    [GeneratedRegex("(?<![\\w$])import\\s*\\(\\s*[\"']([^\"']+)[\"']")]
    private static partial Regex JavaScriptDynamicModuleReferenceRegex();

    private const string BrowserProbeScript = """
        const documentEndpoint = `http://127.0.0.1:${Number(process.argv[2])}`;
        const mermaidEndpoint = `http://127.0.0.1:${Number(process.argv[3])}`;
        const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

        async function eventually(action, description, timeout = 30000) {
          const deadline = Date.now() + timeout;
          let last;
          while (Date.now() < deadline) {
            try {
              const value = await action();
              if (value) return value;
            } catch (error) { last = error; }
            await sleep(100);
          }
          throw new Error(`${description} timed out${last ? `: ${last.message}` : ""}`);
        }

        async function targets(endpoint) {
          return await (await fetch(`${endpoint}/json/list`)).json();
        }

        class Cdp {
          constructor(socket) {
            this.socket = socket;
            this.sequence = 0;
            this.pending = new Map();
            this.events = [];
            socket.onmessage = event => {
              const message = JSON.parse(event.data);
              if (message.id) {
                const pending = this.pending.get(message.id);
                if (!pending) return;
                this.pending.delete(message.id);
                if (message.error) pending.reject(new Error(message.error.message));
                else pending.resolve(message.result);
              } else {
                this.events.push(message);
              }
            };
          }

          static async connect(target) {
            const socket = new WebSocket(target.webSocketDebuggerUrl);
            await new Promise((resolve, reject) => {
              socket.onopen = resolve;
              socket.onerror = () => reject(new Error(`CDP socket failed for ${target.url}`));
            });
            return new Cdp(socket);
          }

          send(method, params = {}) {
            const id = ++this.sequence;
            return new Promise((resolve, reject) => {
              this.pending.set(id, { resolve, reject });
              this.socket.send(JSON.stringify({ id, method, params }));
            });
          }

          async evaluate(expression) {
            const result = await this.send("Runtime.evaluate", {
              expression,
              awaitPromise: true,
              returnByValue: true,
            });
            if (result.exceptionDetails) {
              throw new Error(result.exceptionDetails.exception?.description ?? "Runtime evaluation failed");
            }
            return result.result.value;
          }

          close() { this.socket.close(); }
        }

        async function attach(host, endpoint) {
          let target;
          try {
            target = await eventually(async () =>
              (await targets(endpoint)).find(candidate => candidate.type === "page" && new URL(candidate.url).hostname === host),
              `${host} production WebView target`,
              60000);
          } catch (error) {
            let observed = "unavailable";
            try { observed = JSON.stringify((await targets(endpoint)).map(candidate => ({ type: candidate.type, url: candidate.url }))); }
            catch (observeError) { observed = `unavailable: ${observeError.message}`; }
            throw new Error(`${error.message}; observed targets=${observed}`);
          }
          const cdp = await Cdp.connect(target);
          await cdp.send("Runtime.enable");
          await cdp.send("Page.enable");
          await cdp.send("Network.enable");
          await cdp.send("Network.setCacheDisabled", { cacheDisabled: true });
          return cdp;
        }

        const parsedReferenceExpression = `(() => {
          const result = [];
          const push = (kind, value, element = "") => {
            if (typeof value === "string" && value.trim()) result.push({ kind, value, element });
          };
          const resourceAttributes = ["src", "poster", "data", "action", "formaction"];
          for (const element of document.querySelectorAll("*")) {
            for (const attribute of resourceAttributes) {
              if (element.hasAttribute(attribute)) push(attribute, element[attribute] || element.getAttribute(attribute), element.tagName);
            }
            if (element.hasAttribute("href")) push(element.tagName === "A" ? "navigation" : "href", element.href || element.getAttribute("href"), element.tagName);
            if (element.hasAttribute("srcset")) {
              for (const candidate of element.srcset.split(",")) push("srcset", new URL(candidate.trim().split(/\\s+/)[0], document.baseURI).href, element.tagName);
            }
          }
          const visitRules = rules => {
            for (const rule of rules || []) {
              if (rule.href) push("css-import", rule.href, "CSS");
              if (rule.style) {
                for (const property of rule.style) {
                  const value = rule.style.getPropertyValue(property);
                  for (const match of value.matchAll(/url\\(\\s*["']?([^"')]+)["']?\\s*\\)/gi)) push("css-url", new URL(match[1], document.baseURI).href, "CSS");
                }
              }
              if (rule.cssRules) visitRules(rule.cssRules);
            }
          };
          for (const sheet of document.styleSheets) {
            try { visitRules(sheet.cssRules); } catch {}
          }
          for (const entry of performance.getEntriesByType("resource")) push("performance", entry.name, "PERFORMANCE");
          return result;
        })()`;

        function assertLocalReferences(name, references, allowedHosts) {
          if (!references.some(reference => reference.kind === "performance" && reference.value.includes("/dist/editor.js"))) {
            throw new Error(`${name} did not load its published editor.js`);
          }
          if (!references.some(reference => reference.kind === "performance" && reference.value.includes("/dist/editor.css"))) {
            throw new Error(`${name} did not load its published editor.css`);
          }
          for (const reference of references) {
            let uri;
            try { uri = new URL(reference.value); } catch { continue; }
            if (uri.protocol !== "http:" && uri.protocol !== "https:") continue;
            if (reference.kind === "navigation") continue;
            if (!allowedHosts.has(uri.hostname)) {
              throw new Error(`${name} parsed external ${reference.kind} reference: ${uri.href}`);
            }
          }
        }

        async function runBlockedContexts(cdp, allowedHosts) {
          const eventIndex = cdp.events.length;
          const outcomes = await cdp.evaluate(`(async () => {
            const external = "https://offline-probe.invalid/resource";
            const results = {};
            results.newUrl = new URL(external, document.baseURI).href;
            try { await fetch(external); results.fetch = "resolved"; } catch { results.fetch = "blocked"; }
            const waitElement = (element, property) => new Promise(resolve => {
              element.onload = () => resolve("loaded");
              element.onerror = () => resolve("blocked");
              document.body.append(element);
              setTimeout(() => resolve("blocked"), 750);
            });
            const image = new Image(); image.src = external + ".png";
            results.image = await waitElement(image);
            const source = document.createElement("source"); source.srcset = external + "-srcset.png 1x";
            const picture = document.createElement("picture"); picture.append(source);
            const srcsetImage = new Image(); srcsetImage.src = "data:image/gif;base64,R0lGODlhAQABAAAAACw="; picture.append(srcsetImage);
            results.srcset = await waitElement(picture);
            try {
              const worker = new Worker(external + ".js");
              results.worker = await new Promise(resolve => {
                worker.onmessage = () => resolve("loaded");
                worker.onerror = () => resolve("blocked");
                setTimeout(() => resolve("blocked"), 750);
              });
              worker.terminate();
            } catch { results.worker = "blocked"; }
            const script = document.createElement("script"); script.src = external + "-script.js";
            results.script = await waitElement(script);
            const frame = document.createElement("iframe"); frame.src = external + "-frame.html";
            results.frame = await waitElement(frame);
            return results;
          })()`);
          await sleep(300);
          for (const [kind, outcome] of Object.entries(outcomes)) {
            if (kind !== "newUrl" && kind !== "frame" && outcome !== "blocked") throw new Error(`${kind} escaped offline policy: ${outcome}`);
          }
          const observedEvents = cdp.events.slice(eventIndex);
          const externalRequests = observedEvents
            .filter(event => event.method === "Network.requestWillBeSent")
            .filter(event => /^https?:/i.test(event.params.request.url) && new URL(event.params.request.url).hostname === "offline-probe.invalid");
          for (const request of externalRequests) {
            const response = observedEvents.find(event => event.method === "Network.responseReceived" && event.params.requestId === request.params.requestId);
            const failed = observedEvents.find(event => event.method === "Network.loadingFailed" && event.params.requestId === request.params.requestId);
            if (response || !failed) throw new Error(`external resource reached a response: ${request.params.request.url}`);
          }
          const unexpectedResponse = observedEvents.find(event => {
            if (event.method !== "Network.responseReceived") return false;
            const uri = new URL(event.params.response.url);
            return (uri.protocol === "http:" || uri.protocol === "https:") && !allowedHosts.has(uri.hostname);
          });
          if (unexpectedResponse) throw new Error(`non-local response observed: ${unexpectedResponse.params.response.url}`);
        }

        let documentView;
        let mermaidView;
        try {
          documentView = await attach("app.markupviewmini.local", documentEndpoint);
          await eventually(() => documentView.evaluate(`document.querySelector("a[href='https://offline-probe.invalid/outside']") && document.querySelector("[data-mermaid-edit-action]")`), "activated document surface");
          const documentReferences = await documentView.evaluate(parsedReferenceExpression);
          assertLocalReferences("document", documentReferences, new Set(["app.markupviewmini.local", "document-assets.local"]));

          await documentView.evaluate(`(() => {
            window.__offlineProbePosts = [];
            const original = chrome.webview.postMessage.bind(chrome.webview);
            window.__offlineProbeOriginalPost = original;
            chrome.webview.postMessage = message => {
              window.__offlineProbePosts.push(message);
              if (message?.type !== "link.open") original(message);
            };
          })()`);
          const routeEventIndex = documentView.events.length;
          const routed = await documentView.evaluate(`(() => {
            const anchor = document.querySelector("a[href='https://offline-probe.invalid/outside']");
            const click = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
            const dispatchResult = anchor.dispatchEvent(click);
            const message = window.__offlineProbePosts.find(candidate => candidate?.type === "link.open");
            return { prevented: !dispatchResult && click.defaultPrevented, href: message?.payload?.href ?? null };
          })()`);
          if (!routed.prevented || routed.href !== "https://offline-probe.invalid/outside") throw new Error("external click was not routed before navigation");
          await sleep(300);
          const routedNavigation = documentView.events.slice(routeEventIndex).find(event =>
            (event.method === "Page.frameRequestedNavigation" || event.method === "Page.windowOpen") &&
            JSON.stringify(event.params).includes("offline-probe.invalid"));
          if (routedNavigation) throw new Error("external click started navigation before routing");

          mermaidView = await attach("mermaid-editor.local", mermaidEndpoint);
          await eventually(() => mermaidView.evaluate(`document.querySelector("#mermaid-app") && document.querySelector("[data-source]")?.value.includes("flowchart")`), "production Mermaid editor");
          const mermaidReferences = await mermaidView.evaluate(parsedReferenceExpression);
          assertLocalReferences("mermaid", mermaidReferences, new Set(["mermaid-editor.local"]));
          await runBlockedContexts(mermaidView, new Set(["mermaid-editor.local"]));
          await mermaidView.evaluate(`document.querySelector("[data-cancel]").click()`);

          const blockedNavigationIndex = documentView.events.length;
          try { await documentView.send("Page.navigate", { url: "https://offline-probe.invalid/navigation" }); } catch {}
          await sleep(500);
          const currentDocumentUrl = await documentView.evaluate("location.href");
          if (new URL(currentDocumentUrl).hostname !== "app.markupviewmini.local") throw new Error("production navigation handler allowed an external top-level URI");
          const navigationAttempt = documentView.events.slice(blockedNavigationIndex).find(event =>
            event.method === "Network.requestWillBeSent" && new URL(event.params.request.url).hostname === "offline-probe.invalid");
          const navigationResponse = documentView.events.slice(blockedNavigationIndex).find(event =>
            event.method === "Network.responseReceived" && navigationAttempt && event.params.requestId === navigationAttempt.params.requestId);
          const navigationFailure = documentView.events.slice(blockedNavigationIndex).find(event =>
            event.method === "Network.loadingFailed" && navigationAttempt && event.params.requestId === navigationAttempt.params.requestId);
          if (navigationResponse || (navigationAttempt && !navigationFailure)) throw new Error("blocked top-level navigation reached a response");
          await runBlockedContexts(documentView, new Set(["app.markupviewmini.local", "document-assets.local"]));

          const documentRequests = new Set(documentReferences.filter(reference => reference.kind === "performance").map(reference => reference.value)).size;
          const mermaidRequests = new Set(mermaidReferences.filter(reference => reference.kind === "performance").map(reference => reference.value)).size;
          console.log(`production-webview2-offline-ok documentRequests=${documentRequests} mermaidRequests=${mermaidRequests} routed=true`);
        } finally {
          mermaidView?.close();
          documentView?.close();
        }
        """;
}
