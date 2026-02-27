using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Win7POS.Wpf.Import
{
    public partial class ImportView : UserControl
    {
        public ImportView()
        {
            InitializeComponent();
            DataContext = new ImportViewModel();
        }

        private void OnRootDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
                return;

            var first = files[0];
            if (!File.Exists(first))
                return;
            if (!string.Equals(Path.GetExtension(first), ".csv", StringComparison.OrdinalIgnoreCase))
                return;

            var vm = DataContext as ImportViewModel;
            if (vm != null)
                vm.CsvPath = first;
            e.Handled = true;
        }
    }
}
