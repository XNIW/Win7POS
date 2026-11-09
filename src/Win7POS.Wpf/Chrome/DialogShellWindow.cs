using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Chrome
{
    /// <summary>
    /// Finestra base per dialoghi Win7POS con chrome personalizzato (header viola, pulsanti caption custom).
    /// Implementata interamente in codice per evitare MC6017 (classe XAML non può essere radice di altro XAML).
    /// Usare [ContentProperty] DialogContent; i derivati mettono il proprio XAML come figlio diretto.
    /// </summary>
    [ContentProperty(nameof(DialogContent))]
    public class DialogShellWindow : Window
    {
        private Action<Rect> _refreshConstraints;
        private Border _dialogSurface;
        private bool _geometryReported;
        private static readonly FileLogger GeometryLogger = new FileLogger(nameof(DialogShellWindow));
        public static readonly DependencyProperty DialogContentProperty =
            DependencyProperty.Register(
                nameof(DialogContent),
                typeof(object),
                typeof(DialogShellWindow),
                new PropertyMetadata(null));

        public object DialogContent
        {
            get => GetValue(DialogContentProperty);
            set => SetValue(DialogContentProperty, value);
        }

        public static readonly DependencyProperty ShowHeaderProperty =
            DependencyProperty.Register(
                nameof(ShowHeader),
                typeof(bool),
                typeof(DialogShellWindow),
                new PropertyMetadata(false));

        public bool ShowHeader
        {
            get => (bool)GetValue(ShowHeaderProperty);
            set => SetValue(ShowHeaderProperty, value);
        }

        public static readonly DependencyProperty DialogCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(DialogCornerRadius),
                typeof(CornerRadius),
                typeof(DialogShellWindow),
                new PropertyMetadata(new CornerRadius(8)));

        public CornerRadius DialogCornerRadius
        {
            get => (CornerRadius)GetValue(DialogCornerRadiusProperty);
            set => SetValue(DialogCornerRadiusProperty, value);
        }

        public static readonly DependencyProperty DialogBackgroundProperty =
            DependencyProperty.Register(
                nameof(DialogBackground),
                typeof(Brush),
                typeof(DialogShellWindow),
                new PropertyMetadata(null));

        /// <summary>Brush per lo sfondo della card dialog. Se null, usa #FCFAFE.</summary>
        public Brush DialogBackground
        {
            get => (Brush)GetValue(DialogBackgroundProperty);
            set => SetValue(DialogBackgroundProperty, value);
        }

        public static readonly DependencyProperty DialogBorderBrushProperty =
            DependencyProperty.Register(
                nameof(DialogBorderBrush),
                typeof(Brush),
                typeof(DialogShellWindow),
                new PropertyMetadata(null));

        /// <summary>Brush per il bordo della card. Se null, usa #DDD4E8.</summary>
        public Brush DialogBorderBrush
        {
            get => (Brush)GetValue(DialogBorderBrushProperty);
            set => SetValue(DialogBorderBrushProperty, value);
        }

        public static readonly DependencyProperty UseModalOverlayProperty =
            DependencyProperty.Register(
                nameof(UseModalOverlay),
                typeof(bool),
                typeof(DialogShellWindow),
                new PropertyMetadata(false, (d, e) =>
                {
                    if (d is DialogShellWindow w && (bool)e.NewValue)
                        w.AllowsTransparency = true;
                }));

        /// <summary>Se true, mostra overlay lilla semitrasparente dietro al dialog (richiede Owner).</summary>
        public bool UseModalOverlay
        {
            get => (bool)GetValue(UseModalOverlayProperty);
            set => SetValue(UseModalOverlayProperty, value);
        }

        public DialogShellWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = Brushes.Transparent;
            AllowsTransparency = false;
            WindowStartupLocation = WindowStartupLocation.Manual;

            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            SourceInitialized -= OnSourceInitialized;
            WindowStartupLocation = WindowStartupLocation.Manual;
            BuildChrome();

            MonitorHelper.AddWorkAreaClampHook(this, work => _refreshConstraints?.Invoke(work));
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _refreshConstraints?.Invoke(MonitorHelper.GetWorkAreaForExactWindowOrPrimary(this));
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (_geometryReported || _dialogSurface == null) return;
            _geometryReported = true;
            try
            {
                if (!MonitorHelper.TryReadWindowBoundsPixels(this, out var hwnd, out var work)) return;
                var first = MonitorHelper.GetFirstVisibleBoundsPixels(this);
                var ownerBounds = Owner != null && MonitorHelper.TryReadWindowBoundsPixels(Owner, out var ownerNative, out _) ?
                    ownerNative.ToString(CultureInfo.InvariantCulture) : "none";
                var dpi = VisualTreeHelper.GetDpi(this);
                var buttons = new List<string>(); var remaining = 2048;
                CollectGeometryButtons(_dialogSurface, buttons, ref remaining);
                GeometryLogger.LogInfo("category=dialog.geometry phase=first_render dialog=" + GetType().Name +
                    " owner=" + (Owner?.GetType().Name ?? "none") + " owner_px=[" + ownerBounds + "]" +
                    " dpi=" + (96 * dpi.DpiScaleX).ToString(CultureInfo.InvariantCulture) + "," + (96 * dpi.DpiScaleY).ToString(CultureInfo.InvariantCulture) +
                    " work_px=[" + work.ToString(CultureInfo.InvariantCulture) + "] hwnd_px=[" + hwnd.ToString(CultureInfo.InvariantCulture) +
                    "] surface_px=[" + ScreenBounds(_dialogSurface).ToString(CultureInfo.InvariantCulture) + "] first_hwnd_px=[" +
                    first?.ToString(CultureInfo.InvariantCulture) + "] buttons_px=[" + string.Join(";", buttons) + "]");
            }
            catch (Exception error) { GeometryLogger.LogWarning("Dialog geometry observation unavailable: " + error.GetType().Name); }
        }

        private static Rect ScreenBounds(FrameworkElement element) => new Rect(element.PointToScreen(new Point()),
            element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));

        private static void CollectGeometryButtons(DependencyObject root, List<string> result, ref int remaining)
        {
            if (root is ScrollViewer) return; // Body controls are not footer evidence.
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root) && --remaining >= 0 && result.Count < 16; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button button && button.IsVisible && button.ActualWidth > 0 &&
                    (button.IsDefault || button.IsCancel || ReferenceEquals(button.Style, button.TryFindResource("DialogActionButtonStyle")) ||
                        ReferenceEquals(button.Style, button.TryFindResource("DialogCancelButtonStyle"))))
                    result.Add(ScreenBounds(button).ToString(CultureInfo.InvariantCulture));
                CollectGeometryButtons(child, result, ref remaining);
            }
        }

        private void BuildChrome()
        {
            var showHeader = ShowHeader;
            var radius = DialogCornerRadius;
            var originalSizeToContent = SizeToContent;
            var originalWidth = Width;
            var originalHeight = Height;
            var originalMinWidth = MinWidth;
            var originalMinHeight = MinHeight;
            var originalMaxWidth = MaxWidth;
            var originalMaxHeight = MaxHeight;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = showHeader ? 42 : 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = radius,
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

            var cardBrush = DialogBackground ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCFAFE"));
            var borderBrush = DialogBorderBrush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DDD4E8"));
            var outerBorder = new Border
            {
                Name = "DialogSurface",
                Background = cardBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = radius
            };
            _dialogSurface = outerBorder;

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = showHeader ? new GridLength(42) : new GridLength(0)
            });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            if (showHeader)
            {
                var headerBorder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6F4B8B")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5E3D79")),
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };
                Grid.SetRow(headerBorder, 0);

                var headerGrid = new Grid();
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleBlock = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 0, 0),
                    Foreground = Brushes.White,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold
                };
                titleBlock.SetBinding(TextBlock.TextProperty, new Binding("Title") { Source = this });
                Grid.SetColumn(titleBlock, 0);

                var closeButton = new Button
                {
                    Content = "✕",
                    Width = 36,
                    Height = 30,
                    ToolTip = PosLocalization.T("common.close")
                };
                AutomationProperties.SetName(closeButton, PosLocalization.T("common.close"));
                var closeStyle = (Style)Application.Current.FindResource("DialogCaptionCloseButtonStyle");
                if (closeStyle != null)
                    closeButton.Style = closeStyle;
                WindowChrome.SetIsHitTestVisibleInChrome(closeButton, true);
                closeButton.Click += Close_Click;

                var headerButtonsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                headerButtonsPanel.Children.Add(closeButton);
                WindowChrome.SetIsHitTestVisibleInChrome(headerButtonsPanel, true);
                Grid.SetColumn(headerButtonsPanel, 1);

                headerGrid.Children.Add(titleBlock);
                headerGrid.Children.Add(headerButtonsPanel);
                headerBorder.Child = headerGrid;
                headerBorder.MouseLeftButtonDown += (s, ev) => Header_MouseLeftButtonDown(closeButton, s, ev);
                mainGrid.Children.Add(headerBorder);
            }

            var contentHost = new ContentPresenter
            {
                Margin = new Thickness(24, 14, 24, 14)
            };
            contentHost.SetBinding(ContentPresenter.ContentProperty, new Binding("DialogContent") { Source = this });
            Grid.SetRow(contentHost, 1);

            var adornerDecorator = new AdornerDecorator();
            adornerDecorator.Child = contentHost;
            Grid.SetRow(adornerDecorator, 1);
            mainGrid.Children.Add(adornerDecorator);

            outerBorder.Child = mainGrid;

            if (UseModalOverlay)
            {
                // Keep changing controls outside the shadow's effect input.
                // An effect on outerBorder invalidates/renders the entire card
                // for every progress tick, caret blink or edited grid cell.
                // mainGrid occupies the inside of the one-DIP layout border;
                // the negative margin restores the original shadow silhouette.
                var shadow = new Border
                {
                    Background = cardBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = outerBorder.BorderThickness,
                    CornerRadius = radius,
                    Margin = new Thickness(-1),
                    IsHitTestVisible = false,
                    Focusable = false,
                    Effect = new DropShadowEffect
                    {
                        BlurRadius = 16,
                        ShadowDepth = 0,
                        Opacity = 0.10
                    }
                };
                Grid.SetRowSpan(shadow, 2);
                mainGrid.Children.Insert(0, shadow);
                outerBorder.Background = Brushes.Transparent;
                outerBorder.BorderBrush = null;
                outerBorder.HorizontalAlignment = HorizontalAlignment.Center;
                outerBorder.VerticalAlignment = VerticalAlignment.Center;

                var fullGrid = new Grid { Background = Brushes.Transparent };
                fullGrid.Children.Add(outerBorder);
                Content = fullGrid;
                _refreshConstraints = work =>
                {
                    ConfigureOverlayHost(originalSizeToContent, originalWidth, originalHeight, originalMinWidth, originalMinHeight, originalMaxWidth, originalMaxHeight, outerBorder, work);
                    ApplyOverlayPosition(work);
                };
                _refreshConstraints(MonitorHelper.GetWorkAreaOrPrimary(this));
            }
            else
            {
                var work = MonitorHelper.GetWorkAreaOrPrimary(this);
                var maxWidth = Math.Min(originalMaxWidth, Math.Max(1, work.Width - 48));
                var maxHeight = Math.Min(originalMaxHeight, Math.Max(1, work.Height - 48));
                maxWidth = Math.Min(maxWidth, work.Width * AdaptiveMaxWidthFraction);
                maxHeight = Math.Min(maxHeight, work.Height * AdaptiveMaxHeightFraction);
                MinWidth = Math.Min(originalMinWidth, maxWidth);
                MinHeight = Math.Min(originalMinHeight, maxHeight);
                MaxWidth = maxWidth;
                MaxHeight = maxHeight;
                if (!double.IsNaN(Width)) Width = Math.Min(Width, maxWidth);
                if (!double.IsNaN(Height)) Height = Math.Min(Height, maxHeight);
                _refreshConstraints = available =>
                {
                    var boundedWidth = Math.Min(originalMaxWidth, Math.Min(Math.Max(1, available.Width - 48), available.Width * AdaptiveMaxWidthFraction));
                    var boundedHeight = Math.Min(originalMaxHeight, Math.Min(Math.Max(1, available.Height - 48), available.Height * AdaptiveMaxHeightFraction));
                    MinWidth = Math.Min(originalMinWidth, boundedWidth); MinHeight = Math.Min(originalMinHeight, boundedHeight);
                    MaxWidth = boundedWidth; MaxHeight = boundedHeight;
                };
                Background = cardBrush;
                Content = outerBorder;
                // WPF otherwise shows an HWND at its minimum size, then applies
                // SizeToContent after native initial placement. Measure the final
                // chrome under the monitor cap before showing; later content uses
                // the dialog's internal scrolling instead of moving the window.
                if (originalSizeToContent != SizeToContent.Manual)
                {
                    outerBorder.Measure(new Size(maxWidth, maxHeight));
                    SizeToContent = SizeToContent.Manual;
                    if (originalSizeToContent == SizeToContent.Width || originalSizeToContent == SizeToContent.WidthAndHeight)
                        Width = Math.Max(MinWidth, Math.Min(maxWidth, Math.Max(outerBorder.DesiredSize.Width, double.IsNaN(originalWidth) ? 0 : originalWidth)));
                    if (originalSizeToContent == SizeToContent.Height || originalSizeToContent == SizeToContent.WidthAndHeight)
                        Height = Math.Max(MinHeight, Math.Min(maxHeight, Math.Max(outerBorder.DesiredSize.Height, double.IsNaN(originalHeight) ? 0 : originalHeight)));
                }
            }
        }

        internal double AdaptiveMaxWidthFraction { get; set; } = 1;
        internal double AdaptiveMaxHeightFraction { get; set; } = 1;

        private void Header_MouseLeftButtonDown(Button captionButtonsContainer, object sender, MouseButtonEventArgs e)
        {
            // Non trascinare se il click è sui pulsanti caption (es. Chiudi)
            if (IsMouseOverCaptionButton(e.OriginalSource as DependencyObject, captionButtonsContainer))
            {
                return;
            }

            if (e.ClickCount == 2 && ResizeMode != ResizeMode.NoResize)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                e.Handled = true;
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private static bool IsMouseOverCaptionButton(DependencyObject source, DependencyObject buttonsContainer)
        {
            while (source != null)
            {
                if (source == buttonsContainer || source is Button)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ConfigureOverlayHost(
            SizeToContent originalSizeToContent,
            double originalWidth,
            double originalHeight,
            double originalMinWidth,
            double originalMinHeight,
            double originalMaxWidth,
            double originalMaxHeight,
            Border outerBorder,
            Rect cardWorkArea)
        {
            var cardMaxWidth = ClampToFiniteMax(Math.Max(1, cardWorkArea.Width - 48), originalMaxWidth);
            var cardMaxHeight = ClampToFiniteMax(Math.Max(1, cardWorkArea.Height - 48), originalMaxHeight);

            outerBorder.MaxWidth = cardMaxWidth;
            outerBorder.MaxHeight = cardMaxHeight;

            var boundedMinWidth = ClampToFiniteMax(originalMinWidth, cardMaxWidth);
            var boundedMinHeight = ClampToFiniteMax(originalMinHeight, cardMaxHeight);
            if (boundedMinWidth > 0)
                outerBorder.MinWidth = boundedMinWidth;
            if (boundedMinHeight > 0)
                outerBorder.MinHeight = boundedMinHeight;

            if (originalSizeToContent == SizeToContent.Manual)
            {
                outerBorder.Width = ClampToFiniteMax(GetPreferredLength(originalWidth, originalMinWidth, 760), cardMaxWidth);
                outerBorder.Height = ClampToFiniteMax(GetPreferredLength(originalHeight, originalMinHeight, 430), cardMaxHeight);
            }

            SizeToContent = SizeToContent.Manual;
            MinWidth = 0;
            MinHeight = 0;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }

        private void ApplyOverlayPosition(Rect bounds)
        {
            // Ownership controls modality/focus; the selected monitor work area
            // controls geometry. Never center the visible card on a small parent.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(1, bounds.Width);
            Height = Math.Max(1, bounds.Height);
        }

        private static double ClampToFiniteMax(double value, double max)
        {
            if (double.IsNaN(value) || value <= 0)
                return 0;

            if (double.IsNaN(max) || double.IsInfinity(max) || max <= 0)
                return value;

            return Math.Min(value, max);
        }

        private static double GetPreferredLength(double explicitLength, double minLength, double fallbackLength)
        {
            if (!double.IsNaN(explicitLength) && explicitLength > 0)
                return explicitLength;

            if (!double.IsNaN(minLength) && minLength > 0)
                return minLength;

            return fallbackLength;
        }
    }
}
