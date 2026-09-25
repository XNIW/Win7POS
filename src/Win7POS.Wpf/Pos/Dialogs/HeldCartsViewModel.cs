using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Util;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class HeldCartsViewModel : INotifyPropertyChanged
    {
        private readonly IHeldCartWorkflow _service;
        private readonly Action<PosWorkflowSnapshot> _onRecovered;
        private long _generation;
        private bool _closed;
        public bool IsMutating { get; private set; }

        private bool _isBusy;
        private string _status = string.Empty;

        public ObservableCollection<HoldRow> Items { get; } = new ObservableCollection<HoldRow>();

        private HoldRow _selectedHold;
        public HoldRow SelectedHold
        {
            get => _selectedHold;
            set
            {
                if (_closed || ReferenceEquals(_selectedHold, value)) return;
                _selectedHold = value;
                _generation++;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
                _ = LoadPreviewAsync();
            }
        }

        public ObservableCollection<HoldLineRow> SelectedLines { get; } = new ObservableCollection<HoldLineRow>();

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
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

        public HeldCartsViewModel(IHeldCartWorkflow service, Action<PosWorkflowSnapshot> onRecovered)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _onRecovered = onRecovered ?? throw new ArgumentNullException(nameof(onRecovered));

            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            RecoverCommand = new AsyncRelayCommand(RecoverAsync, _ => !IsBusy && SelectedHold != null);
            DeleteCommand = new AsyncRelayCommand(DeleteAsync, _ => !IsBusy && SelectedHold != null);
            CloseCommand = new RelayCommand(_ => { if (TryClose()) RequestClose?.Invoke(false); });
        }

        public bool TryClose()
        {
            if (IsMutating) return false;
            _closed = true;
            _generation++;
            return true;
        }

        public async Task LoadAsync()
        {
            if (_closed || IsBusy) return;
            IsBusy = true;
            SelectedHold = null;
            var generation = ++_generation;
            try
            {
                Items.Clear();
                var list = await _service.GetHeldCartsAsync().ConfigureAwait(true);
                if (_closed || generation != _generation) return;
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
                Status = Items.Count == 0
                    ? PosLocalization.T("heldCarts.none")
                    : PosLocalization.F("heldCarts.found", Items.Count);
            }
            catch (Exception ex)
            {
                if (!_closed && generation == _generation) Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                if (!_closed) IsBusy = false;
            }
        }

        private async Task LoadPreviewAsync()
        {
            var selected = _selectedHold;
            var generation = _generation;
            SelectedLines.Clear();
            if (_closed || selected == null)
            {
                SelectedLines.Clear();
                return;
            }
            try
            {
                var list = await _service.PeekHeldCartLinesAsync(selected.HoldId).ConfigureAwait(true);
                if (_closed || generation != _generation || !ReferenceEquals(selected, _selectedHold)) return;
                SelectedLines.Clear();
                foreach (var x in list)
                {
                    var lineTotal = checked(x.UnitPrice * x.Qty);
                    SelectedLines.Add(new HoldLineRow
                    {
                        Barcode = x.Barcode,
                        Name = x.Name,
                        UnitPriceDisplay = MoneyClp.Format(x.UnitPrice),
                        Qty = x.Qty,
                        LineTotalDisplay = MoneyClp.Format(lineTotal)
                    });
                }
            }
            catch (Exception ex)
            {
                if (_closed || generation != _generation) return;
                SelectedLines.Clear();
                Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
        }

        private async Task RecoverAsync()
        {
            if (_closed || IsBusy || SelectedHold == null) return;
            var selected = SelectedHold;
            IsBusy = true;
            IsMutating = true;
            _generation++;
            try
            {
                var snapshot = await _service.RecoverHeldCartAsync(selected.HoldId).ConfigureAwait(true);
                if (_closed) return;
                _onRecovered(snapshot);
                IsMutating = false;
                TryClose();
                RequestClose?.Invoke(true);
            }
            catch (Exception ex)
            {
                if (!_closed) Status = PosLocalization.F("heldCarts.recoverError", ex.Message);
            }
            finally
            {
                IsMutating = false;
                if (!_closed) IsBusy = false;
            }
        }

        private async Task DeleteAsync()
        {
            if (_closed || IsBusy || SelectedHold == null) return;
            var selected = SelectedHold;
            IsBusy = true;
            IsMutating = true;
            _generation++;
            try
            {
                await _service.DeleteHeldCartAsync(selected.HoldId).ConfigureAwait(true);
                if (_closed) return;
                Items.Remove(selected);
                if (ReferenceEquals(SelectedHold, selected)) SelectedHold = null;
                Status = PosLocalization.T("heldCarts.deleted");
            }
            catch (Exception ex)
            {
                if (!_closed) Status = PosLocalization.F("heldCarts.deleteError", ex.Message);
            }
            finally
            {
                IsMutating = false;
                if (!_closed) IsBusy = false;
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

        public sealed class HoldLineRow
        {
            public string Barcode { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string UnitPriceDisplay { get; set; } = string.Empty;
            public int Qty { get; set; }
            public string LineTotalDisplay { get; set; } = string.Empty;
            public string DetailLine => $"{Qty} × {UnitPriceDisplay} = {LineTotalDisplay}";
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            public RelayCommand(Action<object> execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _execute(parameter);
            public event EventHandler CanExecuteChanged
            {
                add { CommandManager.RequerySuggested += value; }
                remove { CommandManager.RequerySuggested -= value; }
            }
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
            public async void Execute(object parameter)
            {
                if (!CanExecute(parameter)) return;
                try
                {
                    await _execute().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "HeldCarts AsyncRelayCommand failed");
                }
            }
            public event EventHandler CanExecuteChanged
            {
                add { CommandManager.RequerySuggested += value; }
                remove { CommandManager.RequerySuggested -= value; }
            }
        }
    }
}
