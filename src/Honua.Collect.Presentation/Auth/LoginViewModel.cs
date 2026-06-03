using System.Windows.Input;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Auth;

/// <summary>
/// Verifies a username/password against the server and returns the resulting
/// session. Injected so the login view-model is testable without a real
/// transport; the app implements this over its HTTP client.
/// </summary>
/// <param name="username">Entered username.</param>
/// <param name="password">Entered password.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>The authenticated session on success, or <see langword="null"/> when the credentials are rejected.</returns>
public delegate Task<AuthSession?> CredentialVerifier(string username, string password, CancellationToken cancellationToken);

/// <summary>
/// View-model for the sign-in screen. Binds the username/password fields and a
/// login command, drives a busy state during verification, surfaces validation
/// and error messages, and raises <see cref="Authenticated"/> with the resulting
/// <see cref="AuthSession"/> so the host can navigate on and store the token for
/// the sync transport.
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly CredentialVerifier _verifier;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _isBusy;
    private string? _errorMessage;
    private bool _isAuthenticated;
    private AuthSession? _session;

    /// <summary>Creates the login view-model over a credential verifier.</summary>
    /// <param name="verifier">Checks credentials against the server.</param>
    public LoginViewModel(CredentialVerifier verifier)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        LoginCommand = new RelayCommand(() => _ = LoginAsync(), () => !IsBusy);
    }

    /// <summary>Raised once a session is successfully established.</summary>
    public event EventHandler<AuthSession>? Authenticated;

    /// <summary>The entered username.</summary>
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>The entered password.</summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    /// <summary>Whether a sign-in attempt is in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Validation or error message to show, or <see langword="null"/> when clear.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Diagnostic detail of the last failure (for logging), not shown to the user.</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>Whether a session has been established.</summary>
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    /// <summary>The authenticated session once sign-in succeeds.</summary>
    public AuthSession? Session
    {
        get => _session;
        private set => SetProperty(ref _session, value);
    }

    /// <summary>Attempts to sign in with the current credentials.</summary>
    public ICommand LoginCommand { get; }

    /// <summary>
    /// Runs a sign-in attempt: validates the fields, calls the verifier, and on
    /// success sets <see cref="Session"/>/<see cref="IsAuthenticated"/> and raises
    /// <see cref="Authenticated"/>. Failures and exceptions surface via
    /// <see cref="ErrorMessage"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
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
            // Don't surface raw server/transport detail to the UI; keep it for diagnostics.
            ErrorDetail = ex.Message;
            ErrorMessage = "Sign-in failed. Check your connection and try again.";
        }
        finally
        {
            IsBusy = false;
            (LoginCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
