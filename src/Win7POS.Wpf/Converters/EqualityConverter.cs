using System;
using System.Globalization;
using System.Windows.Data;

namespace Win7POS.Wpf.Converters
{
    /// <summary>Restituisce true quando i due valori del MultiBinding sono uguali (per evidenziare la voce di menu attiva).</summary>
    public sealed class EqualityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return false;
            var a = values[0]?.ToString() ?? "";
            var b = values[1]?.ToString() ?? "";
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
