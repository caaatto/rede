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

/// <summary>
/// Converts input level dB (-100 to 0) to a pixel width for the level meter bar.
/// Assumes max width ~300px (parent container width).
/// </summary>
public class InputLevelConverter : IValueConverter
{
    public static readonly InputLevelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double db) return 0.0;
        // Map -100..0 dB to 0..1, then scale to pixel width
        var normalized = Math.Clamp((db + 100.0) / 100.0, 0.0, 1.0);
        return normalized * 300.0; // max bar width
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
