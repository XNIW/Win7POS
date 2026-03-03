using System.Windows;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>
    /// Helper per dimensionare finestre e dialog in modo proporzionale allo schermo.
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
        /// Imposta dimensioni responsive per un dialog basate sullo schermo (o sulla finestra Owner).
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
                var target = window.Owner ?? Application.Current?.MainWindow;
                double refWidth, refHeight;

                if (target != null && target.IsLoaded && target.ActualWidth > 0)
                {
                    refWidth = target.ActualWidth;
                    refHeight = target.ActualHeight;
                }
                else
                {
                    refWidth = SystemParameters.PrimaryScreenWidth;
                    refHeight = SystemParameters.PrimaryScreenHeight;
                }

                var w = System.Math.Max(minWidth, refWidth * widthPercent);
                var h = System.Math.Max(minHeight, refHeight * heightPercent);
                if (maxWidth > 0) w = System.Math.Min(maxWidth, w);
                if (maxHeight > 0) h = System.Math.Min(maxHeight, h);

                window.Width = w;
                window.Height = h;
                window.MinWidth = minWidth;
                window.MinHeight = minHeight;
                if (maxWidth > 0) window.MaxWidth = maxWidth;
                if (maxHeight > 0) window.MaxHeight = maxHeight;
            };
        }
    }
}
