using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Rede.Desktop.Converters;

/// <summary>
/// Sidebar width: 240px expanded, 48px collapsed.
/// </summary>
public class SidebarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? 48.0 : 240.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Online/connected status -> teal (true) or dim gray (false).
/// </summary>
public class StatusColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Online = new(Color.Parse("#2dd4bf"));
    private static readonly SolidColorBrush Offline = new(Color.Parse("#44445a"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Online : Offline;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Sidebar collapse icon: ">" when collapsed, "<" when expanded.
/// </summary>
public class CollapseIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "\u203a" : "\u2039"; // single angle quotation marks
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
