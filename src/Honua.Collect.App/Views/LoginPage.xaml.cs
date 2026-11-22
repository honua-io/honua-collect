// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.App.Services;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Collect.App.Views;

public partial class LoginPage : ContentPage
{
    private readonly AuthSessionManager _manager;

    private readonly LoginViewModel _viewModel;

    public LoginPage(AuthSessionManager manager, IHttpClientFactory httpClientFactory)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        InitializeComponent();

        // Exchange credentials for a short-lived bearer token at the server's token
        // endpoint; the AuthHeaderHandler then presents that token (not the password).
        var http = httpClientFactory.CreateClient(MauiProgram.TokenHttpClient);
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
