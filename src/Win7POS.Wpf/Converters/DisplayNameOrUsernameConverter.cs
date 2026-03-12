using System;
using System.Globalization;
using System.Windows.Data;

namespace Win7POS.Wpf.Converters
{
    /// <summary>Restituisce DisplayName se non vuoto, altrimenti @Username. Parametro: path dell'oggetto (UserAccount).</summary>
    public sealed class DisplayNameOrUsernameConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return "";
            var displayName = values[0]?.ToString()?.Trim() ?? "";
            var username = values[1]?.ToString()?.Trim() ?? "";
            return string.IsNullOrEmpty(displayName) ? "@" + username : displayName;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
