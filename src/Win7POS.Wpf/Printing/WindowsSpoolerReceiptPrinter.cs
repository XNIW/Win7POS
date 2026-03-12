using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Win7POS.Wpf.Printing
{
    public sealed class WindowsSpoolerReceiptPrinter : IReceiptPrinter
    {
        private const string SiiMarker = "Timbre Electrónico SII";
        private const string ScontrinoPrefix = "Scontrino:";

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

        private static bool TryGetScontrinoSaleCode(string line, out string saleCode)
        {
            saleCode = null;
            var t = (line ?? "").Trim();
            if (!t.StartsWith(ScontrinoPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            saleCode = t.Substring(ScontrinoPrefix.Length).Trim();
            return saleCode.Length > 0;
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

        public Task OpenCashDrawerAsync(ReceiptPrintOptions opt)
        {
            if (opt == null) return Task.CompletedTask;
            var bytes = ParseCashDrawerCommand(opt.CashDrawerCommand);
            return Task.Run(() => TryOpenCashDrawer(opt.PrinterName, bytes));
        }

        /// <summary>ESC/POS kick drawer: ESC p m t1 t2. Comando configurabile (es. 27,112,0,60,255) o default 27,112,0,25,25.</summary>
        private static void TryOpenCashDrawer(string printerName, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(printerName) || bytes == null || bytes.Length == 0) return;
            try
            {
                RawPrinterHelper.SendBytesToPrinter(printerName, bytes);
            }
            catch
            {
                // Driver potrebbe non supportare raw; no-op silenzioso
            }
        }

        private static byte[] ParseCashDrawerCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return new byte[] { 27, 112, 0, 25, 25 };
            var parts = cmd.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<byte>();
            foreach (var p in parts)
            {
                if (byte.TryParse(p.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                    list.Add(b);
            }
            return list.Count > 0 ? list.ToArray() : new byte[] { 27, 112, 0, 25, 25 };
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
            var lines = receiptText.Replace("\r\n", "\n").Split('\n');
            var lineIndex = 0;
            // Barcode solo sull'ultima riga "Scontrino: XXX" (in fondo allo scontrino)
            int lastScontrinoIndex = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (TryGetScontrinoSaleCode(lines[i] ?? "", out _)) { lastScontrinoIndex = i; break; }
            }

            using (var doc = new PrintDocument())
            {
                if (!string.IsNullOrWhiteSpace(opt.PrinterName))
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
                                e.Graphics.DrawString(text, headerFont, Brushes.Black, hx + 0.6f, y); // fake bold
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

                            // Ultima riga "Scontrino: <code>" → testo + barcode Code128 (larghezza = foglio per lettura scanner)
                            if (TryGetScontrinoSaleCode(line, out var saleCode))
                            {
                                if (lineIndex == lastScontrinoIndex)
                                {
                                    using (var barcodeBmp = Code128Renderer.Render(saleCode))
                                    {
                                        float barW = printableW;
                                        float gap = MmToHundredthsInch(SaleCodeBarcodeGapMm);

                                        float needH = bodyLineHeight + gap + (barcodeBmp != null ? barW * barcodeBmp.Height / (float)barcodeBmp.Width : 0f);
                                        if (y + needH > bottom)
                                            break;

                                        e.Graphics.DrawString(line, font, Brushes.Black, x, y);
                                        y += bodyLineHeight + gap;

                                        if (barcodeBmp != null)
                                        {
                                            float barH = barW * barcodeBmp.Height / (float)barcodeBmp.Width;
                                            float barX = e.PageSettings.HardMarginX + (printableW - barW) / 2f;
                                            e.Graphics.DrawImage(barcodeBmp, barX, y, barW, barH);
                                            y += barH;
                                        }
                                    }
                                }
                                else
                                {
                                    if (y + bodyLineHeight > bottom) break;
                                    e.Graphics.DrawString(line, font, Brushes.Black, x, y);
                                    y += bodyLineHeight;
                                }
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

                        e.HasMorePages = lineIndex < lines.Length;
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
