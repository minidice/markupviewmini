using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MarkUpViewMini.App.ViewModels;

namespace MarkUpViewMini.App.Composition;

internal static class NativeDirtyCloseDialog
{
    internal static DirtyCloseChoice Show(Window owner, DocumentTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(tab);
        var choice = DirtyCloseChoice.Cancel;
        var dialog = new Window
        {
            Title = "미저장 변경",
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(18), MinWidth = 390 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{Path.GetFileName(tab.Path)} 문서의 변경 내용을 저장하시겠습니까?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(CreateButton("저장", "변경 내용 저장", true, DirtyCloseChoice.Save));
        buttons.Children.Add(CreateButton("버리기", "변경 내용 버리기", false, DirtyCloseChoice.Discard));
        var cancel = CreateButton("취소", "닫기 취소", false, DirtyCloseChoice.Cancel);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        _ = dialog.ShowDialog();
        return choice;

        Button CreateButton(
            string content,
            string automationName,
            bool isDefault,
            DirtyCloseChoice selected)
        {
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(6, 0, 0, 0),
                IsDefault = isDefault,
                MinWidth = 76,
            };
            AutomationProperties.SetName(button, automationName);
            button.Click += (_, _) =>
            {
                choice = selected;
                dialog.DialogResult = true;
            };
            return button;
        }
    }
}
