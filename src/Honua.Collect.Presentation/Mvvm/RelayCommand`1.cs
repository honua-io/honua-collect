// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;

namespace Honua.Collect.Presentation.Mvvm;

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Cast(parameter)) ?? true;

    public void Execute(object? parameter)
    {
        var value = Cast(parameter);
        if (CanExecute(parameter))
        {
            _execute(value);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Cast(object? parameter) => parameter is T typed ? typed : default;
}
