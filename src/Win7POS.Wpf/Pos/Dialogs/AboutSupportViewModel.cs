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
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class AboutSupportViewModel : INotifyPropertyChanged
    {
        private readonly Pos.PosWorkflowService _service;
        private readonly CashierModeService _cashierMode = new CashierModeService();
        private string _printerName = "(default)";
        private string _autoPrintText = "true";
        private string _cashierPinStatusText = "Disattivo";
        private string _status = string.Empty;

        public AboutSupportViewModel(Pos.PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.DataDirectory));
            OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogsDirectory));
            CopyInfoCommand = new RelayCommand(_ => CopyInfo());
            SetOrChangePinCommand = new RelayCommand(_ => _ = SetOrChangePinAsync());
            RemovePinCommand = new RelayCommand(_ => _ = RemovePinAsync());
            _ = LoadPrinterSettingsAsync();
            _ = LoadCashierPinStatusAsync();
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

        public string CashierPinStatusText
        {
            get => _cashierPinStatusText;
            set { _cashierPinStatusText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public ICommand OpenDataFolderCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand CopyInfoCommand { get; }
        public ICommand SetOrChangePinCommand { get; }
        public ICommand RemovePinCommand { get; }

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
                sb.AppendLine("CashierPin: " + CashierPinStatusText);
                Clipboard.SetText(sb.ToString());
                Status = "Info copied to clipboard.";
            }
            catch (Exception ex)
            {
                Status = "Copy failed: " + ex.Message;
            }
        }

        private async Task LoadCashierPinStatusAsync()
        {
            try
            {
                var enabled = await _cashierMode.IsPinEnabledAsync().ConfigureAwait(true);
                var hasPin = await _cashierMode.HasPinAsync().ConfigureAwait(true);
                CashierPinStatusText = enabled && hasPin ? "Attivo" : "Disattivo";
            }
            catch (Exception ex)
            {
                Status = "Load PIN status failed: " + ex.Message;
            }
        }

        private async Task SetOrChangePinAsync()
        {
            try
            {
                var hasPin = await _cashierMode.HasPinAsync().ConfigureAwait(true);
                if (hasPin)
                {
                    var current = PromptPin("Inserisci PIN attuale");
                    if (current == null) return;
                    var ok = await _cashierMode.VerifyPinAsync(current).ConfigureAwait(true);
                    if (!ok)
                    {
                        Status = "PIN attuale non valido.";
                        return;
                    }
                }

                var pin1 = PromptPin("Nuovo PIN (4 cifre)");
                if (pin1 == null) return;
                var pin2 = PromptPin("Conferma nuovo PIN");
                if (pin2 == null) return;
                if (!string.Equals(pin1, pin2, StringComparison.Ordinal))
                {
                    Status = "PIN non coincidono.";
                    return;
                }

                await _cashierMode.SetPinAsync(pin1).ConfigureAwait(true);
                await _cashierMode.SetPinEnabledAsync(true).ConfigureAwait(true);
                await LoadCashierPinStatusAsync().ConfigureAwait(true);
                Status = hasPin ? "PIN aggiornato." : "PIN impostato.";
            }
            catch (Exception ex)
            {
                Status = "Set PIN failed: " + ex.Message;
            }
        }

        private async Task RemovePinAsync()
        {
            try
            {
                var hasPin = await _cashierMode.HasPinAsync().ConfigureAwait(true);
                if (!hasPin)
                {
                    Status = "PIN non impostato.";
                    return;
                }

                var current = PromptPin("Inserisci PIN attuale");
                if (current == null) return;
                var ok = await _cashierMode.VerifyPinAsync(current).ConfigureAwait(true);
                if (!ok)
                {
                    Status = "PIN attuale non valido.";
                    return;
                }

                await _cashierMode.ClearPinAsync().ConfigureAwait(true);
                await LoadCashierPinStatusAsync().ConfigureAwait(true);
                Status = "PIN rimosso.";
            }
            catch (Exception ex)
            {
                Status = "Remove PIN failed: " + ex.Message;
            }
        }

        private static string PromptPin(string prompt)
        {
            var dlg = new PinPromptDialog(prompt)
            {
                Owner = Application.Current?.MainWindow
            };
            var ok = dlg.ShowDialog() == true;
            if (!ok) return null;
            return dlg.Pin;
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
