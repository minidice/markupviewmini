using MarkUpViewMini.App.Localization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using MarkUpViewMini.App.About;

namespace MarkUpViewMini.App.Tests.About;

public sealed class AboutDialogTests
{
    [Fact]
    public void Dialog_keeps_content_open_and_announces_a_safe_error_when_link_launch_fails_on_sta()
    {
        RunOnSta(() =>
        {
            var dialog = new AboutDialog(ApplicationLicenseContent(), new FailingLinkLauncher());
            try
            {
                dialog.Show();
                dialog.OpenLink(new Uri("https://ministool.com/"));

                Assert.True(dialog.IsVisible);
                Assert.Contains("열 수 없습니다", dialog.ErrorText.Text, StringComparison.Ordinal);
                Assert.Equal(Strings.Get("menu.information.appLicense"), AutomationProperties.GetName(dialog));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void Dialog_renders_the_exact_English_application_license_block_before_the_MIT_text_on_sta()
    {
        RunOnSta(() =>
        {
            var content = ApplicationLicenseContent();
            var dialog = new AboutDialog(content, new FailingLinkLauncher());
            const string approvedBlock = """
                Copyright © 2026 MiniDice

                This application is provided free of charge under the MIT License.
                Personal, business, and commercial use are permitted.

                https://ministool.com
                https://mdvm.ministool.com
                https://github.com/minidice/markupviewmini
                """;

            // The raw string literal's line endings follow this source file's own (CRLF on
            // Windows checkouts), while ReadDocumentText normalizes rendered text to LF.
            // Normalize here too, so the comparison depends on content, not checkout settings.
            var normalizedApprovedBlock = approvedBlock.Replace("\r\n", "\n", StringComparison.Ordinal);
            var renderedText = ReadDocumentText(dialog.BodyDocument);
            var approvedBlockIndex = renderedText.IndexOf(normalizedApprovedBlock, StringComparison.Ordinal);
            Assert.True(approvedBlockIndex >= 0, "The rendered license body omitted or changed the approved English block.");

            var mitHeadingIndex = renderedText.IndexOf(
                "MIT License",
                approvedBlockIndex + normalizedApprovedBlock.Length,
                StringComparison.Ordinal);

            Assert.True(
                mitHeadingIndex > approvedBlockIndex + normalizedApprovedBlock.Length,
                "The canonical MIT heading must follow the approved English block and its textual URLs.");

            var hyperlinks = dialog.BodyDocument.Blocks
                .OfType<Paragraph>()
                .SelectMany(static paragraph => paragraph.Inlines)
                .OfType<Hyperlink>()
                .ToArray();
            Assert.Equal(
                ["https://ministool.com", "https://mdvm.ministool.com", "https://github.com/minidice/markupviewmini"],
                hyperlinks.Select(ReadInlineText));
            Assert.Equal(
                content.ClickableLinks.Select(static uri => uri.OriginalString),
                hyperlinks.Select(static link => link.NavigateUri.OriginalString));
            Assert.All(hyperlinks, static link =>
                Assert.StartsWith("외부 링크 열기:", AutomationProperties.GetName(link), StringComparison.Ordinal));
            Assert.Equal(
                content.Body.TrimEnd(),
                renderedText,
                ignoreLineEndingDifferences: true);
        });
    }

    [Fact]
    public void Dialog_renders_each_component_notice_as_readable_text_in_the_single_scrolling_document_on_sta()
    {
        RunOnSta(() =>
        {
            var content = new AboutMetadataProvider().GetContent(AboutDialogKind.ThirdPartyLicenses);
            var dialog = new AboutDialog(content, new FailingLinkLauncher());
            try
            {
                dialog.Show();
                var webView2 = Assert.Single(content.Components, static item => item.Name == "Microsoft.Web.WebView2");
                var renderedText = ReadDocumentText(dialog.BodyDocument);

                Assert.Contains(content.Body, renderedText, StringComparison.Ordinal);
                Assert.Contains(
                    $"{webView2.Name} {webView2.Version} — {webView2.LicenseIdentifier}",
                    renderedText,
                    StringComparison.Ordinal);
                Assert.Contains(webView2.SourceUrl, renderedText, StringComparison.Ordinal);
                Assert.Contains(webView2.NoticeText, renderedText, StringComparison.Ordinal);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void Production_version_dialog_places_initial_focus_on_close_on_sta()
    {
        RunOnSta(() =>
        {
            var content = new AboutMetadataProvider().GetContent(AboutDialogKind.Version);
            var dialog = new AboutDialog(content, new FailingLinkLauncher());
            try
            {
                dialog.Show();
                dialog.Dispatcher.Invoke(() => { });

                Assert.Same(dialog.CloseButton, FocusManager.GetFocusedElement(dialog));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void Dialog_closes_when_escape_is_pressed_on_sta()
    {
        RunOnSta(() =>
        {
            var dialog = new AboutDialog(ApplicationLicenseContent(), new FailingLinkLauncher());
            try
            {
                dialog.Show();
                dialog.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(dialog),
                    Environment.TickCount,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                });

                Assert.False(dialog.IsVisible);
            }
            finally
            {
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            }
        });
    }

    private static AboutDialogContent ApplicationLicenseContent() =>
        new AboutMetadataProvider().GetContent(AboutDialogKind.ApplicationLicense);

    private static string ReadDocumentText(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd)
            .Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

    private static string ReadInlineText(Hyperlink hyperlink) =>
        new TextRange(hyperlink.ContentStart, hyperlink.ContentEnd).Text;

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The WPF dialog test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class FailingLinkLauncher : IAboutLinkLauncher
    {
        public bool TryOpen(Uri uri, out string? errorMessage)
        {
            errorMessage = Strings.Get("about.linkOpenFailed");
            return false;
        }
    }
}
