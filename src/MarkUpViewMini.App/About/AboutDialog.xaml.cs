using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;

namespace MarkUpViewMini.App.About;

internal partial class AboutDialog : Window
{
    private const string DefaultLinkOpenFailureMessage = "링크를 열 수 없습니다.";
    private readonly IAboutLinkLauncher launcher;

    internal AboutDialog(AboutDialogContent content, IAboutLinkLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(launcher);

        InitializeComponent();
        BodyDocument.FontFamily = FontFamily;
        BodyDocument.FontSize = FontSize;
        this.launcher = launcher;
        Title = content.Title;
        AutomationProperties.SetName(this, content.Title);
        RenderBody(content);
    }

    internal void OpenLink(Uri uri)
    {
        if (launcher.TryOpen(uri, out var errorMessage))
        {
            return;
        }

        ErrorText.Text = string.IsNullOrWhiteSpace(errorMessage)
            ? DefaultLinkOpenFailureMessage
            : errorMessage;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void RenderBody(AboutDialogContent content)
    {
        BodyDocument.Blocks.Clear();
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var cursor = 0;
        foreach (var link in content.ClickableLinks)
        {
            if (!AboutLinkLauncher.IsAllowed(link))
            {
                continue;
            }

            var displayText = FindLinkDisplayText(content.Body, link, cursor);
            if (displayText is null)
            {
                continue;
            }

            var linkIndex = content.Body.IndexOf(displayText, cursor, StringComparison.Ordinal);
            paragraph.Inlines.Add(new Run(content.Body[cursor..linkIndex]));
            paragraph.Inlines.Add(CreateLink(link, displayText));
            cursor = linkIndex + displayText.Length;
        }

        paragraph.Inlines.Add(new Run(content.Body[cursor..]));
        BodyDocument.Blocks.Add(paragraph);

        foreach (var component in content.Components)
        {
            BodyDocument.Blocks.Add(CreateComponentParagraph(component));
        }
    }

    private static Paragraph CreateComponentParagraph(RuntimeComponentNotice component)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 16, 0, 0) };
        paragraph.Inlines.Add(new Bold(new Run($"{component.Name} {component.Version} — {component.LicenseIdentifier}")));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run(component.SourceUrl));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run(component.NoticeText));
        return paragraph;
    }

    private static string? FindLinkDisplayText(string body, Uri link, int startIndex)
    {
        string[] candidates =
        [
            link.OriginalString,
            link.OriginalString.TrimEnd('/'),
        ];
        return candidates
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(candidate => body.IndexOf(candidate, startIndex, StringComparison.Ordinal) >= 0);
    }

    private Hyperlink CreateLink(Uri link, string displayText)
    {
        var hyperlink = new Hyperlink(new Run(displayText))
        {
            NavigateUri = link,
        };
        hyperlink.RequestNavigate += Link_RequestNavigate;
        AutomationProperties.SetName(hyperlink, $"외부 링크 열기: {displayText}");

        return hyperlink;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        OpenLink(e.Uri);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AboutDialog_Loaded(object sender, RoutedEventArgs e)
    {
        FocusManager.SetFocusedElement(this, CloseButton);
        CloseButton.Focus();
    }

    private void AboutDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
