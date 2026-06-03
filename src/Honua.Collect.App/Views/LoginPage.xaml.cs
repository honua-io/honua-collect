using Honua.Collect.App.Services;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Collect.App.Views;

/// <summary>
/// Sign-in page. A thin host: credential checking lives in the unit-tested
/// <see cref="ServerCredentialVerifier"/> (wired into the tested
/// <see cref="LoginViewModel"/>); on success the resulting <see cref="AuthSession"/>
/// is stored in the shared <see cref="IAuthSessionStore"/> so every subsequent
/// sync/upload request carries the signed-in user's token.
/// </summary>
public partial class LoginPage : ContentPage
{
    private readonly IAuthSessionStore _sessions = ServiceHelper.Get<IAuthSessionStore>();

    private readonly LoginViewModel _viewModel;

    public LoginPage()
    {
        InitializeComponent();

        var settings = ServiceHelper.Get<AppSettings>();
        var http = ServiceHelper.Get<IHttpClientFactory>().CreateClient(MauiProgram.ServerHttpClient);
        var verifier = new ServerCredentialVerifier(
            http,
            $"/rest/services/{settings.ServiceId}/FeatureServer/{settings.LayerId}?f=json");

        _viewModel = new LoginViewModel(verifier.VerifyAsync);
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
}
