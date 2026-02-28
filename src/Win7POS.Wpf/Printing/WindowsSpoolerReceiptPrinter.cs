using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Threading.Tasks;

namespace Win7POS.Wpf.Printing
{
    public sealed class WindowsSpoolerReceiptPrinter : IReceiptPrinter
    {
        public Task PrintAsync(string receiptText, ReceiptPrintOptions opt)
        {
            return Task.Run(() =>
            {
                var text = receiptText ?? string.Empty;
                var options = opt ?? new ReceiptPrintOptions();

                if (options.SaveCopyToFile && !string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    var dir = Path.GetDirectoryName(options.OutputPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(options.OutputPath, text);
                }

                var lines = text.Replace("\r\n", "\n").Split('\n');
                var index = 0;
                using (var doc = new PrintDocument())
                using (var font = CreateFont())
                {
                    if (!string.IsNullOrWhiteSpace(options.PrinterName))
                        doc.PrinterSettings.PrinterName = options.PrinterName;

                    if (options.Copies > 0 && options.Copies <= short.MaxValue)
                        doc.PrinterSettings.Copies = (short)options.Copies;

                    doc.DocumentName = "Win7POS Receipt";
                    doc.PrintPage += (sender, e) =>
                    {
                        var y = e.MarginBounds.Top;
                        var lineHeight = font.GetHeight(e.Graphics);
                        var maxY = e.MarginBounds.Bottom;
                        while (index < lines.Length && y + lineHeight <= maxY)
                        {
                            e.Graphics.DrawString(lines[index] ?? string.Empty, font, Brushes.Black, e.MarginBounds.Left, y);
                            y += lineHeight;
                            index += 1;
                        }

                        e.HasMorePages = index < lines.Length;
                    };
                    doc.Print();
                }
            });
        }

        private static Font CreateFont()
        {
            try
            {
                return new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch
            {
                return new Font("Courier New", 9f, FontStyle.Regular, GraphicsUnit.Point);
            }
        }
    }
}
