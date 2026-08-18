using MarkUpViewMini.App.Localization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MarkUpViewMini.App.ViewModels;

namespace MarkUpViewMini.App.Composition;

internal sealed class NativeRecoveryDecisionDialog : IRecoveryDecisionDialog
{
    public Task<RecoveryDecisionKind> ChooseAsync(
        RecoveryPromptViewModel prompt,
        RecoveryComparisonViewModel? comparison,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (comparison is not null)
        {
            _ = CreateComparisonWindow(comparison).ShowDialog();
        }

        var primary = MessageBox.Show(
            $"An unsaved recovery copy exists for {Path.GetFileName(prompt.Record.Path)}.\n\n" +
            "Yes: Restore   No: More choices   Cancel: Stop startup",
            "Recover unsaved document",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (primary == MessageBoxResult.Yes)
        {
            return Task.FromResult(RecoveryDecisionKind.Restore);
        }

        if (primary == MessageBoxResult.Cancel)
        {
            return Task.FromResult(RecoveryDecisionKind.Cancel);
        }

        var secondary = MessageBox.Show(
            "Yes: Use original   No: Compare   Cancel: Stop startup",
            "Recovery choices",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return Task.FromResult(secondary switch
        {
            MessageBoxResult.Yes => RecoveryDecisionKind.UseOriginal,
            MessageBoxResult.No => RecoveryDecisionKind.Compare,
            _ => RecoveryDecisionKind.Cancel,
        });
    }

    public void ShowOriginalReadError() => MessageBox.Show(
        "The original document could not be read. The recovery decision is still pending.",
        "Recovery",
        MessageBoxButton.OK,
        MessageBoxImage.Error);

    internal static Window CreateComparisonWindow(RecoveryComparisonViewModel comparison)
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var recoveredHeader = CreateHeader(Strings.Get("dialog.recovery.recovered"));
        var originalHeader = CreateHeader(Strings.Get("dialog.recovery.original"));
        Grid.SetColumn(originalHeader, 1);
        var recovered = CreateTextBox(comparison.Recovered.Text, Strings.Get("dialog.recovery.recoveredBody"));
        var original = CreateTextBox(comparison.Original.Text, Strings.Get("dialog.recovery.originalBody"));
        Grid.SetRow(recovered, 1);
        Grid.SetRow(original, 1);
        Grid.SetColumn(original, 1);
        grid.Children.Add(recoveredHeader);
        grid.Children.Add(originalHeader);
        grid.Children.Add(recovered);
        grid.Children.Add(original);
        return new Window
        {
            Title = Strings.Format("dialog.recovery.title", Path.GetFileName(comparison.Recovered.Path)),
            Width = 900,
            Height = 600,
            Content = grid,
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }

    private static TextBlock CreateHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 4),
        };
        AutomationProperties.SetName(header, text);
        return header;
    }

    private static TextBox CreateTextBox(string text, string accessibleName)
    {
        var body = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(4),
        };
        AutomationProperties.SetName(body, accessibleName);
        return body;
    }
}
