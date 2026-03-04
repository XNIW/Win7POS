using System;
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

        // regolazioni richieste: spazio bianco sopra QR + QR più stretto su carta
        private const float QrWidthMm = 50f;
        private const float QrTopGapMm = 6f;
        private const float QrBottomGapMm = 3f;

        private static bool IsSiiMarker(string line)
            => string.Equals((line ?? "").Trim(), SiiMarker, StringComparison.OrdinalIgnoreCase);

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

                    doc.PrintPage += (s, e) =>
                    {
                        float x = e.PageSettings.HardMarginX;
                        float y = e.PageSettings.HardMarginY;
                        float lineHeight = font.GetHeight(e.Graphics);
                        float bottom = e.PageBounds.Bottom - e.PageSettings.HardMarginY;
                        float printableW = e.PageBounds.Width - e.PageSettings.HardMarginX * 2f;

                        while (lineIndex < lines.Length)
                        {
                            var line = lines[lineIndex] ?? "";

                            // Se riga marker -> stampa PRIMA il QR, POI la riga "Timbre..." (come nel PDF)
                            if (IsSiiMarker(line) && siiImg != null)
                            {
                                float w = MmToHundredthsInch(QrWidthMm);
                                if (w > printableW) w = printableW;

                                float h = w * siiImg.Height / (float)siiImg.Width;
                                float gapTop = MmToHundredthsInch(QrTopGapMm);
                                float gapBottom = MmToHundredthsInch(QrBottomGapMm);

                                // serve spazio per: gap + img + gap + 1 riga testo
                                if (y + gapTop + h + gapBottom + lineHeight > bottom)
                                    break;

                                // gap sopra
                                y += gapTop;

                                // QR centrato
                                float imgX = e.PageSettings.HardMarginX + (printableW - w) / 2f;
                                e.Graphics.DrawImage(siiImg, imgX, y, w, h);
                                y += h + gapBottom;

                                // ORA stampa "Timbre..."
                                e.Graphics.DrawString(line, font, Brushes.Black, x, y);
                                y += lineHeight;

                                lineIndex++;
                                continue;
                            }

                            if (y + lineHeight > bottom)
                                break;

                            e.Graphics.DrawString(line, font, Brushes.Black, x, y);
                            y += lineHeight;
                            lineIndex++;
                        }

                        e.HasMorePages = lineIndex < lines.Length;
                    };

                    doc.EndPrint += (s, e) =>
                    {
                        if (font != null) font.Dispose();
                        font = null;

                        if (siiImg != null) siiImg.Dispose();
                        siiImg = null;
                    };

                    doc.Print();
                }
                finally
                {
                    if (font != null) font.Dispose();
                    if (siiImg != null) siiImg.Dispose();
                }
            }
        }
    }
}
