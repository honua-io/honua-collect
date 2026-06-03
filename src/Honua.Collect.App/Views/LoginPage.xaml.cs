using Honua.Collect.App.Services;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Collect.App.Views;

/// <summary>
/// Sign-in page. A thin host over the unit-tested <see cref="LoginViewModel"/>;
/// the injected <see cref="CredentialVerifier"/> validates the entered credentials
/// against the Honua server and, on success, stores the resulting
/// <see cref="AuthSession"/> in the shared <see cref="IAuthSessionStore"/> so every
/// subsequent sync/upload request carries the signed-in user's token.
/// </summary>
public partial class LoginPage : ContentPage
{
    private readonly IAuthSessionStore _sessions = ServiceHelper.Get<IAuthSessionStore>();
    private readonly AppSettings _settings = ServiceHelper.Get<AppSettings>();
    private readonly IHttpClientFactory _httpFactory = ServiceHelper.Get<IHttpClientFactory>();

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
        // This is the seam that makes login functional: the transport handler now
        // sends this token on every request.
        _sessions.Set(session);
        SignedInLabel.Text = $"Signed in as {session.DisplayName ?? session.UserId}. Sync uses your account.";
    }

    /// <summary>
    /// Validates credentials by issuing an authenticated request to the server with
    /// the supplied secret. A 2xx response yields a session; any other status is
    /// treated as invalid credentials.
    /// </summary>
    private async Task<AuthSession?> VerifyAsync(string username, string password, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(MauiProgram.ServerHttpClient);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{_settings.ServiceId}/FeatureServer/{_settings.LayerId}?f=json");

        // Present the entered credential explicitly (the auth handler leaves an
        // explicit header untouched), so we test the user's own credential.
        request.Headers.Add(AuthHeaderHandler.HeaderName, password);

        using var response = await http.SendAsync(request, ct);
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
