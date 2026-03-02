using System;
using System.Globalization;

namespace Win7POS.Core.Util
{
    /// <summary>
    /// CLP (pesos cileni) in formato intero: nessun decimale, nessun separatore.
    /// Usare ovunque per formattazione e parsing di importi POS (display, receipt, payment).
    /// </summary>
    public static class MoneyClp
    {
        /// <summary>Formatta importo in pesos interi: "1200" (no separatori, no decimali).</summary>
        public static string Format(int pesos)
        {
            return pesos.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parsing di stringa in pesos interi.
        /// Accetta "1200", "1.200", "1,200" (punti/virgole e spazi vengono rimossi).
        /// Se invalido o negativo ritorna -1.
        /// </summary>
        public static int Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var raw = text.Trim();
            var digitsOnly = raw.Replace(".", string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty);

            if (string.IsNullOrEmpty(digitsOnly))
                return 0;

            if (!int.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return -1;

            return value < 0 ? -1 : value;
        }
    }
}
