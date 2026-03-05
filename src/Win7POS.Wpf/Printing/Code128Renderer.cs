using System;
using System.Drawing;
using ZXing;
using ZXing.Windows.Compatibility;

namespace Win7POS.Wpf.Printing
{
    /// <summary>Genera un'immagine Code128 dal codice scontrino (SaleCode) per stampa termica e scan reso.</summary>
    public static class Code128Renderer
    {
        private const int DefaultWidth = 280;
        private const int DefaultHeight = 56;

        /// <summary>Genera un Bitmap Code128 per il testo dato. Restituisce null se fallisce.</summary>
        public static Bitmap Render(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            try
            {
                var writer = new ZXing.Windows.Compatibility.BarcodeWriter
                {
                    Format = BarcodeFormat.CODE_128,
                    Options = new ZXing.Common.EncodingOptions { Width = DefaultWidth, Height = DefaultHeight, Margin = 2 },
                    Renderer = new BitmapRenderer()
                };
                return writer.Write(content);
            }
            catch
            {
                return null;
            }
        }
    }
}
