// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;

namespace Honua.Collect.Presentation.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
