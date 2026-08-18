using MarkUpViewMini.App.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MarkUpViewMini.App;

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.Equals(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class OutlineIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int headingLevel ? Math.Clamp(headingLevel, 1, 6) : 1;
        return new Thickness((level - 1) * 14, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Formats a bound value through a catalogue entry named by the converter parameter.
/// </summary>
/// <remarks>
/// XAML's StringFormat takes a literal, so a localised "Line {0}" cannot be expressed with it.
/// Converters do not re-run on their own when the language changes, which is acceptable here:
/// the surfaces that use this are rebuilt whenever their content changes.
/// </remarks>
public sealed class LocalizedFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string key ? Strings.Format(key, value) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
