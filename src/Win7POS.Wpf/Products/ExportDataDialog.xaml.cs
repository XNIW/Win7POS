using System;
using System.Windows;
using Microsoft.Win32;
using Win7POS.Core;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public partial class ExportDataDialog : DialogShellWindow
    {
        public ExportDataChoice Result { get; private set; }

        public ExportDataDialog()
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 400, minHeight: 260, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: false);
            PathBox.Text = "";
        }

        private void Format_Changed(object sender, RoutedEventArgs e)
        {
            PathBox.Text = "";
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            if (RadioXlsx.IsChecked == true)
            {
                AppPaths.EnsureCreated();
                var dlg = new SaveFileDialog
                {
                    Title = "Salva export XLSX",
                    Filter = "Excel (*.xlsx)|*.xlsx|Tutti i file (*.*)|*.*",
                    DefaultExt = "xlsx",
                    InitialDirectory = AppPaths.ExportsDirectory,
                    FileName = "export_prodotti_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx"
                };
                if (dlg.ShowDialog() == true)
                {
                    PathBox.Text = dlg.FileName;
                }
            }
            else
            {
                AppPaths.EnsureCreated();
                var dlg = new SaveFileDialog
                {
                    Title = "Salva export CSV",
                    Filter = "CSV (*.csv)|*.csv|Tutti i file (*.*)|*.*",
                    DefaultExt = "csv",
                    InitialDirectory = AppPaths.ExportsDirectory,
                    FileName = "prodotti_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
                };
                if (dlg.ShowDialog() == true)
                {
                    PathBox.Text = dlg.FileName;
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
            Close();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var path = (PathBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(path))
            {
                Win7POS.Wpf.Import.ModernMessageDialog.Show(
                    this,
                    "Esporta dati",
                    "Seleziona un file di destinazione.");
                return;
            }

            Result = new ExportDataChoice(RadioXlsx.IsChecked == true ? ExportDataFormat.Xlsx : ExportDataFormat.Csv, path);
            DialogResult = true;
            Close();
        }

        public static ExportDataChoice ShowDialogAndGetChoice(Window owner)
        {
            var dlg = new ExportDataDialog { Owner = owner };
            Win7POS.Wpf.Infrastructure.WindowSizingHelper.CapMaxHeightToOwner(dlg);
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }
    }

    public enum ExportDataFormat { Xlsx, Csv }

    public sealed class ExportDataChoice
    {
        public ExportDataFormat Format { get; }
        public string TargetPath { get; }

        public ExportDataChoice(ExportDataFormat format, string targetPath)
        {
            Format = format;
            TargetPath = targetPath ?? "";
        }
    }
}
