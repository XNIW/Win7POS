using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Util;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class HeldCartsViewModel : INotifyPropertyChanged
    {
        private readonly PosWorkflowService _service;
        private readonly Action<PosWorkflowSnapshot> _onRecovered;

        private bool _isBusy;
        private string _status = string.Empty;

        public ObservableCollection<HoldRow> Items { get; } = new ObservableCollection<HoldRow>();

        private HoldRow _selectedHold;
        public HoldRow SelectedHold
        {
            get => _selectedHold;
            set { _selectedHold = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public ICommand LoadCommand { get; }
        public ICommand RecoverCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand CloseCommand { get; }

        public HeldCartsViewModel(PosWorkflowService service, Action<PosWorkflowSnapshot> onRecovered)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _onRecovered = onRecovered ?? throw new ArgumentNullException(nameof(onRecovered));

            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            RecoverCommand = new AsyncRelayCommand(RecoverAsync, _ => !IsBusy && SelectedHold != null);
            DeleteCommand = new AsyncRelayCommand(DeleteAsync, _ => !IsBusy && SelectedHold != null);
            CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(false));
        }

        public async Task LoadAsync()
        {
            IsBusy = true;
            try
            {
                Items.Clear();
                var list = await _service.GetHeldCartsAsync().ConfigureAwait(true);
                foreach (var x in list)
                {
                    Items.Add(new HoldRow
                    {
                        HoldId = x.HoldId,
                        CreatedAtMs = x.CreatedAtMs,
                        TotalMinor = x.TotalMinor,
                        TimeText = x.TimeText,
                        TotalDisplay = MoneyClp.Format(x.TotalMinor)
                    });
                }
                Status = Items.Count == 0 ? "Nessuno scontrino sospeso." : "Trovati: " + Items.Count;
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RecoverAsync()
        {
            if (SelectedHold == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.RecoverHeldCartAsync(SelectedHold.HoldId).ConfigureAwait(true);
                _onRecovered(snapshot);
                RequestClose?.Invoke(true);
            }
            catch (Exception ex)
            {
                Status = "Errore recupero: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DeleteAsync()
        {
            if (SelectedHold == null) return;
            IsBusy = true;
            try
            {
                await _service.DeleteHeldCartAsync(SelectedHold.HoldId).ConfigureAwait(true);
                Items.Remove(SelectedHold);
                SelectedHold = null;
                Status = "Sospeso eliminato.";
            }
            catch (Exception ex)
            {
                Status = "Errore eliminazione: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public event Action<bool> RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class HoldRow
        {
            public string HoldId { get; set; } = string.Empty;
            public long CreatedAtMs { get; set; }
            public long TotalMinor { get; set; }
            public string TimeText { get; set; } = string.Empty;
            public string TotalDisplay { get; set; } = string.Empty;
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            public RelayCommand(Action<object> execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _execute(parameter);
#pragma warning disable CS0067
            public event EventHandler CanExecuteChanged;
#pragma warning restore CS0067
        }

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _execute;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
            public async void Execute(object parameter) => await _execute().ConfigureAwait(true);
#pragma warning disable CS0067
            public event EventHandler CanExecuteChanged;
#pragma warning restore CS0067
        }
    }
}
