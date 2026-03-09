using System;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public partial class ExportDataDialog : Window
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
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Salva export XLSX",
                    Filter = "Excel (*.xlsx)|*.xlsx|Tutti i file (*.*)|*.*",
                    DefaultExt = "xlsx",
                    FileName = "export_prodotti_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx"
                };
                if (dlg.ShowDialog() == true)
                {
                    PathBox.Text = dlg.FileName;
                }
            }
            else
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Seleziona la cartella per i file CSV (Products.csv, Suppliers.csv, ...)"
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    PathBox.Text = dlg.SelectedPath;
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
                System.Windows.MessageBox.Show(
                    RadioXlsx.IsChecked == true ? "Seleziona un file di destinazione." : "Seleziona una cartella di destinazione.",
                    "Esporta dati",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (RadioXlsx.IsChecked == true)
            {
                Result = new ExportDataChoice(ExportDataFormat.Xlsx, path, null);
            }
            else
            {
                Result = new ExportDataChoice(ExportDataFormat.Csv, null, path);
            }

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
        public string TargetFolder { get; }

        public ExportDataChoice(ExportDataFormat format, string targetPath, string targetFolder)
        {
            Format = format;
            TargetPath = targetPath ?? "";
            TargetFolder = targetFolder ?? "";
        }
    }
}
