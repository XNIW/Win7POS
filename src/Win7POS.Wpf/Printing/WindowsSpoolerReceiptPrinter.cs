using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Printing
{
    public sealed class WindowsSpoolerReceiptPrinter : IReceiptPrinter
    {
        private static readonly FileLogger _logger = new FileLogger("WindowsSpoolerReceiptPrinter");
        private const string SiiMarker = "Timbre Electrónico SII";

        // regolazioni richieste: spazio bianco sopra QR + QR più stretto su carta
        private const float QrWidthMm = 50f;
        private const float QrTopGapMm = 6f;
        private const float QrBottomGapMm = 3f;
        private const float SaleCodeBarcodeGapMm = 4f;

        private static bool IsSiiMarker(string line)
            => string.Equals((line ?? "").Trim(), SiiMarker, StringComparison.OrdinalIgnoreCase);

        private const string Code128Placeholder = "[Codice a barre Code128 stampato sotto]";
        private static bool IsCode128Placeholder(string line)
            => string.Equals((line ?? "").Trim(), Code128Placeholder, StringComparison.OrdinalIgnoreCase);

        private static string NormalizeSaleCodeForBarcode(string saleCode)
        {
            var normalized = (saleCode ?? string.Empty).Trim();
            return normalized.Length == 0 ? null : normalized;
        }

        private static float MmToHundredthsInch(float mm) => (mm / 25.4f) * 100f;

        public async Task PrintAsync(string receiptText, ReceiptPrintOptions opt)
        {
            if (receiptText == null) throw new ArgumentNullException(nameof(receiptText));
            if (opt == null) throw new ArgumentNullException(nameof(opt));

            // 1) Optional: save copy to file (debug / no printer)
            if (opt.SaveCopyToFile && !string.IsNullOrWhiteSpace(opt.OutputPath))
            {
                var dir = Path.GetDirectoryName(opt.OutputPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(opt.OutputPath, receiptText);
            }

            // 2) Print via Windows spooler driver
            await TryPrintWithRetryAsync(receiptText, opt).ConfigureAwait(false);
        }

        private const string DefaultCashDrawerCommand = "27,112,0,25,250";

        public Task OpenCashDrawerAsync(ReceiptPrintOptions opt)
        {
            if (opt == null) return Task.CompletedTask;
            var cmd = string.IsNullOrWhiteSpace(opt.CashDrawerCommand) ? DefaultCashDrawerCommand : opt.CashDrawerCommand;
            var bytes = ParseCashDrawerCommand(cmd);
            return Task.Run(() => TryOpenCashDrawer(opt.PrinterName, bytes, cmd));
        }

        /// <summary>ESC/POS kick drawer: ESC p m t1 t2. Non usa fallback sulla stampante predefinita Windows.</summary>
        private static void TryOpenCashDrawer(string printerName, byte[] bytes, string cmdForLog)
        {
            if (bytes == null || bytes.Length == 0) return;

            var effectivePrinter = (printerName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(effectivePrinter))
                throw new InvalidOperationException(PosLocalization.T("printer.cashDrawerNotConfigured"));

            var bytesStr = string.Join(",", bytes.Select(b => b.ToString()));
            _logger.LogInfo("CashDrawer: stampante=\"" + (effectivePrinter ?? "") + "\" comando=" + (cmdForLog ?? "") + " bytes=[" + bytesStr + "]");
            try
            {
                RawPrinterHelper.SendBytesToPrinter(effectivePrinter, bytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CashDrawer SendBytes failed");
                throw;
            }
        }

        private static byte[] ParseCashDrawerCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return new byte[] { 27, 112, 0, 25, 250 };
            var parts = cmd.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<byte>();
            foreach (var p in parts)
            {
                if (byte.TryParse(p.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                    list.Add(b);
            }
            return list.Count > 0 ? list.ToArray() : new byte[] { 27, 112, 0, 25, 250 };
        }

        private const int RetryDelayMs = 300;
        private const int ThermalPaper80mmMin = 300;
        private const int ThermalPaper80mmMax = 330;

        private static async Task TryPrintWithRetryAsync(string receiptText, ReceiptPrintOptions opt)
        {
            try
            {
                await Task.Run(() => PrintOnce(receiptText, opt)).ConfigureAwait(false);
            }
            catch
            {
                await Task.Delay(RetryDelayMs).ConfigureAwait(false);
                await Task.Run(() => PrintOnce(receiptText, opt)).ConfigureAwait(false);
            }
        }

        private static void PrintOnce(string receiptText, ReceiptPrintOptions opt)
        {
            if (string.IsNullOrWhiteSpace(opt.PrinterName))
                throw new InvalidOperationException(PosLocalization.T("printer.receiptPrinterNotConfigured"));

            var lines = receiptText.Replace("\r\n", "\n").Split('\n');
            var lineIndex = 0;
            var saleCodeForBarcode = NormalizeSaleCodeForBarcode(opt.SaleCodeForBarcode);
            var barcodePrinted = string.IsNullOrEmpty(saleCodeForBarcode);

            using (var doc = new PrintDocument())
            {
                doc.PrinterSettings.PrinterName = opt.PrinterName;

                doc.PrinterSettings.Copies = (short)Math.Max(1, opt.Copies);

                // Margini a zero; offset reale da HardMargin (area non stampabile driver)
                doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

                // Carta 80mm se disponibile (larghezza ~315 centesimi di pollice)
                try
                {
                    foreach (PaperSize ps in doc.PrinterSettings.PaperSizes)
                    {
                        if (ps.Width >= ThermalPaper80mmMin && ps.Width <= ThermalPaper80mmMax ||
                            (ps.PaperName != null && ps.PaperName.IndexOf("80", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            doc.DefaultPageSettings.PaperSize = ps;
                            break;
                        }
                    }
                }
                catch { /* fallback default paper */ }

                Font font = null;
                Font headerFont = null;
                Image siiImg = null;
                try
                {
                    var siiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "sii_qrcode.png");
                    if (File.Exists(siiPath))
                    {
                        try { siiImg = Image.FromFile(siiPath); }
                        catch { siiImg = null; }
                    }
                }
                catch { siiImg = null; }

                try
                {
                    // Courier New prima (coerente col preview fiscale), poi fallback Consolas
                    try { font = new Font("Courier New", 9f); }
                    catch { font = new Font("Consolas", 9f); }
                    try { headerFont = new Font(font.FontFamily, 10f, FontStyle.Bold); }
                    catch { headerFont = new Font(font.FontFamily, 9f, FontStyle.Bold); }

                    bool headerDone = false;
                    bool useReceiptHeader = opt.UseReceiptHeaderStyle;

                    doc.PrintPage += (s, e) =>
                    {
                        float x = e.PageSettings.HardMarginX;
                        float y = e.PageSettings.HardMarginY;
                        float bodyLineHeight = font.GetHeight(e.Graphics);
                        float headerLineHeight = headerFont.GetHeight(e.Graphics);
                        float bottom = e.PageBounds.Bottom - e.PageSettings.HardMarginY;
                        float printableW = e.PageBounds.Width - e.PageSettings.HardMarginX * 2f;

                        while (lineIndex < lines.Length)
                        {
                            var line = lines[lineIndex] ?? "";

                            // Solo per scontrino: prima riga non vuota = nome negozio (grassetto e più grande, centrata)
                            if (useReceiptHeader && !headerDone && !string.IsNullOrWhiteSpace(line))
                            {
                                var text = line.Trim();
                                var size = e.Graphics.MeasureString(text, headerFont);
                                float hx = x + (printableW - size.Width) / 2f;
                                if (hx < x) hx = x;
                                if (y + headerLineHeight > bottom)
                                    break;
                                e.Graphics.DrawString(text, headerFont, Brushes.Black, hx, y);
                                e.Graphics.DrawString(text, headerFont, Brushes.Black, hx + 0.6f, y); // simulated bold
                                y += headerLineHeight;
                                headerDone = true;
                                lineIndex++;
                                continue;
                            }

                            // Se riga marker -> stampa PRIMA il QR, POI la riga "Timbre..." (come nel PDF)
                            if (IsSiiMarker(line) && siiImg != null)
                            {
                                float w = MmToHundredthsInch(QrWidthMm);
                                if (w > printableW) w = printableW;

                                float h = w * siiImg.Height / (float)siiImg.Width;
                                float gapTop = MmToHundredthsInch(QrTopGapMm);
                                float gapBottom = MmToHundredthsInch(QrBottomGapMm);

                                // serve spazio per: gap + img + gap + 1 riga testo
                                if (y + gapTop + h + gapBottom + bodyLineHeight > bottom)
                                    break;

                                // gap sopra
                                y += gapTop;

                                // QR centrato
                                float imgX = e.PageSettings.HardMarginX + (printableW - w) / 2f;
                                e.Graphics.DrawImage(siiImg, imgX, y, w, h);
                                y += h + gapBottom;

                                // ORA stampa "Timbre..."
                                e.Graphics.DrawString(line, font, Brushes.Black, x, y);
                                y += bodyLineHeight;

                                lineIndex++;
                                continue;
                            }

                            if (y + bodyLineHeight > bottom)
                                break;

                            // Non stampare la riga placeholder (rimossa dalla generazione; ignora se presente in testi vecchi)
                            if (IsCode128Placeholder(line))
                            {
                                lineIndex++;
                                continue;
                            }

                            e.Graphics.DrawString(line, font, Brushes.Black, x, y);
                            y += bodyLineHeight;
                            lineIndex++;
                        }

                        if (lineIndex >= lines.Length && !barcodePrinted)
                        {
                            using (var barcodeBmp = Code128Renderer.Render(saleCodeForBarcode))
                            {
                                if (barcodeBmp != null)
                                {
                                    float barW = printableW;
                                    float gap = MmToHundredthsInch(SaleCodeBarcodeGapMm);
                                    float barH = barW * barcodeBmp.Height / (float)barcodeBmp.Width;

                                    if (y + gap + barH > bottom)
                                    {
                                        e.HasMorePages = true;
                                        return;
                                    }

                                    y += gap;
                                    float barX = e.PageSettings.HardMarginX + (printableW - barW) / 2f;
                                    e.Graphics.DrawImage(barcodeBmp, barX, y, barW, barH);
                                }
                            }

                            barcodePrinted = true;
                        }

                        e.HasMorePages = lineIndex < lines.Length || !barcodePrinted;
                    };

                    doc.EndPrint += (s, e) =>
                    {
                        if (font != null) font.Dispose();
                        font = null;
                        if (headerFont != null) headerFont.Dispose();
                        headerFont = null;

                        if (siiImg != null) siiImg.Dispose();
                        siiImg = null;
                    };

                    doc.Print();
                }
                finally
                {
                    if (font != null) font.Dispose();
                    if (headerFont != null) headerFont.Dispose();
                    if (siiImg != null) siiImg.Dispose();
                }
            }
        }
    }
}
