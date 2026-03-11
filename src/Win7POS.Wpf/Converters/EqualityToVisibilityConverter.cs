using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Win7POS.Wpf.Converters
{
    /// <summary>Restituisce Visible quando i due valori del MultiBinding sono uguali (ordinal); altrimenti Collapsed.</summary>
    public sealed class EqualityToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return Visibility.Collapsed;
            var a = values[0]?.ToString() ?? "";
            var b = values[1]?.ToString() ?? "";
            return string.Equals(a, b, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
