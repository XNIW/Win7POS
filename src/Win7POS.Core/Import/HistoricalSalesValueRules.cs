using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Win7POS.Core.Import
{
    public static class HistoricalSalesValueRules
    {
        public const string HeaderNormalizationVersion = "getnet-header-nfd-v1";
        private static readonly Regex Number = new Regex(@"^([+-]?)([0-9]+)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]{1,4}))?$", RegexOptions.CultureInvariant);

        public static string NormalizeHeader(string value)
        {
            var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            var pendingSpace = false;
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(character)) { pendingSpace = result.Length > 0; continue; }
                if (pendingSpace) result.Append(' ');
                result.Append(char.ToUpperInvariant(character)); pendingSpace = false;
            }
            return result.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>Parses the lexical decimal exactly: no binary floating point or decimal rounding.</summary>
        public static long ParseClp(string raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 256) throw new FormatException("getnet_amount_invalid");
            var match = Number.Match(value);
            if (!match.Success) throw new FormatException("getnet_amount_invalid");
            var exponent = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            var fraction = match.Groups[3].Value;
            var digits = match.Groups[2].Value + fraction;
            var scale = fraction.Length - exponent;
            if (scale > 0)
            {
                if (scale > digits.Length)
                {
                    if (digits.Trim('0').Length != 0) throw new FormatException("getnet_amount_fractional");
                    return 0;
                }
                for (var index = digits.Length - scale; index < digits.Length; index++)
                    if (digits[index] != '0') throw new FormatException("getnet_amount_fractional");
                digits = digits.Substring(0, digits.Length - scale);
            }
            digits = digits.TrimStart('0');
            if (digits.Length == 0) return 0;
            if (scale < 0)
            {
                if (digits.Length - scale > 19) throw new OverflowException("getnet_amount_overflow");
                digits += new string('0', -scale);
            }
            long result;
            if (!long.TryParse((match.Groups[1].Value == "-" ? "-" : string.Empty) + digits,
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
                throw new OverflowException("getnet_amount_overflow");
            return result;
        }

        public static bool IsMissingIdentifier(string value)
        {
            var text = (value ?? string.Empty).Trim();
            return text.Length == 0 || text == "-" || text == "\u2212" || text == "\u2013" || text == "\u2014";
        }

        public static string ParseIdentifier(string raw, bool numericCell = false, string numberFormat = null)
        {
            var value = (raw ?? string.Empty).Trim();
            if (IsMissingIdentifier(value) || value.Length > 256) throw new FormatException("getnet_identifier_missing_or_invalid");
            foreach (var c in value) if (char.IsControl(c)) throw new FormatException("getnet_identifier_invalid");
            if (!numericCell)
            {
                if (Regex.IsMatch(value, @"^[+-]?[0-9]+(?:\.[0-9]+|[eE][+-]?[0-9]+)$", RegexOptions.CultureInvariant))
                    throw new FormatException("getnet_identifier_numeric_precision");
                return value;
            }
            if (value.IndexOf('e') >= 0 || value.IndexOf('E') >= 0) throw new FormatException("getnet_identifier_numeric_precision");
            var number = ParseClp(value);
            if (number < 0) throw new FormatException("getnet_identifier_invalid");
            var digits = number.ToString(CultureInfo.InvariantCulture);
            if (digits.Length > 15) throw new FormatException("getnet_identifier_numeric_precision");
            if (!string.IsNullOrEmpty(numberFormat) && numberFormat != "General" && numberFormat != "0")
            {
                if (numberFormat.Length > 128 || numberFormat.Trim('0').Length != 0)
                    throw new FormatException("getnet_identifier_number_format_unsupported");
                digits = digits.PadLeft(numberFormat.Length, '0');
            }
            return digits;
        }

        public static HistoricalSalesCardSubtype CardSubtype(string rawType, string rawPrepaid)
        {
            var prepaid = NormalizeHeader(rawPrepaid);
            if (prepaid == "SI") return HistoricalSalesCardSubtype.Prepaid;
            if (prepaid.Length > 0 && prepaid != "NO" && prepaid != "-") throw new FormatException("getnet_prepaid_invalid");
            var type = NormalizeHeader(rawType);
            if (type == "DEBITO") return HistoricalSalesCardSubtype.Debit;
            if (type == "CREDITO") return HistoricalSalesCardSubtype.Credit;
            if (type == "PREPAGO") return HistoricalSalesCardSubtype.Prepaid;
            return HistoricalSalesCardSubtype.Unknown;
        }
    }
}
