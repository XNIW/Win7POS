using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DailyReportDialog : Window
    {
        public DailyReportDialog(DailyReportViewModel vm)
        {
            InitializeComponent();
            // Dimensioni: XAML Width=1180 Height=760 MinWidth=1000 MinHeight=660 (nessun Max per ridimensionamento). Tab Giornaliero/Storico, KPI compatti.
            DataContext = vm;
            Loaded += OnLoaded;
            if (vm != null)
            {
                vm.ExportRequested += OnExportRequested;
                vm.RequestExportScopeChoice += OnRequestExportScopeChoice;
            }
        }

        private void OnRequestExportScopeChoice()
        {
            var vm = DataContext as DailyReportViewModel;
            if (vm == null) return;
            var menu = new ContextMenu();
            var periodo = new MenuItem { Header = "Esporta periodo" };
            periodo.Click += (_, __) =>
            {
                vm.ChooseExportPeriod();
            };
            var giorno = new MenuItem { Header = "Esporta giorno selezionato" };
            giorno.Click += (_, __) =>
            {
                vm.ChooseExportDay();
            };
            menu.Items.Add(periodo);
            menu.Items.Add(giorno);
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
