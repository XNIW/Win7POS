using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win7POS.Core;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class AboutSupportViewModel : INotifyPropertyChanged
    {
        private readonly Pos.PosWorkflowService _service;
        private string _printerName = "(default)";
        private string _autoPrintText = "true";
        private string _status = string.Empty;

        public AboutSupportViewModel(Pos.PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.DataDirectory));
            OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogsDirectory));
            CopyInfoCommand = new RelayCommand(_ => CopyInfo());
            _ = LoadPrinterSettingsAsync();
        }

        public string VersionText => (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version?.ToString() ?? "unknown";
        public string BuildText => ReadBuildInfoOrFallback();
        public string DbPath => AppPaths.DbPath;
        public string LogsDirectory => AppPaths.LogsDirectory;
        public string BackupsDirectory => AppPaths.BackupsDirectory;
        public string ExportsDirectory => AppPaths.ExportsDirectory;

        public string PrinterName
        {
            get => _printerName;
            set { _printerName = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string AutoPrintText
        {
            get => _autoPrintText;
            set { _autoPrintText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value ?? string.Empty; OnPropertyChanged(); }
        }

        public ICommand OpenDataFolderCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand CopyInfoCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        private async Task LoadPrinterSettingsAsync()
        {
            try
            {
                var settings = await _service.GetPrinterSettingsAsync().ConfigureAwait(true);
                PrinterName = string.IsNullOrWhiteSpace(settings.PrinterName) ? "(default)" : settings.PrinterName;
                AutoPrintText = settings.AutoPrint ? "true" : "false";
            }
            catch (Exception ex)
            {
                Status = "Load printer settings failed: " + ex.Message;
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                AppPaths.EnsureCreated();
                Process.Start("explorer.exe", path);
            }
            catch (Exception ex)
            {
                Status = "Open folder failed: " + ex.Message;
            }
        }

        private void CopyInfo()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Win7POS About/Support");
                sb.AppendLine("Version: " + VersionText);
                sb.AppendLine("Build: " + BuildText);
                sb.AppendLine("DB: " + DbPath);
                sb.AppendLine("Logs: " + LogsDirectory);
                sb.AppendLine("Backups: " + BackupsDirectory);
                sb.AppendLine("Exports: " + ExportsDirectory);
                sb.AppendLine("Printer: " + PrinterName);
                sb.AppendLine("AutoPrint: " + AutoPrintText);
                Clipboard.SetText(sb.ToString());
                Status = "Info copied to clipboard.";
            }
            catch (Exception ex)
            {
                Status = "Copy failed: " + ex.Message;
            }
        }

        private string ReadBuildInfoOrFallback()
        {
            try
            {
                var versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VERSION.txt");
                if (!File.Exists(versionFile))
                    return VersionText;

                var raw = File.ReadAllText(versionFile, Encoding.UTF8).Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    return VersionText;

                return raw.Replace("\r\n", " | ").Replace("\n", " | ");
            }
            catch
            {
                return VersionText;
            }
        }

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
        }
    }
}
