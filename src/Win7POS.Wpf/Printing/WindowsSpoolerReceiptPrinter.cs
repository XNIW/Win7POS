using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Win7POS.Wpf.Printing
{
    public sealed class WindowsSpoolerReceiptPrinter : IReceiptPrinter
    {
        private readonly object _printSync = new object();
        private int _lineIndex;

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

                try
                using (var font = CreateFont())
                {
                    PrintOnce(text, options, font);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("Receipt print failed, retrying once: " + ex);
                    Thread.Sleep(300);
                    using (var font = CreateFont())
                    {
                        PrintOnce(text, options, font);
                    }
                }
            });
        }

        private void PrintOnce(string text, ReceiptPrintOptions options, Font font)
        {
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            lock (_printSync)
            {
                _lineIndex = 0;
                using (var doc = new PrintDocument())
                {
                    if (!string.IsNullOrWhiteSpace(options.PrinterName))
                        doc.PrinterSettings.PrinterName = options.PrinterName;

                    if (options.Copies > 0 && options.Copies <= short.MaxValue)
                        doc.PrinterSettings.Copies = (short)options.Copies;

                    doc.DocumentName = "Win7POS Receipt";
                    doc.PrintPage += (sender, e) =>
                    {
                        var bounds = e.MarginBounds;
                        var y = bounds.Top;
                        var lineHeight = (int)Math.Ceiling(font.GetHeight(e.Graphics));
                        if (lineHeight < 1) lineHeight = 1;

                        while (_lineIndex < lines.Length && y + lineHeight <= bounds.Bottom)
                        {
                            var line = lines[_lineIndex] ?? string.Empty;
                            e.Graphics.DrawString(line, font, Brushes.Black, bounds.Left, y);
                            y += lineHeight;
                            _lineIndex += 1;
                        }

                        if (_lineIndex < lines.Length)
                        {
                            e.HasMorePages = true;
                        }
                        else
                        {
                            e.HasMorePages = false;
                            _lineIndex = 0;
                        }
                    };

                    doc.Print();
                    _lineIndex = 0;
                }
            }
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
