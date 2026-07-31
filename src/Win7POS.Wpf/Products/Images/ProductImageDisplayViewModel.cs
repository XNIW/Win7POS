using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Win7POS.Core.Images;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Products.Images
{
    /// <summary>
    /// Presentation-only adapter. Decode, cache and preprocessing policy stay
    /// in their dedicated services.
    /// </summary>
    public sealed class ProductImageDisplayViewModel : INotifyPropertyChanged
    {
        private ProductImageDisplayState _state;
        private ImageSource _image;

        public ProductImageDisplayViewModel()
        {
            SetState(ProductImageDisplayState.FeatureDisabled, null);
        }

        public ProductImageDisplayState State => _state;
        public ImageSource Image => _image;
        public bool IsLoading => _state == ProductImageDisplayState.Loading;
        public bool IsLoaded =>
            _state == ProductImageDisplayState.Loaded &&
            _image != null;
        public bool ShowsStatus => !IsLoaded;
        public string StatusText => GetStatusText(_state);
        public string AccessibleName =>
            IsLoaded
                ? PosLocalization.T("productImage.preview")
                : StatusText;

        public void SetLoading()
        {
            SetState(ProductImageDisplayState.Loading, null);
        }

        public void SetNoImage()
        {
            SetState(ProductImageDisplayState.NoImage, null);
        }

        public void Apply(ProductImageDecodeResult result)
        {
            if (result == null)
            {
                SetState(ProductImageDisplayState.Error, null);
                return;
            }

            SetState(result.State, result.Image);
        }

        private void SetState(ProductImageDisplayState state, ImageSource image)
        {
            _state = state;
            _image = state == ProductImageDisplayState.Loaded ? image : null;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Image));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(ShowsStatus));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(AccessibleName));
        }

        private static string GetStatusText(ProductImageDisplayState state)
        {
            switch (state)
            {
                case ProductImageDisplayState.Loading:
                    return PosLocalization.T("productImage.loading");
                case ProductImageDisplayState.Corrupt:
                    return PosLocalization.T("productImage.invalid");
                case ProductImageDisplayState.Unavailable:
                case ProductImageDisplayState.Error:
                    return PosLocalization.T("productImage.unavailable");
                case ProductImageDisplayState.Loaded:
                    return PosLocalization.T("productImage.preview");
                default:
                    return PosLocalization.T("productImage.noImage");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }
}
