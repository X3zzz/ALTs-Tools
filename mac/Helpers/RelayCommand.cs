using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;

namespace AltsTools.Helpers
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged;

        // Avalonia's Button only re-queries CanExecute when this fires, and it
        // must fire on the UI thread or the button won't refresh.
        public void NotifyCanExecuteChanged()
        {
            if (Dispatcher.UIThread.CheckAccess())
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            else
                Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
            => !_isExecuting && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
                return;

            try
            {
                _isExecuting = true;
                NotifyCanExecuteChanged();
                await _execute();
            }
            finally
            {
                _isExecuting = false;
                NotifyCanExecuteChanged();
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void NotifyCanExecuteChanged()
        {
            if (Dispatcher.UIThread.CheckAccess())
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            else
                Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }
}
