using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core;
using Win7POS.Core.ImportDb;
using Win7POS.Data;
using Win7POS.Data.ImportDb;

namespace Win7POS.Wpf.Import
{
    public sealed class ProductDbImportViewModel : INotifyPropertyChanged
    {
        private string _xlsxPath;
        private string _summary;
        private string _status;
        private bool _isBusy;
        private bool _dryRun = true;
        private string _lastWorkbookFingerprint = string.Empty;

        private ProductDbWorkbook _lastWorkbook;
        private ProductDbAnalysis _lastAnalysis;

        public string XlsxPath
        {
            get => _xlsxPath;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_xlsxPath ?? string.Empty, next, StringComparison.Ordinal))
                    return;
                _xlsxPath = next;
                ClearCachedWorkbook("Prima esegui Analizza.");
                OnPropertyChanged();
            }
        }

        public string Summary
        {
            get => _summary;
            set { _summary = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public bool DryRun
        {
            get => _dryRun;
            set { _dryRun = value; OnPropertyChanged(); OnPropertyChanged(nameof(ApplyModeText)); }
        }

        public string DbPath => AppPaths.DbPath;

        public string ApplyModeText => DryRun ? "Mode: DryRun (no DB write)" : "Mode: Apply (write to DB)";

        public ICommand BrowseCommand { get; }
        public ICommand AnalyzeCommand { get; }
        public ICommand ApplyCommand { get; }

        public ProductDbImportViewModel()
        {
            BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsBusy);
            AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, _ => !IsBusy);
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, _ => !IsBusy && HasCurrentWorkbook());
            Summary = "Seleziona un file .xlsx (Database prodotti) e premi Analizza.";
            Status = "";
        }

        private void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Seleziona file Excel Database prodotti",
                Filter = "Excel (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
            {
                XlsxPath = dlg.FileName;
                Status = "";
            }
        }

        private async Task AnalyzeAsync()
        {
            if (string.IsNullOrWhiteSpace(XlsxPath) || !File.Exists(XlsxPath))
            {
                Status = "Excel non valido: seleziona un file .xlsx esistente.";
                return;
            }

            IsBusy = true;
            Status = "Analisi in corso...";
            Summary = "";

            try
            {
                _lastWorkbook = ProductDbExcelReader.Read(XlsxPath);
                _lastAnalysis = ProductDbAnalysis.Analyze(_lastWorkbook);
                _lastWorkbookFingerprint = BuildCurrentWorkbookFingerprint();

                Summary = _lastAnalysis.ToSummaryString();
                Status = "OK. Products: " + _lastAnalysis.ProductsCount +
                    ", Suppliers: " + _lastAnalysis.SuppliersCount +
                    ", Categories: " + _lastAnalysis.CategoriesCount +
                    ", PriceHistory: " + _lastAnalysis.PriceHistoryCount;
            }
            catch (Exception ex)
            {
                Status = "Errore Analisi: " + ex.Message;
                Summary = ex.ToString();
                _lastWorkbook = null;
                _lastAnalysis = null;
            }
            finally
            {
                IsBusy = false;
            }

            await Task.CompletedTask.ConfigureAwait(true);
        }

        private async Task ApplyAsync()
        {
            if (_lastWorkbook == null)
            {
                Status = "Prima esegui Analizza.";
                return;
            }
            if (!HasCurrentWorkbook())
            {
                ClearCachedWorkbook("Prima esegui Analizza.");
                Status = "File modificato dopo l'analisi: riesegui Analizza prima di applicare.";
                return;
            }

            if (!DryRun)
            {
                if (!ApplyConfirmDialog.ShowConfirm(Application.Current?.MainWindow, "Conferma Apply Database", "Confermi Apply? Verranno scritti prodotti, supplier, categories e storico prezzi nel DB."))
                {
                    Status = "Apply annullato.";
                    return;
                }
            }

            IsBusy = true;
            Status = DryRun ? "DryRun: simulazione..." : "Apply in corso...";

            try
            {
                var opt = PosDbOptions.ForPath(AppPaths.DbPath);
                DbInitializer.EnsureCreated(opt);
                var factory = new SqliteConnectionFactory(opt);
                var importer = new ProductDbImporter(factory);
                var result = await importer.ImportAsync(_lastWorkbook, DryRun).ConfigureAwait(true);

                if (result.Errors.Count > 0)
                {
                    Summary = string.Join("\n", result.Errors);
                    Status = "Apply fallito.";
                }
                else
                {
                    Summary = "Products upserted: " + result.ProductsUpserted +
                        "\nPriceHistory inserted/skipped: " + result.PriceHistoryInserted + "/" + result.PriceHistorySkipped;
                    Status = DryRun ? "DryRun OK (nessuna scrittura DB)." : "Apply OK.";
                }
            }
            catch (Exception ex)
            {
                Status = "Errore Apply: " + ex.Message;
                Summary = ex.ToString();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RaiseCanExecuteChanged()
        {
            (BrowseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AnalyzeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void ClearCachedWorkbook(string status)
        {
            if (_lastWorkbook == null && _lastAnalysis == null) return;
            _lastWorkbook = null;
            _lastAnalysis = null;
            _lastWorkbookFingerprint = string.Empty;
            Summary = "Seleziona un file .xlsx (Database prodotti) e premi Analizza.";
            Status = status ?? string.Empty;
            RaiseCanExecuteChanged();
        }

        private bool HasCurrentWorkbook()
        {
            return _lastWorkbook != null &&
                !string.IsNullOrWhiteSpace(_lastWorkbookFingerprint) &&
                string.Equals(_lastWorkbookFingerprint, BuildCurrentWorkbookFingerprint(), StringComparison.Ordinal);
        }

        private string BuildCurrentWorkbookFingerprint()
        {
            var path = (XlsxPath ?? string.Empty).Trim();
            var fullPath = string.Empty;
            long length = -1;
            long lastWriteTicks = -1;
            try
            {
                if (path.Length > 0 && File.Exists(path))
                {
                    var info = new FileInfo(path);
                    fullPath = info.FullName;
                    length = info.Length;
                    lastWriteTicks = info.LastWriteTimeUtc.Ticks;
                }
                else
                {
                    fullPath = Path.GetFullPath(path);
                }
            }
            catch
            {
                fullPath = path;
            }

            return string.Join("|", new[]
            {
                fullPath,
                length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                lastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            private readonly Func<object, bool> _canExecute;

            public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
            public void Execute(object parameter) => _execute(parameter);
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> executeAsync, Func<object, bool> canExecute = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

            public async void Execute(object parameter)
            {
                try
                {
                    await _executeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "ProductDbImport AsyncRelayCommand failed");
                }
            }
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
