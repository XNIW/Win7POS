using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Import
{
    public partial class SupplierExcelImportDialog : DialogShellWindow
    {
        private readonly SupplierExcelImportViewModel _viewModel;

        public SupplierExcelImportDialog(Func<bool> authorizeApply)
        {
            InitializeComponent();
            _viewModel = new SupplierExcelImportViewModel(
                service: new SupplierExcelImportWorkflowService(authorizeApply),
                fileDialogService: new SupplierExcelFileDialogService(() => this));
            _viewModel.RequestClose += OnRequestClose;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            DataContext = _viewModel;
        }

        public static bool ShowDialog(Window owner, Func<bool> authorizeApply)
        {
            var dlg = new SupplierExcelImportDialog(authorizeApply)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(owner)
            };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);
            return dlg.ShowDialog() == true;
        }

        private void OnRequestClose(bool success)
        {
            DialogResult = success;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.RequestClose -= OnRequestClose;
            base.OnClosed(e);
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(SupplierExcelImportViewModel.StepIndex), StringComparison.Ordinal) &&
                !string.Equals(e.PropertyName, nameof(SupplierExcelImportViewModel.IsBusy), StringComparison.Ordinal))
            {
                return;
            }

            if (_viewModel.IsBusy)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusCurrentStep));
        }

        private void FocusCurrentStep()
        {
            switch (_viewModel.StepIndex)
            {
                case 0:
                    if (AnalyzeButton.IsEnabled)
                    {
                        AnalyzeButton.Focus();
                    }
                    else
                    {
                        BrowseButton.Focus();
                    }
                    break;
                case 1:
                    ColumnMappingGrid.Focus();
                    break;
                case 2:
                    EditableRowsGrid.Focus();
                    break;
                case 3:
                    ReviewTabs.Focus();
                    break;
            }
        }
    }
}
