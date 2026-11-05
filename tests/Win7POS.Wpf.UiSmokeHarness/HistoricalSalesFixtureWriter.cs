using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Independent OOXML fixture author. It does not call the product reader, query or value rules.
    internal static class HistoricalSalesFixtureWriter
    {
        internal static readonly string[] LegacyHeaders = { "FECHA VENTA", "ID TRANSACCION", "VALOR TRANSACCION", "ESTADO", "LOCAL", "COMISION", "MONTO ABONO", "TIPO", "COD.AUT" };
        internal static readonly string[] ModernHeaders = { "FECHA DE VENTA", "ID FINANCIERO DEL MOVIMIENTO", "MONTO BRUTO", "ESTADO TRX", "TIPO MOV.", "MONEDA", "ZONA HORARIA (UTC)", "VALOR TRANSACCION", "LOCAL", "COMISION", "IVA COMISION", "VALOR NETO", "TIPO", "PREPAGO" };
        internal static string[] CreateLoad(string root)
        {
            Directory.CreateDirectory(root);
            var legacy = Path.Combine(root, "load-legacy.xlsx"); var modern = Path.Combine(root, "load-modern.xlsx");
            Write(legacy, LegacyHeaders, Enumerable.Range(0, 25000).Select(i => LegacyRow("L-" + i.ToString("D20", CultureInfo.InvariantCulture), 1000, i)));
            Write(modern, ModernHeaders, Enumerable.Range(0, 25000).Select(i => ModernRow(i < 24000 ? "M-" + i.ToString("D20", CultureInfo.InvariantCulture) : "-", i >= 23900 && i < 24000 ? -1000 : 1000, i, i >= 24000)));
            return new[] { legacy, modern };
        }
        internal static string[] LegacyRow(string id, long amount, int second = 0) => new[] { new DateTime(2000, 1, 2, 8, 0, 0).AddSeconds(second).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture), id, amount.ToString(CultureInfo.InvariantCulture), "Abonado", "SYNTHETIC LOAD", "0", amount.ToString(CultureInfo.InvariantCulture), "Debito", "000ABC" };
        internal static string[] ModernRow(string id, long amount, int second = 0, bool rejected = false) => new[] { new DateTime(2000, 1, 2, 8, 0, 0).AddSeconds(second).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture), id, amount.ToString(CultureInfo.InvariantCulture), rejected ? "Rechazada" : "Confirmada", amount < 0 ? "Anulación" : "Venta", "CLP", "0", amount.ToString(CultureInfo.InvariantCulture), "SYNTHETIC LOAD", "0", "0", amount.ToString(CultureInfo.InvariantCulture), "Debito", "No" };
        internal static void Write(string path, IReadOnlyList<string> headers, IEnumerable<string[]> rows, bool reverseColumns = false)
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Text(zip, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/></Types>");
                Text(zip, "_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='xl/workbook.xml'/></Relationships>");
                Text(zip, "xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Synthetic' sheetId='1' r:id='r1'/></sheets></workbook>");
                Text(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
                using (var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open())
                using (var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false }))
                {
                    xml.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"); xml.WriteStartElement("sheetData");
                    var order = Enumerable.Range(0, headers.Count).ToArray(); if (reverseColumns) Array.Reverse(order);
                    Row(xml, order.Select(i => headers[i]));
                    foreach (var row in rows) Row(xml, order.Select(i => row[i]));
                    xml.WriteEndElement(); xml.WriteEndElement();
                }
            }
        }
        private static void Row(XmlWriter xml, IEnumerable<string> values)
        {
            xml.WriteStartElement("row");
            foreach (var value in values) { xml.WriteStartElement("c"); xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is"); xml.WriteElementString("t", value); xml.WriteEndElement(); xml.WriteEndElement(); }
            xml.WriteEndElement();
        }
        private static void Text(ZipArchive zip, string name, string value) { using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(value); }
    }
}
