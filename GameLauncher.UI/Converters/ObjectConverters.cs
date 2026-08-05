using Avalonia.Data.Converters;
using Avalonia.Media;
using GameLauncher.Core.Models;
using GameLauncher.UI.Services;
using System;
using System.Globalization;

namespace GameLauncher.UI.Converters;

public static class ObjectConverters
{
    public static readonly IValueConverter IsNotZero = new IsNotZeroConverter();
    public static readonly IValueConverter IsZero = new IsZeroConverter();
    public static readonly IValueConverter IsNotNull = new IsNotNullConverter();
    public static readonly IValueConverter IsNull = new IsNullConverter();
    public static readonly IValueConverter NullToString = new NullToStringConverter();
    public static readonly IValueConverter StringArrayToString = new StringArrayToStringConverter();
    public static readonly IValueConverter Not = new NotConverter();
    public static new readonly IValueConverter Equals = new EqualsConverter();
    public static readonly IValueConverter EqualsAny = new EqualsAnyConverter();
    public static readonly IValueConverter BytesToMBps = new BytesToMBpsConverter();
    public static readonly IValueConverter StatusPillBg = new StatusPillBackgroundConverter();
    public static readonly IValueConverter StatusPillFg = new StatusPillForegroundConverter();
    public static readonly IValueConverter BoolToBrush = new BoolToBrushConverter();
    public static readonly IValueConverter ToastBrush = new ToastBrushConverter();
    public static readonly IValueConverter ToastIcon = new ToastIconConverter();
    public static readonly IValueConverter TagChipBg = new TagChipBackgroundConverter();
    public static readonly IValueConverter TagChipFg = new TagChipForegroundConverter();
}

public class IsZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int i => i == 0,
            long l => l == 0,
            double d => d == 0,
            _ => false
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class IsNotZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            _ => false
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class IsNotNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value != null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class IsNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value == null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class NullToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "").Split('|');
        var ifNull = parts.Length > 0 ? parts[0] : "";
        var ifNotNull = parts.Length > 1 ? parts[1] : "";
        return value == null ? ifNull : ifNotNull;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class StringArrayToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string[] arr ? string.Join(", ", arr) : "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as string ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public class NotConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : false;
    }
}

public class EqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class EqualsAnyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        var candidates = parameter.ToString()!.Split(',', StringSplitOptions.TrimEntries);
        return Array.IndexOf(candidates, value.ToString()) >= 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class BytesToMBpsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is double speed ? $"{speed / 1024 / 1024:F1} MB/s" : "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class StatusPillBackgroundConverter : IValueConverter
{
    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            InstallStatus.Installed => Hex("#301E9E57"),
            InstallStatus.Downloading or InstallStatus.Installing => Hex("#364A6CF7"),
            InstallStatus.Failed => Hex("#36DC2626"),
            _ => Hex("#1C8B94A4")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class StatusPillForegroundConverter : IValueConverter
{
    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            InstallStatus.Installed => Hex("#1E9E57"),
            InstallStatus.Downloading or InstallStatus.Installing => Hex("#4A6CF7"),
            InstallStatus.Failed => Hex("#DC2626"),
            _ => Hex("#8B94A4")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class BoolToBrushConverter : IValueConverter
{
    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = value is true;
        if (parameter is string p)
        {
            var parts = p.Split('|');
            if (parts.Length == 2)
            {
                return Hex(active ? parts[0] : parts[1]);
            }
        }
        return active;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
public class ToastBrushConverter : IValueConverter
{
    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            NotificationType.Success => Hex("#1E9E57"),
            NotificationType.Warning => Hex("#D97706"),
            NotificationType.Error => Hex("#DC2626"),
            _ => Hex("#4A6CF7")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class ToastIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            NotificationType.Success => "\u2705",
            NotificationType.Warning => "\u26A0\uFE0F",
            NotificationType.Error => "\u274C",
            _ => "\u2139\uFE0F"
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class TagChipBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.Equals(value?.ToString(), parameter?.ToString())
            ? ResourceBrush("AccentBrush")
            : ResourceBrush("TagBgBrush");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;

    private static IBrush? ResourceBrush(string key)
    {
        if (Avalonia.Application.Current is App app &&
            app.Resources.TryGetResource(key, app.RequestedThemeVariant, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }
        return null;
    }
}

public class TagChipForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.Equals(value?.ToString(), parameter?.ToString())
            ? Brushes.White
            : ResourceBrush("TextSecondaryBrush");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;

    private static IBrush? ResourceBrush(string key)
    {
        if (Avalonia.Application.Current is App app &&
            app.Resources.TryGetResource(key, app.RequestedThemeVariant, out var value) &&
            value is IBrush brush)
        {
            return brush;
        }
        return null;
    }
}