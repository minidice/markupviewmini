using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
                Assert.Equal("앱 라이선스", AutomationProperties.GetName(dialog));
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

            var renderedText = ReadDocumentText(dialog.BodyDocument);
            var approvedBlockIndex = renderedText.IndexOf(approvedBlock, StringComparison.Ordinal);
            Assert.True(approvedBlockIndex >= 0, "The rendered license body omitted or changed the approved English block.");

            var mitHeadingIndex = renderedText.IndexOf(
                "MIT License",
                approvedBlockIndex + approvedBlock.Length,
                StringComparison.Ordinal);

            Assert.True(
                mitHeadingIndex > approvedBlockIndex + approvedBlock.Length,
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
    public void Dialog_renders_each_component_notice_in_a_selectable_accessible_read_only_wrapping_control_on_sta()
    {
        RunOnSta(() =>
        {
            var content = new AboutMetadataProvider().GetContent(AboutDialogKind.ThirdPartyLicenses);
            var dialog = new AboutDialog(content, new FailingLinkLauncher());
            try
            {
                dialog.Show();
                var componentIndex = content.Components
                    .Select(static (item, index) => (item, index))
                    .Single(static pair => pair.item.Name == "Microsoft.Web.WebView2")
                    .index;
                dialog.ComponentsList.ScrollIntoView(content.Components[componentIndex]);
                dialog.ComponentsList.UpdateLayout();
                var componentItem = Assert.IsType<ListBoxItem>(
                    dialog.ComponentsList.ItemContainerGenerator.ContainerFromIndex(componentIndex));
                var textBoxes = FindVisualDescendants<TextBox>(componentItem).ToArray();
                Assert.Equal(2, textBoxes.Length);
                var metadata = textBoxes[0];
                var notice = textBoxes[1];

                Assert.True(metadata.IsReadOnly);
                Assert.Contains("Microsoft.Web.WebView2", metadata.Text, StringComparison.Ordinal);
                Assert.Contains("1.0.2903.40", metadata.Text, StringComparison.Ordinal);
                Assert.Contains("https://", metadata.Text, StringComparison.Ordinal);
                Assert.True(notice.IsReadOnly);
                Assert.Equal(TextWrapping.Wrap, notice.TextWrapping);
                Assert.Equal(
                    "타사 구성 요소 고지: Microsoft.Web.WebView2",
                    AutomationProperties.GetName(notice));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void Production_version_dialog_enables_copy_and_places_initial_focus_on_close_on_sta()
    {
        RunOnSta(() =>
        {
            var content = new AboutMetadataProvider().GetContent(AboutDialogKind.Version);
            var dialog = new AboutDialog(content, new FailingLinkLauncher());
            try
            {
                dialog.Show();
                dialog.Dispatcher.Invoke(() => { });

                Assert.True(dialog.CopyDiagnosticsButton.IsEnabled);
                dialog.CopyDiagnostics();
                Assert.Equal(content.DiagnosticsText, Clipboard.GetText());
                Assert.Same(dialog.CloseButton, FocusManager.GetFocusedElement(dialog));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void Dialog_copies_diagnostics_and_closes_when_escape_is_pressed_on_sta()
    {
        RunOnSta(() =>
        {
            var content = ApplicationLicenseContent() with { DiagnosticsText = "diagnostic value" };
            var dialog = new AboutDialog(content, new FailingLinkLauncher());
            try
            {
                dialog.Show();
                dialog.CopyDiagnostics();
                dialog.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(dialog),
                    Environment.TickCount,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                });

                Assert.Equal("diagnostic value", Clipboard.GetText());
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

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

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
            errorMessage = "링크를 열 수 없습니다.";
            return false;
        }
    }
}
