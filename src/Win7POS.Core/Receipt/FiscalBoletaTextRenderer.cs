using System;
using System.Collections.Generic;
using System.Globalization;

namespace Win7POS.Core.Receipt
{
    /// <summary>
    /// Deterministic, width-bounded renderer shared by fiscal preview and direct
    /// thermal printing. It does not create or archive a file.
    /// </summary>
    public static class FiscalBoletaTextRenderer
    {
        public const string SiiStampMarker = "Timbre Electrónico SII";

        private static readonly CultureInfo EsCl = CultureInfo.GetCultureInfo("es-CL");

        public static string Render(
            ReceiptShopInfo shop,
            long createdAtMs,
            int boletaNumber,
            long totalMinor,
            long includedVatMinor,
            int columns)
        {
            if (boletaNumber <= 0) throw new ArgumentOutOfRangeException(nameof(boletaNumber));

            var width = ReceiptTextLayout.NormalizeColumns(columns);
            var safeShop = shop ?? new ReceiptShopInfo();
            ReceiptShopMetadataPolicy.EnsureValidReceiptShop(safeShop);
            var lines = new List<string> { string.Empty };

            AddWrapped(lines, safeShop.Name, width);
            AddWrapped(lines, safeShop.Rut, width);
            AddWrapped(lines, PrefixOrFallback("Giro", safeShop.BusinessGiro, "no informado"), width);
            AddWrapped(lines, PrefixOrFallback("Representante legal", safeShop.LegalRepresentativeRut, "no informado"), width);
            AddWrapped(lines, PrefixOrFallback("Dirección", safeShop.Address, "no informada"), width);
            AddWrapped(lines, PrefixOrFallback("Ciudad", safeShop.City, "no informada"), width);
            AddWrapped(lines, "BOLETA ELECTRÓNICA NUMERO: " + FormatNumber(boletaNumber), width);
            AddWrapped(
                lines,
                "Fecha: " + DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                width);
            lines.Add(string.Empty);
            lines.AddRange(ReceiptTextLayout.TwoColumnLine("Venta", "$ " + FormatNumber(totalMinor), width));
            lines.Add(string.Empty);
            AddWrapped(lines, "El IVA incluido en esta boleta es", width);
            lines.AddRange(ReceiptTextLayout.TwoColumnLine("de:", "$ " + FormatNumber(includedVatMinor), width));
            lines.Add(string.Empty);
            lines.Add(SiiStampMarker);
            AddWrapped(lines, "Res. 99 de 2014", width);
            AddWrapped(lines, "Verifique documento en sii.cl", width);

            var receipt = string.Join(Environment.NewLine, lines);
            ReceiptDocumentPolicy.EnsureValidDocument(receipt);
            return receipt;
        }

        private static string PrefixOrFallback(
            string prefix,
            string value,
            string fallback)
        {
            return prefix + ": " +
                   (string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
        }

        private static string FormatNumber(long value)
            => value.ToString("#,0", EsCl);

        private static void AddWrapped(List<string> lines, string value, int width)
        {
            lines.AddRange(ReceiptTextLayout.WrapText(value ?? string.Empty, width));
        }
    }
}
