using System.Windows.Input;

namespace Honua.Collect.Presentation.Mvvm;

/// <summary>An <see cref="ICommand"/> backed by delegates that take a typed parameter.</summary>
/// <typeparam name="T">The command parameter type.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    /// <summary>Creates a parameterized command.</summary>
    /// <param name="execute">Action to run with the command parameter.</param>
    /// <param name="canExecute">Optional guard.</param>
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Cast(parameter)) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        var value = Cast(parameter);
        if (CanExecute(parameter))
        {
            _execute(value);
        }
    }

    /// <summary>Raises <see cref="CanExecuteChanged"/> so bound controls re-query the guard.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Cast(object? parameter) => parameter is T typed ? typed : default;
}
