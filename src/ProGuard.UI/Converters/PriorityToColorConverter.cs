using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ProGuard.UI.Converters;

public class PriorityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string priority) return Brushes.Transparent;

        return priority switch
        {
            "Realtime" or "High" => new SolidColorBrush(Colors.IndianRed),
            "Above Normal" => new SolidColorBrush(Colors.LightSalmon),
            "Below Normal" or "Idle" => new SolidColorBrush(Colors.LightGreen),
            _ => Brushes.Transparent
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
