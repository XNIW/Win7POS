using System;
using System.Windows;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>
    /// Helper per dimensionare finestre e dialog in modo proporzionale allo schermo
    /// rispettando l'area di lavoro (es. senza essere tagliati dalla taskbar).
    /// I dialog si dimensionano in base al contenuto e allo schermo disponibile (WorkArea),
    /// non in base alla dimensione della finestra Owner.
    /// </summary>
    public static class WindowSizingHelper
    {
        private const double MainWindowMinWidth = 1024;
        private const double MainWindowMinHeight = 600;
        private const double CapMaxHeightMargin = 48;
        private const double CapMaxHeightMin = 200;

        /// <summary>
        /// Imposta la finestra principale a schermo intero con dimensioni minime quando ridotta.
        /// </summary>
        public static void ApplyMainWindowSizing(Window window)
        {
            window.MinWidth = MainWindowMinWidth;
            window.MinHeight = MainWindowMinHeight;
            window.WindowState = WindowState.Maximized;
        }

        /// <summary>
        /// Dimensionamento adattivo per dialog: SizeToContent, tetto massimo da WorkArea (non da Owner).
        /// Il dialog si adatta al contenuto; se supera lo schermo è ridimensionabile con scroll interno.
        /// </summary>
        /// <param name="window">La finestra dialog</param>
        /// <param name="minWidth">Larghezza minima</param>
        /// <param name="minHeight">Altezza minima</param>
        /// <param name="maxWidthPercent">Percentuale dell'area di lavoro per MaxWidth (es. 0.92)</param>
        /// <param name="maxHeightPercent">Percentuale dell'area di lavoro per MaxHeight</param>
        /// <param name="allowResize">Se true il dialog è ridimensionabile</param>
        public static void ApplyAdaptiveDialogSizing(
            Window window,
            double minWidth = 420,
            double minHeight = 260,
            double maxWidthPercent = 0.92,
            double maxHeightPercent = 0.92,
            bool allowResize = true)
        {
            if (window == null) throw new ArgumentNullException("window");
            if (window is DialogShellWindow dlg && dlg.UseModalOverlay)
                return;

            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.MinWidth = minWidth;
            window.MinHeight = minHeight;
            window.ResizeMode = allowResize ? ResizeMode.CanResize : ResizeMode.NoResize;

            window.SourceInitialized += (s, e) =>
            {
                var work = MonitorHelper.GetWorkAreaOrPrimary(window);
                window.MaxWidth = Math.Max(minWidth, work.Width * maxWidthPercent);
                window.MaxHeight = Math.Max(minHeight, work.Height * maxHeightPercent);
            };
        }

        /// <summary>
        /// Limita l'altezza massima del dialog a quella della finestra Owner (schermata POS),
        /// così nessun dialog supera l'altezza della finestra principale.
        /// Va chiamato dopo aver impostato Owner e prima di ShowDialog().
        /// </summary>
        /// <param name="window">Dialog da limitare</param>
        /// <param name="margin">Margine verticale da sottrarre all'altezza Owner (default 48)</param>
        public static void CapMaxHeightToOwner(Window window, double margin = CapMaxHeightMargin)
        {
            if (window == null) return;
            if (window is DialogShellWindow dlg && dlg.UseModalOverlay) return;
            window.SourceInitialized += (s, e) =>
            {
                if (window.Owner == null || window.Owner.ActualHeight <= 0) return;
                var maxFromOwner = Math.Max(CapMaxHeightMin, window.Owner.ActualHeight - margin);
                var currentMax = window.MaxHeight;
                if (double.IsNaN(currentMax) || currentMax > maxFromOwner || double.IsPositiveInfinity(currentMax))
                    window.MaxHeight = maxFromOwner;
            };
        }
    }
}
