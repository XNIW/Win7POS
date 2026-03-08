using System;
using System.Windows;
using System.Windows.Controls;

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

            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.MinWidth = minWidth;
            window.MinHeight = minHeight;
            window.ResizeMode = allowResize ? ResizeMode.CanResize : ResizeMode.NoResize;

            window.Loaded += (s, e) =>
            {
                var work = SystemParameters.WorkArea;
                var maxWidth = Math.Max(minWidth, work.Width * maxWidthPercent);
                var maxHeight = Math.Max(minHeight, work.Height * maxHeightPercent);
                window.MaxWidth = maxWidth;
                window.MaxHeight = maxHeight;

                // forza il measure del contenuto
                var fe = window.Content as FrameworkElement;
                if (fe != null)
                {
                    fe.Measure(new Size(maxWidth, maxHeight));
                    var desired = fe.DesiredSize;
                    window.Width = Math.Min(Math.Max(desired.Width + 40, minWidth), maxWidth);
                    window.Height = Math.Min(Math.Max(desired.Height + 60, minHeight), maxHeight);
                }

                // ricentra dopo il sizing finale (rispetto all'owner, ma senza limitare la size all'owner)
                if (window.Owner != null)
                {
                    window.Left = window.Owner.Left + Math.Max(0, (window.Owner.ActualWidth - window.Width) / 2);
                    window.Top = window.Owner.Top + Math.Max(0, (window.Owner.ActualHeight - window.Height) / 2);
                }
            };
        }
    }
}
