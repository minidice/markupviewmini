using MarkUpViewMini.App.Localization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MarkUpViewMini.App.About;

internal sealed class AboutMetadataProvider : IAboutMetadataProvider
{
    private const string RuntimeNoticesResourceName = "MarkUpViewMini.App.About.Resources.runtime-notices.json";
    private const string ApplicationLicenseResourceName = "MarkUpViewMini.App.About.Resources.app-license.txt";
    private static string NoticesUnavailableMessage => Strings.Get("about.noticesUnavailable");
    private static string LicenseUnavailableMessage => Strings.Get("about.licenseUnavailable");

    private static readonly string[] HighlightedLibraryNames =
    [
        "mermaid",
        "codemirror",
        "katex",
        "dompurify",
        "highlight.js",
        "markdown-it",
        "Microsoft.Web.WebView2",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<TextReader> runtimeNoticesReaderFactory;
    private readonly Func<TextReader> applicationLicenseReaderFactory;
    private readonly Assembly applicationAssembly;

    internal AboutMetadataProvider()
        : this(
            () => OpenResourceReader(RuntimeNoticesResourceName),
            () => OpenResourceReader(ApplicationLicenseResourceName),
            typeof(AboutMetadataProvider).Assembly)
    {
    }

    internal AboutMetadataProvider(
        TextReader runtimeNoticesReader,
        TextReader applicationLicenseReader,
        Assembly applicationAssembly)
        : this(
            () => runtimeNoticesReader,
            () => applicationLicenseReader,
            applicationAssembly)
    {
    }

    private AboutMetadataProvider(
        Func<TextReader> runtimeNoticesReaderFactory,
        Func<TextReader> applicationLicenseReaderFactory,
        Assembly applicationAssembly)
    {
        this.runtimeNoticesReaderFactory = runtimeNoticesReaderFactory;
        this.applicationLicenseReaderFactory = applicationLicenseReaderFactory;
        this.applicationAssembly = applicationAssembly;
    }

    public AboutDialogContent GetContent(AboutDialogKind kind) => kind switch
    {
        AboutDialogKind.Version => BuildVersionContent(),
        AboutDialogKind.ThirdPartyLicenses => BuildThirdPartyContent(),
        AboutDialogKind.ApplicationLicense => BuildApplicationLicenseContent(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private AboutDialogContent BuildVersionContent()
    {
        var notices = ReadRuntimeNotices(out var noticesAvailable);
        var entryAssembly = Assembly.GetEntryAssembly();
        var version = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
            applicationAssembly.GetName().Version?.ToString() ??
            Strings.Get("about.unknown");
        var body = Strings.Format(
            "about.versionBody",
            version,
            RuntimeInformation.FrameworkDescription,
            Environment.Version);
        if (!noticesAvailable)
        {
            body = $"{body}\n\n{NoticesUnavailableMessage}";
        }

        var highlightedLibraries = notices
            .Where(static item => HighlightedLibraryNames.Contains(item.Name))
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static item => Version.TryParse(item.Version, out var parsed) ? parsed : new Version())
                .First())
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ToArray();
        if (highlightedLibraries.Length > 0)
        {
            var libraryLines = string.Join(
                '\n',
                highlightedLibraries.Select(static item => $"- {item.Name} {item.Version}"));
            body = Strings.Format("about.componentsSuffix", body, libraryLines);
        }

        return new AboutDialogContent(
            AboutDialogKind.Version,
            Strings.Get("menu.information.version"),
            body,
            [],
            []);
    }

    private AboutDialogContent BuildThirdPartyContent()
    {
        var notices = ReadRuntimeNotices(out var noticesAvailable);
        var ordered = notices
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ThenBy(static item => item.Version, StringComparer.Ordinal)
            .ToArray();
        return new AboutDialogContent(
            AboutDialogKind.ThirdPartyLicenses,
            Strings.Get("menu.information.thirdParty"),
            noticesAvailable ? Strings.Get("about.thirdPartyBody") : NoticesUnavailableMessage,
            ordered,
            []);
    }

    private AboutDialogContent BuildApplicationLicenseContent()
    {
        try
        {
            using var reader = applicationLicenseReaderFactory();
            var license = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(license))
            {
                throw new InvalidDataException("The application license resource is empty.");
            }

            return new AboutDialogContent(
                AboutDialogKind.ApplicationLicense,
                Strings.Get("menu.information.appLicense"),
                license,
                [],
                [
                    new Uri("https://ministool.com/"),
                    new Uri("https://mdvm.ministool.com/"),
                    new Uri("https://github.com/minidice/markupviewmini"),
                ]);
        }
        catch (IOException)
        {
            return CreateUnavailableLicenseContent();
        }
        catch (MissingManifestResourceException)
        {
            return CreateUnavailableLicenseContent();
        }
        catch (InvalidDataException)
        {
            return CreateUnavailableLicenseContent();
        }
    }

    private IReadOnlyList<RuntimeComponentNotice> ReadRuntimeNotices(out bool available)
    {
        try
        {
            using var reader = runtimeNoticesReaderFactory();
            var notices = JsonSerializer.Deserialize<RuntimeComponentNotice[]>(reader.ReadToEnd(), SerializerOptions);
            if (notices is null || notices.Length == 0 || notices.Any(IsInvalidNotice))
            {
                throw new InvalidDataException("The runtime notices resource contains invalid entries.");
            }

            available = true;
            return notices;
        }
        catch (JsonException)
        {
            available = false;
            return [];
        }
        catch (IOException)
        {
            available = false;
            return [];
        }
        catch (MissingManifestResourceException)
        {
            available = false;
            return [];
        }
        catch (InvalidDataException)
        {
            available = false;
            return [];
        }
    }

    private static bool IsInvalidNotice(RuntimeComponentNotice? notice) =>
        notice is null ||
        string.IsNullOrWhiteSpace(notice.Name) ||
        string.IsNullOrWhiteSpace(notice.Version) ||
        string.IsNullOrWhiteSpace(notice.LicenseIdentifier) ||
        string.IsNullOrWhiteSpace(notice.SourceUrl) ||
        string.IsNullOrWhiteSpace(notice.NoticeText) ||
        !Uri.TryCreate(notice.SourceUrl, UriKind.Absolute, out var sourceUri) ||
        sourceUri.Scheme != Uri.UriSchemeHttps;

    private static TextReader OpenResourceReader(string resourceName)
    {
        var stream = typeof(AboutMetadataProvider).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new MissingManifestResourceException($"Embedded resource '{resourceName}' was not found.");
        }

        return new StreamReader(stream);
    }

    private static AboutDialogContent CreateUnavailableLicenseContent() => new(
        AboutDialogKind.ApplicationLicense,
        Strings.Get("menu.information.appLicense"),
        LicenseUnavailableMessage,
        [],
        []);
}
