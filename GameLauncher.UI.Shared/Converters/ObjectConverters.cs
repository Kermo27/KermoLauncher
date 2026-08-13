using System.Globalization;
using Avalonia.Data.Converters;

namespace GameLauncher.UI.Shared.Converters;

/// <summary>
/// The handful of value converters both apps need. Avalonia ships IsNull/IsNotNull, but not the
/// numeric and boolean ones, so they live here in one place.
/// </summary>
public static class ObjectConverters
{
    public static readonly IValueConverter IsNotZero = new IsNotZeroConverter();
    public static readonly IValueConverter IsZero = new IsZeroConverter();
    public static readonly IValueConverter IsNotNull = new IsNotNullConverter();
    public static readonly IValueConverter IsNull = new IsNullConverter();
    public static readonly IValueConverter Not = new NotConverter();
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
