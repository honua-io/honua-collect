using Honua.Collect.App.Services;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Collect.App.Views;

/// <summary>
/// Sign-in page. A thin host: credential checking lives in the unit-tested
/// <see cref="ServerCredentialVerifier"/> (wired into the tested
/// <see cref="LoginViewModel"/>); on success the resulting <see cref="AuthSession"/>
/// is handed to the <see cref="AuthSessionManager"/>, which makes it live in the
/// shared <see cref="IAuthSessionStore"/> (so every sync/upload request carries the
/// token) and persists it to secure storage so it resumes across restarts. The
/// manager's <see cref="AuthSessionManager.SessionExpired"/> event drives a graceful
/// re-sign-in prompt instead of a silent failure.
/// </summary>
public partial class LoginPage : ContentPage
{
    private readonly AuthSessionManager _manager = ServiceHelper.Get<AuthSessionManager>();

    private readonly LoginViewModel _viewModel;

    public LoginPage()
    {
        InitializeComponent();

        // Exchange credentials for a short-lived bearer token at the server's token
        // endpoint; the AuthHeaderHandler then presents that token (not the password).
        var http = ServiceHelper.Get<IHttpClientFactory>().CreateClient(MauiProgram.ServerHttpClient);
        var verifier = new ServerCredentialVerifier(http);

        _viewModel = new LoginViewModel(verifier.VerifyAsync);
        _viewModel.Authenticated += OnAuthenticated;
        _manager.SessionExpired += OnSessionExpired;
        BindingContext = _viewModel;

        // Resume a persisted, still-valid session so a returning user isn't forced to
        // re-enter credentials. Expired sessions are dropped by the manager.
        _ = ResumeAsync();
    }

    private async Task ResumeAsync()
    {
        var resumed = await _manager.RestoreAsync().ConfigureAwait(true);
        if (resumed is not null)
        {
            SignedInLabel.Text = $"Signed in as {resumed.DisplayName ?? resumed.UserId}. Sync uses your account.";
        }
    }

    private async void OnAuthenticated(object? sender, AuthSession session)
    {
        // This is the seam that makes login functional: the transport handler now
        // sends this token on every request, and the session is persisted for resume.
        await _manager.SignInAsync(session).ConfigureAwait(true);
        SignedInLabel.Text = $"Signed in as {session.DisplayName ?? session.UserId}. Sync uses your account.";
    }

    private void OnSessionExpired(object? sender, EventArgs e)
    {
        // Marshal back to the UI thread — the lifecycle event can fire from a
        // background request — and prompt a graceful re-sign-in.
        Dispatcher.Dispatch(() =>
        {
            _viewModel.NotifySessionExpired();
            SignedInLabel.Text = string.Empty;
        });
    }
}
