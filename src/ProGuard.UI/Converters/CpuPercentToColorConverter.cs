using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ProGuard.UI.Converters;

public class CpuPercentToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double percent) return Brushes.Transparent;

        if (percent > 80) return new SolidColorBrush(Colors.Red);
        if (percent > 50) return new SolidColorBrush(Colors.Orange);
        if (percent > 20) return new SolidColorBrush(Colors.Yellow);
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
