using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    internal sealed class GetnetHeaderMap
    {
        private readonly Dictionary<string, int> _columns = new Dictionary<string, int>(StringComparer.Ordinal);
        private string _duplicate;
        internal GetnetHeaderMap(GetnetWorksheetRow row)
        {
            RowNumber = row.Number;
            foreach (var cell in row.Cells)
            {
                var name = HistoricalSalesValueRules.NormalizeHeader(cell.Value.Value);
                if (name.Length == 0) continue;
                if (_columns.ContainsKey(name)) _duplicate = name;
                else _columns.Add(name, cell.Key);
            }
        }
        internal int RowNumber { get; }
        internal bool Has(string name) => _columns.ContainsKey(name);
        internal HashSet<int> RetainedColumns()
        {
            var fields = new HashSet<string>(new[] { "FECHA VENTA", "ID TRANSACCION", "VALOR TRANSACCION", "ESTADO", "LOCAL", "COMISION", "MONTO ABONO",
                "COD.AUT", "TIPO", "PROPINA", "VUELTO", "COMISION PROPINA", "FECHA DE VENTA", "ID FINANCIERO DEL MOVIMIENTO", "VALOR VENTA", "MONTO BRUTO",
                "ESTADO TRX", "TIPO MOV.", "MONEDA", "ZONA HORARIA (UTC)", "VENDEDOR", "SELLER ID", "NUM LOCAL", "RUT", "IVA COMISION", "VALOR NETO",
                "ESTADO DEL ABONO", "PREPAGO", "PAYMENT ID", "TERMINAL", "FECHA DE ABONO" }, StringComparer.Ordinal);
            return new HashSet<int>(_columns.Where(x => fields.Contains(x.Key)).Select(x => x.Value));
        }
        internal int? Column(string name) => _columns.TryGetValue(name, out var value) ? value : (int?)null;
        internal void Validate()
        { if (_duplicate != null) throw new GetnetImportException("getnet_header_duplicate", _duplicate, RowNumber, Column(_duplicate)); }
        internal void Require(params string[] names)
        {
            foreach (var name in names)
                if (!Has(name)) throw new GetnetImportException("getnet_header_missing", name, RowNumber);
        }
    }

    internal sealed class GetnetRowContext
    {
        private readonly GetnetHeaderMap _header;
        private readonly GetnetWorksheetRow _row;
        private readonly bool _date1904;
        internal GetnetRowContext(GetnetHeaderMap header, GetnetWorksheetRow row, HistoricalSalesSource source, bool date1904)
        { _header = header; _row = row; Source = source; _date1904 = date1904; }
        internal HistoricalSalesSource Source { get; }
        internal bool Has(string field) => _header.Has(field);
        internal GetnetCell Cell(string field)
        {
            var column = _header.Column(field); GetnetCell cell;
            return column.HasValue && _row.Cells.TryGetValue(column.Value, out cell) ? cell : new GetnetCell { Type = "str" };
        }
        internal GetnetImportException Error(string code, string field) => new GetnetImportException(code, field, Source.RowNumber, _header.Column(field));
        internal string Text(string field, int maximum = 256)
        {
            var cell = Cell(field);
            if (cell.Type == "e" || cell.Type == "b") throw Error("getnet_cell_type_invalid", field);
            var value = cell.Value.Trim();
            if (value.Length > maximum) throw Error("getnet_field_text_limit", field);
            return value;
        }
        internal string Identifier(string field, bool required = false)
        {
            var cell = Cell(field); var value = Text(field);
            if (HistoricalSalesValueRules.IsMissingIdentifier(value))
            { if (required) throw Error("getnet_identifier_missing_or_invalid", field); return string.Empty; }
            try { return HistoricalSalesValueRules.ParseIdentifier(value, cell.IsNumeric, cell.NumberFormat); }
            catch (Exception error) when (error is FormatException || error is OverflowException)
            { throw Error(error is FormatException ? error.Message : "getnet_identifier_numeric_precision", field); }
        }
        internal long? Money(string field, bool required = false)
        {
            var value = Text(field);
            if (HistoricalSalesValueRules.IsMissingIdentifier(value))
            { if (required) throw Error("getnet_amount_missing", field); return null; }
            try { return HistoricalSalesValueRules.ParseClp(value); }
            catch (Exception error) when (error is FormatException || error is OverflowException)
            { throw Error(error is FormatException ? error.Message : "getnet_amount_overflow", field); }
        }
        internal void RequireZeroIfProvided(params string[] fields)
        {
            foreach (var field in fields)
            { var value = Money(field); if (value.HasValue && value.Value != 0) throw Error("getnet_tip_or_cashback_unsupported", field); }
        }
        internal HistoricalSalesMerchant Merchant()
        {
            string Clean(string value) => HistoricalSalesValueRules.IsMissingIdentifier(value) ? string.Empty : value;
            var merchant = new HistoricalSalesMerchant(Clean(Text("LOCAL")), Clean(Text("VENDEDOR")),
                Identifier("SELLER ID"), Identifier("NUM LOCAL"), Clean(Text("RUT")));
            if (!merchant.HasEvidence) throw Error("getnet_merchant_missing", "LOCAL");
            return merchant;
        }
        internal string LegacyTimestampText()
        {
            var cell = Cell("FECHA VENTA"); var raw = Text("FECHA VENTA", 128);
            if (cell.Type == "d")
            {
                DateTime typed;
                var formats = new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" };
                if (!DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out typed))
                    throw Error("getnet_timestamp_invalid", "FECHA VENTA");
                return typed.ToString("dd/MM/yyyy HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            }
            if (!cell.IsNumeric) return raw;
            decimal serial;
            if (!cell.IsDate || raw.Count(char.IsDigit) > 28 || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out serial) || serial < 0)
                throw Error("getnet_timestamp_numeric_unsupported", "FECHA VENTA");
            if (!_date1904 && serial >= 60 && serial < 61) throw Error("getnet_timestamp_excel_leap_day", "FECHA VENTA");
            var ticks = checked(serial * TimeSpan.TicksPerDay);
            if (ticks != decimal.Truncate(ticks)) throw Error("getnet_timestamp_precision", "FECHA VENTA");
            var epoch = _date1904 ? new DateTime(1904, 1, 1) : serial < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
            var timestamp = epoch.AddTicks(checked((long)ticks));
            return timestamp.ToString("dd/MM/yyyy HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }
    }

    internal abstract class GetnetProfileAdapter
    {
        internal abstract HistoricalSalesProfile Profile { get; }
        internal abstract void ValidateHeaders(GetnetHeaderMap header);
        internal abstract HistoricalSalesTransaction Read(GetnetRowContext row, HistoricalSalesImportOptions options, List<HistoricalSalesRejectedRow> rejected);
        internal static GetnetProfileAdapter Detect(GetnetHeaderMap header)
        {
            var legacy = header.Has("FECHA VENTA") || header.Has("ID TRANSACCION");
            var modern = header.Has("FECHA DE VENTA") || header.Has("ID FINANCIERO DEL MOVIMIENTO") || header.Has("ESTADO TRX");
            if (legacy && modern) throw new GetnetImportException("getnet_profile_ambiguous", row: header.RowNumber);
            return modern ? (GetnetProfileAdapter)new GetnetModernProfileAdapter() : legacy ? new GetnetLegacyProfileAdapter() : null;
        }
    }
}
