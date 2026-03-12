using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Win7POS.Wpf.Converters
{
    /// <summary>Convertitore inverso: true → Collapsed, false → Visible.</summary>
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool v && v;
            return b ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
