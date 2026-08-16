namespace MarkUpViewMini.App.About;

internal enum AboutDialogKind
{
    Version,
    ThirdPartyLicenses,
    ApplicationLicense,
}

internal sealed record RuntimeComponentNotice(
    string Name,
    string Version,
    string LicenseIdentifier,
    string SourceUrl,
    string NoticeText);

internal sealed record AboutDialogContent(
    AboutDialogKind Kind,
    string Title,
    string Body,
    IReadOnlyList<RuntimeComponentNotice> Components,
    IReadOnlyList<Uri> ClickableLinks,
    string DiagnosticsText);

internal interface IAboutMetadataProvider
{
    AboutDialogContent GetContent(AboutDialogKind kind);
}
