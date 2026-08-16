using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Win7POS.Core.Images;
using Win7POS.Core.Models;

namespace Win7POS.Wpf.Products.Images
{
    public partial class ProductImageListPresenter : UserControl
    {
        public static readonly DependencyProperty ProductProperty =
            DependencyProperty.Register(
                nameof(Product),
                typeof(ProductDetailsRow),
                typeof(ProductImageListPresenter),
                new PropertyMetadata(null, OnProductChanged));

        public static readonly DependencyProperty ThumbnailWidthProperty =
            DependencyProperty.Register(
                nameof(ThumbnailWidth),
                typeof(double),
                typeof(ProductImageListPresenter),
                new PropertyMetadata(52d));

        public static readonly DependencyProperty ThumbnailHeightProperty =
            DependencyProperty.Register(
                nameof(ThumbnailHeight),
                typeof(double),
                typeof(ProductImageListPresenter),
                new PropertyMetadata(52d));

        private readonly ProductImageDisplayViewModel _display =
            new ProductImageDisplayViewModel();
        private CancellationTokenSource _lifetime;

        public ProductImageListPresenter()
        {
            InitializeComponent();
            Presenter.DataContext = _display;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public ProductDetailsRow Product
        {
            get => (ProductDetailsRow)GetValue(ProductProperty);
            set => SetValue(ProductProperty, value);
        }

        public double ThumbnailWidth
        {
            get => (double)GetValue(ThumbnailWidthProperty);
            set => SetValue(ThumbnailWidthProperty, value);
        }

        public double ThumbnailHeight
        {
            get => (double)GetValue(ThumbnailHeightProperty);
            set => SetValue(ThumbnailHeightProperty, value);
        }

        private static void OnProductChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs eventArgs)
        {
            ((ProductImageListPresenter)dependencyObject).Restart();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Restart();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        private void Restart()
        {
            Cancel();
            if (Product == null)
            {
                _display.SetNoImage();
                return;
            }
            if (!IsLoaded) return;
            _lifetime = new CancellationTokenSource();
            _ = LoadAsync(
                Product,
                _lifetime.Token);
        }

        private async System.Threading.Tasks.Task LoadAsync(
            ProductDetailsRow product,
            CancellationToken cancellationToken)
        {
            try
            {
                await ProductImageRuntime.LoadAsync(
                    product,
                    ProductImageVariant.Thumb,
                    ProductImageDecodeProfile.ListThumbnail,
                    _display,
                    cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                _display.SetUnavailable();
            }
        }

        private void Cancel()
        {
            var lifetime = _lifetime;
            _lifetime = null;
            if (lifetime == null) return;
            try { lifetime.Cancel(); }
            finally { lifetime.Dispose(); }
        }
    }
}
