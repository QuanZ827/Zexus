using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Zexus.Views.Converters
{
    /// <summary>
    /// true → Visible, false → Collapsed.
    /// Pass ConverterParameter="Invert" to flip the semantics.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool flag && flag;
            if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
                b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var v = value is Visibility vis && vis == Visibility.Visible;
            if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
                v = !v;
            return v;
        }
    }
}
