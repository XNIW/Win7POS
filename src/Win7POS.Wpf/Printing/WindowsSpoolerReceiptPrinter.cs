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
            await Task.Run(() =>
            {
                TryPrintWithRetry(receiptText, opt);
            }).ConfigureAwait(false);
        }

        private static void TryPrintWithRetry(string receiptText, ReceiptPrintOptions opt)
        {
            try
            {
                PrintOnce(receiptText, opt);
            }
            catch
            {
                // light retry once
                Thread.Sleep(300);
                PrintOnce(receiptText, opt);
            }
        }

        private static void PrintOnce(string receiptText, ReceiptPrintOptions opt)
        {
            var lines = receiptText.Replace("\r\n", "\n").Split('\n');
            var lineIndex = 0;

            using (var doc = new PrintDocument())
            {
                // default printer: do NOT set PrinterName if empty
                if (!string.IsNullOrWhiteSpace(opt.PrinterName))
                    doc.PrinterSettings.PrinterName = opt.PrinterName;

                doc.PrinterSettings.Copies = (short)Math.Max(1, opt.Copies);

                Font font = null;
                try
                {
                    // prefer monospace
                    try { font = new Font("Consolas", 10f); }
                    catch { font = new Font("Courier New", 10f); }

                    doc.PrintPage += (s, e) =>
                    {
                        float y = e.MarginBounds.Top;
                        float lineHeight = font.GetHeight(e.Graphics);

                        while (lineIndex < lines.Length)
                        {
                            if (y + lineHeight > e.MarginBounds.Bottom)
                                break;

                            e.Graphics.DrawString(lines[lineIndex], font, Brushes.Black, e.MarginBounds.Left, y);
                            y += lineHeight;
                            lineIndex++;
                        }

                        e.HasMorePages = lineIndex < lines.Length;
                    };

                    doc.EndPrint += (s, e) =>
                    {
                        if (font != null) font.Dispose();
                        font = null;
                    };

                    doc.Print();
                }
                finally
                {
                    if (font != null) font.Dispose();
                }
            }
        }
    }
}
