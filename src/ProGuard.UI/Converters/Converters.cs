using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProGuard.UI.Converters;

/// <summary>true → Visible. Pass "invert" as the parameter to flip it.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value switch
        {
            bool b => b,
            int i => i != 0,
            string s => s.Length > 0,
            null => false,
            _ => true
        };
        if (parameter is "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Compares a value with the parameter; used to make a group of radio buttons pick one enum value.</summary>
public sealed class EqualsParameter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return Binding.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum ? Enum.Parse(type, parameter.ToString()!) : System.Convert.ChangeType(parameter, type, culture);
    }
}
