using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MarkUpViewMini.App.About;

internal sealed class AboutMetadataProvider : IAboutMetadataProvider
{
    private const string RuntimeNoticesResourceName = "MarkUpViewMini.App.About.Resources.runtime-notices.json";
    private const string ApplicationLicenseResourceName = "MarkUpViewMini.App.About.Resources.app-license.txt";
    private const string NoticesUnavailableMessage = "타사 구성 요소 고지를 읽을 수 없습니다.";
    private const string LicenseUnavailableMessage = "앱 라이선스를 읽을 수 없습니다.";

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
            "알 수 없음";
        var body = $"MarkUpViewMini\n버전: {version}\n프레임워크: {RuntimeInformation.FrameworkDescription}\n런타임: {Environment.Version}";
        if (!noticesAvailable)
        {
            body = $"{body}\n\n{NoticesUnavailableMessage}";
        }

        var diagnostics = new StringBuilder()
            .AppendLine("MarkUpViewMini diagnostics")
            .AppendLine($"Application version: {version}")
            .AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"Runtime: {Environment.Version}")
            .AppendLine($"OS: {RuntimeInformation.OSDescription}")
            .AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}")
            .AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
        if (noticesAvailable)
        {
            diagnostics.AppendLine("Runtime components:");
            foreach (var notice in notices.OrderBy(static item => item.Name, StringComparer.Ordinal)
                         .ThenBy(static item => item.Version, StringComparer.Ordinal))
            {
                diagnostics.AppendLine($"- {notice.Name} {notice.Version} ({notice.LicenseIdentifier})");
            }
        }
        else
        {
            diagnostics.AppendLine(NoticesUnavailableMessage);
        }

        return new AboutDialogContent(
            AboutDialogKind.Version,
            "버전 정보",
            body,
            notices,
            [],
            diagnostics.ToString().TrimEnd());
    }

    private AboutDialogContent BuildThirdPartyContent()
    {
        var notices = ReadRuntimeNotices(out var noticesAvailable);
        return new AboutDialogContent(
            AboutDialogKind.ThirdPartyLicenses,
            "타사 라이선스",
            noticesAvailable ? "이 앱에 포함된 런타임 구성 요소의 고지입니다." : NoticesUnavailableMessage,
            notices,
            [],
            string.Empty);
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
                "앱 라이선스",
                license,
                [],
                [
                    new Uri("https://ministool.com/"),
                    new Uri("https://mdvm.ministool.com/"),
                    new Uri("https://github.com/minidice/markupviewmini"),
                ],
                string.Empty);
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
        "앱 라이선스",
        LicenseUnavailableMessage,
        [],
        [],
        string.Empty);
}
