using System;
using System.Windows;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>
    /// Helper per dimensionare finestre e dialog in modo proporzionale allo schermo
    /// rispettando l'area di lavoro (es. senza essere tagliati dalla taskbar).
    /// </summary>
    public static class WindowSizingHelper
    {
        /// <summary>
        /// Imposta la finestra principale a schermo intero con dimensioni minime quando ridotta.
        /// </summary>
        public static void ApplyMainWindowSizing(Window window)
        {
            window.MinWidth = 1024;
            window.MinHeight = 600;
            window.WindowState = WindowState.Maximized;
        }

        /// <summary>
        /// Restituisce l'area di lavoro del monitor primario (schermo meno taskbar/dock).
        /// </summary>
        private static Rect GetWorkArea()
        {
            try
            {
                return SystemParameters.WorkArea;
            }
            catch
            {
                return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            }
        }

        /// <summary>
        /// Imposta dimensioni responsive per un dialog: non supera l'area di lavoro,
        /// si centra e non viene tagliato dalla taskbar.
        /// </summary>
        /// <param name="window">La finestra dialog</param>
        /// <param name="widthPercent">Percentuale larghezza (0.0-1.0), es. 0.7 = 70%</param>
        /// <param name="heightPercent">Percentuale altezza (0.0-1.0)</param>
        /// <param name="minWidth">Larghezza minima</param>
        /// <param name="minHeight">Altezza minima</param>
        /// <param name="maxWidth">Larghezza massima (0 = illimitata)</param>
        /// <param name="maxHeight">Altezza massima (0 = illimitata)</param>
        public static void ApplyDialogSizing(Window window,
            double widthPercent = 0.6, double heightPercent = 0.7,
            double minWidth = 400, double minHeight = 320,
            double maxWidth = 0, double maxHeight = 0)
        {
            window.Loaded += (s, e) =>
            {
                var work = GetWorkArea();
                // Usa l'area di lavoro così il dialog non viene mai tagliato dalla taskbar
                double refWidth = work.Width;
                double refHeight = work.Height;

                var target = window.Owner ?? Application.Current?.MainWindow;
                if (target != null && target.IsLoaded && target.ActualWidth > 0 && target.ActualHeight > 0)
                {
                    // Limita al minimo tra area di lavoro e dimensione owner
                    refWidth = Math.Min(refWidth, target.ActualWidth);
                    refHeight = Math.Min(refHeight, target.ActualHeight);
                }

                var w = Math.Max(minWidth, refWidth * widthPercent);
                var h = Math.Max(minHeight, refHeight * heightPercent);
                // Non superare mai l'area di lavoro
                w = Math.Min(w, work.Width);
                h = Math.Min(h, work.Height);
                if (maxWidth > 0) w = Math.Min(maxWidth, w);
                if (maxHeight > 0) h = Math.Min(maxHeight, h);

                window.Width = w;
                window.Height = h;
                window.MinWidth = minWidth;
                window.MinHeight = minHeight;
                if (maxWidth > 0) window.MaxWidth = maxWidth;
                if (maxHeight > 0) window.MaxHeight = maxHeight;

                // Centra nella area di lavoro e clampa così non esce mai dallo schermo
                var left = work.Left + (work.Width - w) / 2;
                var top = work.Top + (work.Height - h) / 2;
                window.Left = Math.Max(work.Left, Math.Min(work.Right - w, left));
                window.Top = Math.Max(work.Top, Math.Min(work.Bottom - h, top));
            };
        }
    }
}
