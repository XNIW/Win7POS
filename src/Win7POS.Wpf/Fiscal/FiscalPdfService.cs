using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Win7POS.Core;

namespace Win7POS.Wpf.Fiscal
{
    public sealed class FiscalPdfService
    {
        private const double PageW = 226.77;
        private const double PageH = 479.28;

        public Task<string> GenerateFiscalPdfAsync(string fiscalText, string saleCode)
        {
            var dir = AppPaths.ExportsDirectory;
            Directory.CreateDirectory(dir);
            var safeCode = string.IsNullOrWhiteSpace(saleCode) ? "sale" : saleCode.Replace("/", "-").Replace("\\", "-");
            var fileName = $"Boleta_{safeCode}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            var path = Path.Combine(dir, fileName);

            var doc = new PdfDocument();
            var page = doc.AddPage();
            page.Width = PageW;
            page.Height = PageH;

            var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Courier New", 10, XFontStyle.Regular);

            double marginLeft = 12;
            double y = 18;
            double lineSpacing = 14;

            var lines = (fiscalText ?? "")
                .Replace("\r\n", "\n").Replace("\r", "\n")
                .Split('\n');

            var footerIdx = Array.FindIndex(lines, s => (s ?? "").Trim().Equals("Timbre Electrónico SII", StringComparison.OrdinalIgnoreCase));
            if (footerIdx < 0) footerIdx = lines.Length;

            for (int i = 0; i < footerIdx; i++)
            {
                gfx.DrawString(lines[i] ?? "", font, XBrushes.Black, new XPoint(marginLeft, y));
                y += lineSpacing;
            }

            TryDrawSiiImage(gfx, page, ref y);

            for (int i = footerIdx; i < lines.Length; i++)
            {
                gfx.DrawString(lines[i] ?? "", font, XBrushes.Black, new XPoint(marginLeft, y));
                y += lineSpacing;
            }

            doc.Save(path);
            doc.Close();
            return Task.FromResult(path);
        }

        private static void TryDrawSiiImage(XGraphics gfx, PdfPage page, ref double y)
        {
            try
            {
                var imgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "sii_qrcode.png");
                if (!File.Exists(imgPath)) return;

                using (var img = XImage.FromFile(imgPath))
                {
                    // ~60mm su carta 80mm: 60/25.4*72 = 170.08pt
                    const double targetW = 170.0;

                    // clamp solo per sicurezza (margini)
                    var maxW = page.Width - 24;
                    var w = Math.Min(targetW, maxW);
                    var h = w * img.PixelHeight / (double)img.PixelWidth;

                    var x = (page.Width - w) / 2.0;

                    y += 8;
                    gfx.DrawImage(img, x, y, w, h);
                    y += h + 10;
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
