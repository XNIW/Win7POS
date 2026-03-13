using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

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

        public DialogShellWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = System.Windows.Media.Brushes.Transparent;
            AllowsTransparency = false;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 42,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(8),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

            var outerBorder = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FCFBFD")),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D8D0E4")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var headerBorder = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6F4B8B")),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5E3D79")),
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
                Foreground = System.Windows.Media.Brushes.White,
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

            var contentHost = new ContentPresenter
            {
                Margin = new Thickness(18)
            };
            contentHost.SetBinding(ContentPresenter.ContentProperty, new Binding("DialogContent") { Source = this });
            Grid.SetRow(contentHost, 1);

            var adornerDecorator = new AdornerDecorator();
            adornerDecorator.Child = contentHost;
            Grid.SetRow(adornerDecorator, 1);
            mainGrid.Children.Add(adornerDecorator);

            outerBorder.Child = mainGrid;
            Content = outerBorder;
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
    }
}
