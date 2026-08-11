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
            if (!IsLoaded || Product == null) return;
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
