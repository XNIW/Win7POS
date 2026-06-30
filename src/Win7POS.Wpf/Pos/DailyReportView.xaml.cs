using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Pos
{
    public partial class DailyReportView : UserControl
    {
        private bool _initialLoadRequested;

        public DailyReportView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _initialLoadRequested = false;
            if (e.OldValue is DailyReportViewModel oldVm)
            {
                oldVm.ExportRequested -= OnExportRequested;
                oldVm.RequestExportScopeChoice -= OnRequestExportScopeChoice;
            }
            if (e.NewValue is DailyReportViewModel newVm)
            {
                newVm.ExportRequested += OnExportRequested;
                newVm.RequestExportScopeChoice += OnRequestExportScopeChoice;
                RequestInitialLoad(newVm);
            }
        }

        private void OnRequestExportScopeChoice()
        {
            var vm = DataContext as DailyReportViewModel;
            if (vm == null) return;
            var menu = new ContextMenu();
            var periodo = new MenuItem { Header = PosLocalization.T("reports.exportPeriod") };
            periodo.Click += (_, __) => vm.ChooseExportPeriod();
            var giorno = new MenuItem { Header = PosLocalization.T("reports.exportCurrentDay") };
            giorno.Click += (_, __) => vm.ChooseExportDay();
            menu.Items.Add(periodo);
            menu.Items.Add(giorno);
            if (vm.HasMarkedRows)
            {
                var selezione = new MenuItem { Header = PosLocalization.T("reports.exportMarkedDays") };
                selezione.Click += (_, __) => vm.ChooseExportMarked();
                menu.Items.Add(selezione);
            }
            menu.Closed += (_, __) => menu.PlacementTarget = null;
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            menu.IsOpen = true;
        }

        private async void OnExportRequested(ExportRequest request)
        {
            if (request == null) return;
            var dialog = new SaveFileDialog
            {
                Title = PosLocalization.T("reports.exportTitle"),
                Filter = PosLocalization.T("reports.exportFileFilter"),
                DefaultExt = ".csv",
                AddExtension = true,
                FileName = request.BaseFileName
            };
            if (dialog.ShowDialog() != true)
                return;
            var vm = DataContext as DailyReportViewModel;
            var ext = Path.GetExtension(dialog.FileName)?.ToLowerInvariant() ?? string.Empty;
            try
            {
                if (ext == ".xlsx")
                {
                    vm?.SetStatus(PosLocalization.T("reports.exportXlsxUnavailable"));
                    return;
                }
                var content = await vm.GetExportCsvContentAsync(request).ConfigureAwait(true);
                File.WriteAllText(dialog.FileName, content, Encoding.UTF8);
                vm?.SetStatus(PosLocalization.F("reports.fileSaved", dialog.FileName));
            }
            catch (System.Exception ex)
            {
                vm?.SetStatus(PosLocalization.F("reports.saveError", ex.Message));
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as DailyReportViewModel;
            RequestInitialLoad(vm);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as DailyReportViewModel;
            if (vm == null) return;
            vm.ExportRequested -= OnExportRequested;
            vm.RequestExportScopeChoice -= OnRequestExportScopeChoice;
            _initialLoadRequested = false;
        }

        private void RequestInitialLoad(DailyReportViewModel vm)
        {
            if (_initialLoadRequested || vm == null || !vm.LoadCommand.CanExecute(null))
            {
                return;
            }

            _initialLoadRequested = true;
            vm.LoadCommand.Execute(null);
        }

        private void HistoryGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            if (dep == null) return;

            if (FindVisualParent<CheckBox>(dep) != null) return;

            var row = FindVisualParent<DataGridRow>(dep);
            if (row?.Item is DailyReportViewModel.HistoryRow hr)
            {
                hr.IsMarked = !hr.IsMarked;
            }
        }

        private void HistoryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space) return;

            if (HistoryGrid?.SelectedItem is DailyReportViewModel.HistoryRow hr)
            {
                hr.IsMarked = !hr.IsMarked;
                e.Handled = true;
            }
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t) return t;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }
    }
}
