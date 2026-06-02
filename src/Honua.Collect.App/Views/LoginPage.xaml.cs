using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;

namespace Honua.Collect.App.Views;

/// <summary>
/// Sign-in page. A thin host over the unit-tested <see cref="LoginViewModel"/>;
/// the injected <see cref="CredentialVerifier"/> validates the
/// entered credentials against the Honua server and produces an
/// <see cref="AuthSession"/> on success.
/// </summary>
public partial class LoginPage : ContentPage
{
    // The Android emulator reaches the host loopback at 10.0.2.2.
    private const string ServerBaseUrl = "http://10.0.2.2:18080";

    private readonly LoginViewModel _viewModel;

    public LoginPage()
    {
        InitializeComponent();
        _viewModel = new LoginViewModel(VerifyAsync);
        _viewModel.Authenticated += OnAuthenticated;
        BindingContext = _viewModel;
    }

    private void OnAuthenticated(object? sender, AuthSession session)
    {
        SignedInLabel.Text = $"Signed in as {session.DisplayName ?? session.UserId}. Sync is enabled.";
    }

    /// <summary>
    /// Validates credentials by issuing an authenticated request to the server
    /// with the supplied secret as the API key. A 2xx response yields a session;
    /// any other status is treated as invalid credentials.
    /// </summary>
    private static async Task<AuthSession?> VerifyAsync(string username, string password, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ServerBaseUrl) };
        http.DefaultRequestHeaders.Add("X-API-Key", password);

        using var response = await http.GetAsync(
            "/rest/services/mobile_offline_demo/FeatureServer/68910?f=json", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return new AuthSession
        {
            UserId = username,
            DisplayName = username,
            AccessToken = password,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(8),
            Scopes = new HashSet<string>(StringComparer.Ordinal) { "collect.sync" },
        };
    }
}
