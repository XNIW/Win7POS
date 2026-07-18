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

        private const byte EscPosEscape = 27;
        private const byte EscPosPulse = 112;

        public async Task OpenCashDrawerAsync(ReceiptPrintOptions opt)
        {
            if (opt == null) return;
            var cmd = (opt.CashDrawerCommand ?? string.Empty).Trim();
            var bytes = ParseCashDrawerCommand(cmd);
            var drawerTask = Task.Run(() => TryOpenCashDrawer(opt.PrinterName, bytes, cmd));
            var completed = await Task.WhenAny(
                drawerTask,
                Task.Delay(PrintAttemptTimeoutMilliseconds)).ConfigureAwait(false);
            if (completed != drawerTask)
            {
                // Do not retry: the driver may still emit the original pulse.
                throw new TimeoutException(PosLocalization.T("printer.drawerTimedOut"));
            }

            await drawerTask.ConfigureAwait(false);
        }

        public static bool IsCashDrawerCommandValid(string command)
        {
            try
            {
                ParseCashDrawerCommand(command);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
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
            if (string.IsNullOrWhiteSpace(cmd))
                throw InvalidCashDrawerCommand();

            // Accetta la sintassi storica con virgole, punti e virgola o spazi, ma
            // non ignora mai token vuoti o caratteri non numerici. Un comando non
            // vuoto malformato deve fallire prima di raggiungere lo spooler.
            if (cmd.Any(ch => !(ch >= '0' && ch <= '9') && ch != ',' && ch != ';' && ch != ' ' && ch != '\t'))
                throw InvalidCashDrawerCommand();

            var list = new List<byte>();
            var groups = cmd.Split(new[] { ',', ';' }, StringSplitOptions.None);
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group))
                    throw InvalidCashDrawerCommand();

                var parts = group.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                        throw InvalidCashDrawerCommand();
                    list.Add(b);
                }
            }

            if (list.Count != 5 ||
                list[0] != EscPosEscape ||
                list[1] != EscPosPulse ||
                (list[2] != 0 && list[2] != 1 && list[2] != 48 && list[2] != 49) ||
                list[3] >= list[4])
            {
                throw InvalidCashDrawerCommand();
            }

            return list.ToArray();
        }

        private static FormatException InvalidCashDrawerCommand()
        {
            return new FormatException(
                "Invalid ESC/POS cash-drawer command. Expected 27,112,m,t1,t2 with m 0/1/48/49, byte timings 0..255 and t1 < t2.");
        }

        private const int RetryDelayMs = 300;
        private const int PrintAttemptTimeoutMilliseconds = 5000;
        private const int ThermalPaper80mmMin = 300;
        private const int ThermalPaper80mmMax = 330;
        private const int DefaultCharactersPerLine = 42;
        private const int MinimumCharactersPerLine = 16;
        private const int MaximumCharactersPerLine = 96;
        private const float MinimumReceiptFontSizePoints = 5f;
        private const float PrintableWidthSafetyFactor = 0.97f;

        private static async Task TryPrintWithRetryAsync(string receiptText, ReceiptPrintOptions opt)
        {
            try
            {
                await PrintOnceWithinTimeoutAsync(receiptText, opt).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A timed-out spooler call can still submit later. Retrying would
                // make the physical outcome ambiguous and risks a duplicate receipt.
                throw;
            }
            catch
            {
                await Task.Delay(RetryDelayMs).ConfigureAwait(false);
                await PrintOnceWithinTimeoutAsync(receiptText, opt).ConfigureAwait(false);
            }
        }

        private static async Task PrintOnceWithinTimeoutAsync(string receiptText, ReceiptPrintOptions opt)
        {
            var printTask = Task.Run(() => PrintOnce(receiptText, opt));
            var completed = await Task.WhenAny(
                printTask,
                Task.Delay(PrintAttemptTimeoutMilliseconds)).ConfigureAwait(false);
            if (completed != printTask)
            {
                throw new TimeoutException(PosLocalization.T("printer.printTimedOut"));
            }

            await printTask.ConfigureAwait(false);
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
                StringFormat bodyFormat = null;
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
                    bodyFormat = new StringFormat(StringFormat.GenericTypographic)
                    {
                        FormatFlags = StringFormatFlags.MeasureTrailingSpaces |
                                      StringFormatFlags.NoWrap
                    };

                    bool headerDone = false;
                    bool useReceiptHeader = opt.UseReceiptHeaderStyle;
                    bool bodyFontFitted = false;

                    doc.PrintPage += (s, e) =>
                    {
                        // The printer Graphics origin is already translated to the
                        // printable surface. Adding HardMarginX/Y again clips Epson
                        // output twice, so all layout uses the visible graphics bounds.
                        var visibleBounds = e.Graphics.VisibleClipBounds;
                        float x = visibleBounds.Left;
                        float y = visibleBounds.Top;
                        float printableW = visibleBounds.Width;
                        float bottom = visibleBounds.Bottom;

                        // ReceiptFormatter produces fixed-column rows. Fit that declared
                        // column count to the driver's real printable area (not merely the
                        // nominal 80 mm paper width), otherwise Epson hard margins clip the
                        // right-hand characters instead of wrapping them.
                        if (!bodyFontFitted)
                        {
                            var oldFont = font;
                            font = CreateColumnFittedFont(
                                e.Graphics,
                                oldFont,
                                opt.CharactersPerLine,
                                printableW,
                                bodyFormat);
                            oldFont.Dispose();

                            var oldHeaderFont = headerFont;
                            try
                            {
                                headerFont = new Font(
                                    font.FontFamily,
                                    Math.Max(font.Size + 1f, font.Size * 1.12f),
                                    FontStyle.Bold);
                            }
                            catch
                            {
                                headerFont = new Font(font.FontFamily, font.Size, FontStyle.Bold);
                            }
                            oldHeaderFont.Dispose();
                            bodyFontFitted = true;
                        }

                        float bodyLineHeight = font.GetHeight(e.Graphics);

                        while (lineIndex < lines.Length)
                        {
                            var line = lines[lineIndex] ?? "";

                            // Solo per scontrino: prima riga non vuota = nome negozio (grassetto e più grande, centrata)
                            if (useReceiptHeader && !headerDone && !string.IsNullOrWhiteSpace(line))
                            {
                                var text = line.Trim();
                                using (var fittedHeaderFont = CreateTextFittedFont(
                                    e.Graphics,
                                    headerFont,
                                    text,
                                    printableW,
                                    bodyFormat))
                                {
                                    var headerLineHeight = fittedHeaderFont.GetHeight(e.Graphics);
                                    var size = e.Graphics.MeasureString(
                                        text,
                                        fittedHeaderFont,
                                        PointF.Empty,
                                        bodyFormat);
                                    float hx = x + (printableW - size.Width) / 2f;
                                    if (hx < x) hx = x;
                                    if (y + headerLineHeight > bottom)
                                        break;
                                    e.Graphics.DrawString(text, fittedHeaderFont, Brushes.Black, hx, y, bodyFormat);
                                    e.Graphics.DrawString(text, fittedHeaderFont, Brushes.Black, hx + 0.6f, y, bodyFormat); // simulated bold
                                    y += headerLineHeight;
                                }
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
                                float imgX = x + (printableW - w) / 2f;
                                e.Graphics.DrawImage(siiImg, imgX, y, w, h);
                                y += h + gapBottom;

                                // ORA stampa "Timbre..."
                                e.Graphics.DrawString(line, font, Brushes.Black, x, y, bodyFormat);
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

                            e.Graphics.DrawString(line, font, Brushes.Black, x, y, bodyFormat);
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
                                    float barX = x + (printableW - barW) / 2f;
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
                        if (bodyFormat != null) bodyFormat.Dispose();
                        bodyFormat = null;
                    };

                    doc.Print();
                }
                finally
                {
                    if (font != null) font.Dispose();
                    if (headerFont != null) headerFont.Dispose();
                    if (siiImg != null) siiImg.Dispose();
                    if (bodyFormat != null) bodyFormat.Dispose();
                }
            }
        }

        private static Font CreateColumnFittedFont(
            Graphics graphics,
            Font sourceFont,
            int charactersPerLine,
            float printableWidth,
            StringFormat format)
        {
            if (graphics == null) throw new ArgumentNullException(nameof(graphics));
            if (sourceFont == null) throw new ArgumentNullException(nameof(sourceFont));
            if (format == null) throw new ArgumentNullException(nameof(format));

            var requestedColumns = charactersPerLine <= 0
                ? DefaultCharactersPerLine
                : charactersPerLine;
            var columns = Math.Max(
                MinimumCharactersPerLine,
                Math.Min(MaximumCharactersPerLine, requestedColumns));
            return CreateSampleFittedFont(
                graphics,
                sourceFont,
                new string('W', columns),
                printableWidth,
                format);
        }

        private static Font CreateTextFittedFont(
            Graphics graphics,
            Font sourceFont,
            string text,
            float printableWidth,
            StringFormat format)
        {
            return CreateSampleFittedFont(
                graphics,
                sourceFont,
                string.IsNullOrEmpty(text) ? "W" : text,
                printableWidth,
                format);
        }

        private static Font CreateSampleFittedFont(
            Graphics graphics,
            Font sourceFont,
            string sample,
            float printableWidth,
            StringFormat format)
        {
            if (graphics == null) throw new ArgumentNullException(nameof(graphics));
            if (sourceFont == null) throw new ArgumentNullException(nameof(sourceFont));
            if (format == null) throw new ArgumentNullException(nameof(format));

            var measuredWidth = graphics.MeasureString(sample, sourceFont, PointF.Empty, format).Width;
            var targetWidth = Math.Max(1f, printableWidth * PrintableWidthSafetyFactor);
            var fittedSize = sourceFont.Size;
            if (measuredWidth > targetWidth && measuredWidth > 0f)
            {
                fittedSize = Math.Max(
                    MinimumReceiptFontSizePoints,
                    sourceFont.Size * targetWidth / measuredWidth);
            }

            return new Font(sourceFont.FontFamily, fittedSize, sourceFont.Style, sourceFont.Unit);
        }
    }
}
