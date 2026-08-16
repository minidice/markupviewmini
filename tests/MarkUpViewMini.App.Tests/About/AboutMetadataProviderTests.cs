using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using MarkUpViewMini.App.About;

namespace MarkUpViewMini.App.Tests.About;

public sealed class AboutMetadataProviderTests
{
    [Fact]
    public void Application_license_content_preserves_the_approved_English_copy_and_three_links()
    {
        var content = CreateProvider().GetContent(AboutDialogKind.ApplicationLicense);

        Assert.Contains("Copyright © 2026 MiniDice", content.Body, StringComparison.Ordinal);
        Assert.Contains("This application is provided free of charge under the MIT License.", content.Body);
        Assert.Contains("Personal, business, and commercial use are permitted.", content.Body);
        Assert.Equal(
            ["https://ministool.com/", "https://mdvm.ministool.com/", "https://github.com/minidice/markupviewmini"],
            content.ClickableLinks.Select(static uri => uri.AbsoluteUri));
    }

    [Fact]
    public void Version_content_reports_current_dotnet_framework_and_only_runtime_notices()
    {
        var content = CreateProvider().GetContent(AboutDialogKind.Version);

        Assert.Contains(RuntimeInformation.FrameworkDescription, content.Body, StringComparison.Ordinal);
        Assert.Contains(Environment.Version.ToString(), content.Body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(content.DiagnosticsText));
        Assert.Contains(RuntimeInformation.OSDescription, content.DiagnosticsText, StringComparison.Ordinal);
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString(), content.DiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2", content.DiagnosticsText, StringComparison.Ordinal);
        Assert.Contains(content.Components, item => item.Name == "Microsoft.Web.WebView2");
        Assert.DoesNotContain(content.Components, item => item.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Runtime_notices_match_built_bundle_and_manual_runtime_closure_with_real_license_text()
    {
        var repositoryRoot = GetRepositoryRoot();
        var expected = ReadBundleComponents(repositoryRoot)
            .Concat(ReadManualComponents(repositoryRoot))
            .ToHashSet(StringComparer.Ordinal);
        var content = CreateProvider().GetContent(AboutDialogKind.ThirdPartyLicenses);
        var actual = content.Components
            .Select(static item => $"{item.Name}@{item.Version}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.All(content.Components, static item =>
        {
            Assert.DoesNotContain("Bundled with", item.NoticeText, StringComparison.OrdinalIgnoreCase);
            Assert.True(item.NoticeText.Length >= 100, $"{item.Name}@{item.Version} has no meaningful notice text.");
        });
        Assert.DoesNotContain(content.Components, static item =>
            item.Name is "vitest" or "esbuild" or "jsdom" or "xunit");

        var domPurify = Assert.Single(content.Components, static item => item.Name == "dompurify");
        Assert.Equal("(MPL-2.0 OR Apache-2.0)", domPurify.LicenseIdentifier);
        Assert.Contains("Mozilla Public License Version 2.0", domPurify.NoticeText, StringComparison.Ordinal);
        Assert.Contains("Apache License", domPurify.NoticeText, StringComparison.Ordinal);

        var highlight = Assert.Single(content.Components, static item => item.Name == "highlight.js");
        Assert.Contains("Copyright (c) 2006, Ivan Sagalaev", highlight.NoticeText, StringComparison.Ordinal);
        Assert.Contains("Redistribution and use", highlight.NoticeText, StringComparison.Ordinal);
        Assert.Contains("THIS SOFTWARE IS PROVIDED", highlight.NoticeText, StringComparison.Ordinal);

        var dotnet = Assert.Single(content.Components, static item => item.Name == ".NET Runtime (win-x64)");
        Assert.Contains(".NET Runtime uses third-party libraries", dotnet.NoticeText, StringComparison.Ordinal);

        var codePages = Assert.Single(
            content.Components,
            static item => item.Name == "System.Text.Encoding.CodePages");
        Assert.Contains("===== THIRD-PARTY-NOTICES.TXT =====", codePages.NoticeText, StringComparison.Ordinal);
        Assert.Contains(".NET Runtime uses third-party libraries", codePages.NoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_notice_versions_match_direct_runtime_package_references()
    {
        var repositoryRoot = GetRepositoryRoot();
        var manualSourcePath = Path.Combine(
            repositoryRoot,
            "src",
            "MarkUpViewMini.App",
            "About",
            "Resources",
            "runtime-notice-sources.json");
        using var manualSources = JsonDocument.Parse(File.ReadAllText(manualSourcePath));
        var manualVersions = manualSources.RootElement.EnumerateArray()
            .ToDictionary(
                static component => component.GetProperty("packageId").GetString()!,
                static component => component.GetProperty("version").GetString()!,
                StringComparer.OrdinalIgnoreCase);
        string[] projectPaths =
        [
            Path.Combine(repositoryRoot, "src", "MarkUpViewMini.App", "MarkUpViewMini.App.csproj"),
            Path.Combine(repositoryRoot, "src", "MarkUpViewMini.Infrastructure", "MarkUpViewMini.Infrastructure.csproj"),
        ];
        var packageReferences = projectPaths
            .Select(XDocument.Load)
            .SelectMany(static project => project.Descendants("PackageReference"))
            .ToDictionary(
                static reference => reference.Attribute("Include")!.Value,
                static reference => reference.Attribute("Version")!.Value,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(packageReferences["Microsoft.Web.WebView2"], manualVersions["microsoft.web.webview2"]);
        Assert.Equal(
            packageReferences["System.Text.Encoding.CodePages"],
            manualVersions["system.text.encoding.codepages"]);
    }

    [Fact]
    public void Invalid_notice_resource_returns_a_local_fallback_without_throwing()
    {
        var provider = new AboutMetadataProvider(
            new StringReader("{invalid"),
            new StringReader("license"),
            typeof(App).Assembly);

        var content = provider.GetContent(AboutDialogKind.ThirdPartyLicenses);

        Assert.Contains("고지를 읽을 수 없습니다", content.Body, StringComparison.Ordinal);
        Assert.Empty(content.Components);
    }

    [Fact]
    public void Null_notice_entry_returns_a_local_fallback_without_throwing()
    {
        var provider = new AboutMetadataProvider(
            new StringReader("[null]"),
            new StringReader("license"),
            typeof(App).Assembly);

        var content = provider.GetContent(AboutDialogKind.ThirdPartyLicenses);

        Assert.Contains("고지를 읽을 수 없습니다", content.Body, StringComparison.Ordinal);
        Assert.Empty(content.Components);
    }

    private static AboutMetadataProvider CreateProvider() => new();

    private static IEnumerable<string> ReadBundleComponents(string repositoryRoot)
    {
        string[] manifestPaths =
        [
            Path.Combine(repositoryRoot, "web", "document-surface", "dist", "runtime-components.json"),
            Path.Combine(repositoryRoot, "web", "mermaid-editor", "dist", "runtime-components.json"),
        ];

        foreach (var path in manifestPaths)
        {
            Assert.True(File.Exists(path), $"Build the web bundle component manifest first: {path}");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var package in document.RootElement.GetProperty("packages").EnumerateArray())
            {
                yield return $"{package.GetProperty("name").GetString()}@{package.GetProperty("version").GetString()}";
            }
        }
    }

    private static IEnumerable<string> ReadManualComponents(string repositoryRoot)
    {
        var path = Path.Combine(
            repositoryRoot,
            "src",
            "MarkUpViewMini.App",
            "About",
            "Resources",
            "runtime-notice-sources.json");
        Assert.True(File.Exists(path), $"The manual runtime notice source is missing: {path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var component in document.RootElement.EnumerateArray())
        {
            yield return $"{component.GetProperty("name").GetString()}@{component.GetProperty("version").GetString()}";
        }
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
