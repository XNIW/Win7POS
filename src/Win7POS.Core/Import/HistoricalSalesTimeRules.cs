using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Win7POS.Core.Import
{
    public static class HistoricalSalesTimeRules
    {
        private static readonly string[] LegacyFormats = { "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss.FFFFFFF" };
        private static readonly string[] IsoFormats = { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" };

        public static HistoricalSalesTimestamp ParseLegacy(string raw, HistoricalSalesImportOptions options, string parsedText = null)
        {
            DateTime business;
            if (!DateTime.TryParseExact(parsedText ?? raw, LegacyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out business))
                throw new FormatException("getnet_timestamp_invalid");
            return new HistoricalSalesTimestamp(raw, string.Empty, null, business, null, null, null,
                HistoricalSalesModernTimeInterpretation.Unresolved, options.TemporalPolicyVersion, string.Empty, false);
        }

        public static HistoricalSalesTimestamp ParseModern(string raw, string rawOffset, HistoricalSalesImportOptions options)
        {
            DateTime source;
            if (!DateTime.TryParseExact(raw, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out source))
                throw new FormatException("getnet_timestamp_invalid");
            source = DateTime.SpecifyKind(source, DateTimeKind.Unspecified);
            var offsetMinutes = ParseOffsetMinutes(rawOffset);
            var isoLocal = source.AddMinutes(offsetMinutes);
            var selected = options.ModernTimeInterpretation;
            var needsEvidence = selected == HistoricalSalesModernTimeInterpretation.Unresolved ||
                string.IsNullOrWhiteSpace(options.TemporalEvidenceReference);
            DateTime? business = selected == HistoricalSalesModernTimeInterpretation.IsoUtc ? isoLocal :
                selected == HistoricalSalesModernTimeInterpretation.ExportedLocal ? source : (DateTime?)null;
            DateTimeOffset? utc = selected == HistoricalSalesModernTimeInterpretation.IsoUtc ?
                new DateTimeOffset(source, TimeSpan.Zero) : selected == HistoricalSalesModernTimeInterpretation.ExportedLocal ?
                new DateTimeOffset(source, TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime() : (DateTimeOffset?)null;
            return new HistoricalSalesTimestamp(raw, rawOffset, offsetMinutes, business, isoLocal, source, utc,
                selected, options.TemporalPolicyVersion, options.TemporalEvidenceReference, needsEvidence);
        }

        public static int ParseOffsetMinutes(string raw)
        {
            var value = (raw ?? string.Empty).Trim();
            var match = Regex.Match(value, @"^([+-]?)([0-9]{1,2})(?::([0-9]{2}))?$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new FormatException("getnet_offset_invalid");
            var hours = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0)) throw new FormatException("getnet_offset_invalid");
            return (match.Groups[1].Value == "-" ? -1 : 1) * (hours * 60 + minutes);
        }
    }
}
