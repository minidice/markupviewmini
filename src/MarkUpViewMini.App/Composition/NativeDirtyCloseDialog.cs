using MarkUpViewMini.App.Localization;
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
            Title = Strings.Get("dialog.dirtyClose.title"),
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(18), MinWidth = 390 };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Format("dialog.dirtyClose.message", Path.GetFileName(tab.Path)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(CreateButton(Strings.Get("dialog.dirtyClose.save"), Strings.Get("dialog.dirtyClose.saveName"), true, DirtyCloseChoice.Save));
        buttons.Children.Add(CreateButton(Strings.Get("dialog.dirtyClose.discard"), Strings.Get("dialog.dirtyClose.discardName"), false, DirtyCloseChoice.Discard));
        var cancel = CreateButton(Strings.Get("dialog.dirtyClose.cancel"), Strings.Get("dialog.dirtyClose.cancelName"), false, DirtyCloseChoice.Cancel);
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
