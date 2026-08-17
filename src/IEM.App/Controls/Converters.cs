using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using IEM.Core.Presentation;

namespace IEM.App.Controls;

/// <summary>Booleans to visibility, with an inverted mode for "show when not running".</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when its bound value has something in it.</summary>
public sealed class PresentToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Collapsed,
            string text when string.IsNullOrWhiteSpace(text) => Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Colours the verdict banner.
/// <para>
/// Keyed off the shared verdict rather than off raw counts, so the window cannot end up
/// showing green next to a headline that says there is a case.
/// </para>
/// </summary>
public sealed class VerdictBrushConverter : IValueConverter
{
    /// <summary>Pass "Soft" for the background, anything else for the accent.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value is SessionVerdict verdict ? verdict.Kind : VerdictKind.TooShort;
        var soft = string.Equals(parameter as string, "Soft", StringComparison.OrdinalIgnoreCase);

        return kind switch
        {
            VerdictKind.UpstreamFault => soft ? Soft("#FDECEB") : Palette.Outage,
            VerdictKind.LocalFault => soft ? Soft("#FDF5E2") : Palette.Degraded,
            VerdictKind.Stable => soft ? Soft("#EAF7EF") : Palette.Ok,
            _ => soft ? Soft("#F4F4F6") : Palette.Neutral,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Soft(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Green while the connection is up, red while it is not.</summary>
public sealed class OnlineBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Palette.Ok : Palette.Outage;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
