using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Zexus.Views.Converters
{
    /// <summary>
    /// Convert a <see cref="Color"/> to a frozen <see cref="SolidColorBrush"/>.
    /// Optional ConverterParameter sets the brush opacity (0..255, hex prefix "#"),
    /// so the same color can be reused as a fill / border / glow.
    /// </summary>
    public class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is Color c)) return Brushes.Transparent;

            byte alpha = c.A;
            if (parameter is string s && !string.IsNullOrEmpty(s))
            {
                if (s.StartsWith("#")) s = s.Substring(1);
                if (byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a))
                    alpha = a;
            }

            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
