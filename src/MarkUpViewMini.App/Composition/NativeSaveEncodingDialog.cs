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
        AutomationProperties.SetName(selection, "저장 인코딩");
        EncodingDescriptor? result = null;
        var dialog = new Window
        {
            Title = "저장 인코딩 선택",
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(18), MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = "파일에 사용할 인코딩을 선택하세요.",
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
            Content = "저장",
            Padding = new Thickness(14, 5, 14, 5),
            IsDefault = true,
            MinWidth = 76,
        };
        AutomationProperties.SetName(save, "선택한 인코딩으로 저장");
        save.Click += (_, _) =>
        {
            result = ((SaveEncodingChoice)selection.SelectedItem).Encoding;
            dialog.DialogResult = true;
        };
        var cancel = new Button
        {
            Content = "취소",
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(6, 0, 0, 0),
            IsCancel = true,
            MinWidth = 76,
        };
        AutomationProperties.SetName(cancel, "인코딩 선택 취소");
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
            new("현재 인코딩 유지", current),
            new("Unicode (UTF-8, BOM 없음)", new EncodingDescriptor("utf-8", false)),
            new("Unicode (UTF-8, BOM 포함)", new EncodingDescriptor("utf-8", true)),
            new("Unicode (UTF-16 LE)", new EncodingDescriptor("utf-16", true)),
            new("Unicode (UTF-16 BE)", new EncodingDescriptor("utf-16BE", true)),
            new("한국어 (Windows-949)", new EncodingDescriptor("ks_c_5601-1987", false)),
        };
        return choices
            .DistinctBy(choice => choice.Encoding)
            .ToArray();
    }
}
