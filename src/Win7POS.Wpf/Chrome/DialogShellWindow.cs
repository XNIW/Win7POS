using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using Win7POS.Wpf.Infrastructure;

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

            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            SourceInitialized -= OnSourceInitialized;
            BuildChrome();

            if (!UseModalOverlay)
                MonitorHelper.AddWorkAreaClampHook(this);
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
                Background = cardBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = radius,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.10
                }
            };

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
                    ToolTip = "Chiudi"
                };
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
                outerBorder.HorizontalAlignment = HorizontalAlignment.Center;
                outerBorder.VerticalAlignment = VerticalAlignment.Center;

                var fullGrid = new Grid { Background = Brushes.Transparent };
                fullGrid.Children.Add(outerBorder);
                Content = fullGrid;
                ConfigureOverlayHost(originalSizeToContent, originalWidth, originalHeight, originalMinWidth, originalMinHeight, originalMaxWidth, originalMaxHeight, outerBorder);
                ApplyOverlayPosition();
            }
            else
            {
                outerBorder.Effect = null;
                Background = cardBrush;
                Content = outerBorder;
            }
        }

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
            Border outerBorder)
        {
            var cardWorkArea = Owner != null
                ? MonitorHelper.GetWorkAreaForExactWindowOrPrimary(Owner)
                : MonitorHelper.GetWorkAreaOrPrimary(this);

            var cardMaxWidth = ClampToFiniteMax(Math.Max(200, cardWorkArea.Width - 48), originalMaxWidth);
            var cardMaxHeight = ClampToFiniteMax(Math.Max(200, cardWorkArea.Height - 48), originalMaxHeight);

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

        private void ApplyOverlayPosition()
        {
            Rect bounds;

            if (Owner == null)
            {
#if DEBUG
                Debug.WriteLine("[DialogShellWindow] Overlay without owner, using work area fallback.");
#endif
                bounds = MonitorHelper.GetWorkAreaOrPrimary(this);
            }
            else if (Owner.ActualWidth <= 0 || Owner.ActualHeight <= 0)
            {
#if DEBUG
                Debug.WriteLine("[DialogShellWindow] Overlay owner has invalid size, using exact-owner monitor work area.");
#endif
                bounds = MonitorHelper.GetWorkAreaForExactWindowOrPrimary(Owner);
            }
            else
            {
                bounds = new Rect(Owner.Left, Owner.Top, Owner.ActualWidth, Owner.ActualHeight);
            }

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
