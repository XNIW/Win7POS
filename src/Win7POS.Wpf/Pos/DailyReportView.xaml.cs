using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Pos
{
    public partial class DailyReportView : UserControl
    {
        public DailyReportView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is DailyReportViewModel oldVm)
            {
                oldVm.ExportRequested -= OnExportRequested;
                oldVm.RequestExportScopeChoice -= OnRequestExportScopeChoice;
            }
            if (e.NewValue is DailyReportViewModel newVm)
            {
                newVm.ExportRequested += OnExportRequested;
                newVm.RequestExportScopeChoice += OnRequestExportScopeChoice;
                if (newVm.LoadCommand.CanExecute(null))
                    newVm.LoadCommand.Execute(null);
            }
        }

        private void OnRequestExportScopeChoice()
        {
            var vm = DataContext as DailyReportViewModel;
            if (vm == null) return;
            var menu = new ContextMenu();
            var periodo = new MenuItem { Header = "Esporta periodo" };
            periodo.Click += (_, __) => vm.ChooseExportPeriod();
            var giorno = new MenuItem { Header = "Esporta giorno corrente" };
            giorno.Click += (_, __) => vm.ChooseExportDay();
            menu.Items.Add(periodo);
            menu.Items.Add(giorno);
            if (vm.HasMarkedRows)
            {
                var selezione = new MenuItem { Header = "Esporta giorni selezionati" };
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
                Title = "Esporta report",
                Filter = "File Excel (*.xlsx)|*.xlsx|File CSV (*.csv)|*.csv",
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
                    vm?.SetStatus("Export Excel (xlsx) in fase di implementazione. Usare CSV.");
                    return;
                }
                var content = await vm.GetExportCsvContentAsync(request).ConfigureAwait(true);
                File.WriteAllText(dialog.FileName, content, Encoding.UTF8);
                vm?.SetStatus("File salvato: " + dialog.FileName);
            }
            catch (System.Exception ex)
            {
                vm?.SetStatus("Errore salvataggio: " + ex.Message);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as DailyReportViewModel;
            if (vm != null && vm.LoadCommand.CanExecute(null))
                vm.LoadCommand.Execute(null);
        }
    }
}
