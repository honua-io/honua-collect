// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Auth;

public delegate Task<AuthSession?> CredentialVerifier(string username, string password, CancellationToken cancellationToken);

public sealed class LoginViewModel : ObservableObject
{
    private readonly CredentialVerifier _verifier;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _isBusy;
    private string? _errorMessage;
    private bool _isAuthenticated;
    private AuthSession? _session;

    public LoginViewModel(CredentialVerifier verifier)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        LoginCommand = new RelayCommand(() => _ = LoginAsync(), () => !IsBusy);
    }

    public event EventHandler<AuthSession>? Authenticated;

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string? ErrorDetail { get; private set; }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    public AuthSession? Session
    {
        get => _session;
        private set => SetProperty(ref _session, value);
    }

    public ICommand LoginCommand { get; }

    public void NotifySessionExpired()
    {
        IsAuthenticated = false;
        Session = null;
        ErrorMessage = "Your session expired. Please sign in again.";
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Enter a username and password.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        (LoginCommand as RelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            var session = await _verifier(Username, Password, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                Session = session;
                IsAuthenticated = true;
                Authenticated?.Invoke(this, session);
            }
            else
            {
                ErrorMessage = "Invalid username or password.";
            }
        }
        catch (Exception ex)
        {
            // Don't surface raw server/transport detail to the UI; keep it in-memory for
            // diagnostics. The DEBUG log is deliberately limited to the exception TYPE —
            // never the message or stack — so a server-returned string can't reach any
            // log sink. (Credentials are never in scope here: the password is only ever
            // sent to the token endpoint and is not part of any exception this catches.)
            ErrorDetail = $"{ex.GetType().Name}: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[honua-login] sign-in failed: {ex.GetType().FullName}");
            ErrorMessage = "Sign-in failed. Check your connection and try again.";
        }
        finally
        {
            IsBusy = false;
            (LoginCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
