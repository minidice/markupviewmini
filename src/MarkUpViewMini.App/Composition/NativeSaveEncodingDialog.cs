using MarkUpViewMini.App.Localization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MarkUpViewMini.Core.Documents;

namespace MarkUpViewMini.App.Composition;

internal sealed record SaveEncodingChoice(string DisplayName, EncodingDescriptor Encoding);

internal static class NativeSaveEncodingDialog
{
    internal static EncodingDescriptor? Choose(Window owner, EncodingDescriptor current)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(current);
        var choices = CreateChoices(current);
        var selection = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(SaveEncodingChoice.DisplayName),
            SelectedIndex = 0,
            MinWidth = 280,
        };
        AutomationProperties.SetName(selection, Strings.Get("dialog.encoding.listName"));
        EncodingDescriptor? result = null;
        var dialog = new Window
        {
            Title = Strings.Get("dialog.encoding.title"),
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(18), MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("dialog.encoding.prompt"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(selection);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var save = new Button
        {
            Content = Strings.Get("dialog.encoding.save"),
            Padding = new Thickness(14, 5, 14, 5),
            IsDefault = true,
            MinWidth = 76,
        };
        AutomationProperties.SetName(save, Strings.Get("dialog.encoding.saveName"));
        save.Click += (_, _) =>
        {
            result = ((SaveEncodingChoice)selection.SelectedItem).Encoding;
            dialog.DialogResult = true;
        };
        var cancel = new Button
        {
            Content = Strings.Get("dialog.encoding.cancel"),
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(6, 0, 0, 0),
            IsCancel = true,
            MinWidth = 76,
        };
        AutomationProperties.SetName(cancel, Strings.Get("dialog.encoding.cancelName"));
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        _ = dialog.ShowDialog();
        return result;
    }

    internal static IReadOnlyList<SaveEncodingChoice> CreateChoices(EncodingDescriptor current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var choices = new List<SaveEncodingChoice>
        {
            new(Strings.Get("encoding.keepCurrent"), current),
            new(Strings.Get("encoding.utf8NoBom"), new EncodingDescriptor("utf-8", false)),
            new(Strings.Get("encoding.utf8Bom"), new EncodingDescriptor("utf-8", true)),
            new("Unicode (UTF-16 LE)", new EncodingDescriptor("utf-16", true)),
            new("Unicode (UTF-16 BE)", new EncodingDescriptor("utf-16BE", true)),
            new(Strings.Get("encoding.korean949"), new EncodingDescriptor("ks_c_5601-1987", false)),
        };
        return choices
            .DistinctBy(choice => choice.Encoding)
            .ToArray();
    }
}
