using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class PinPromptViewModel : INotifyPropertyChanged
    {
        private string _pin = string.Empty;
        private string _errorMessage = string.Empty;

        public PinPromptViewModel(string prompt)
        {
            Prompt = string.IsNullOrWhiteSpace(prompt) ? "Inserisci PIN (4 cifre)" : prompt;
        }

        public string Prompt { get; }

        public string Pin
        {
            get => _pin;
            set
            {
                _pin = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool IsValid
        {
            get
            {
                if (_pin.Length != 4) return false;
                for (var i = 0; i < _pin.Length; i++)
                {
                    if (!char.IsDigit(_pin[i])) return false;
                }
                return true;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
