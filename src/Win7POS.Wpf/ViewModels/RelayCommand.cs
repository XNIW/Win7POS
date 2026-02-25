using System;
using System.Windows.Input;

namespace Win7POS.Wpf.ViewModels
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _run;
        private readonly Func<bool> _canRun;

        public RelayCommand(Action run, Func<bool> canRun = null)
        {
            _run = run;
            _canRun = canRun;
        }

        public bool CanExecute(object parameter) => _canRun?.Invoke() ?? true;
        public void Execute(object parameter) => _run();

        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
