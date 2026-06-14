using Honua.Collect.Core.Enterprise;
using Honua.Collect.Presentation.Auth;

namespace Honua.Collect.Presentation.Tests;

public class LoginViewModelTests
{
    private static AuthSession SampleSession() => new()
    {
        UserId = "user-1",
        DisplayName = "Sample User",
        AccessToken = "token-abc",
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
    };

    [Fact]
    public void Constructor_NullVerifier_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LoginViewModel(null!));
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("user", "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public async Task LoginAsync_BlankCredentials_SetsErrorAndDoesNotCallVerifier(string username, string password)
    {
        var called = false;
        var vm = new LoginViewModel((_, _, _) =>
        {
            called = true;
            return Task.FromResult<AuthSession?>(SampleSession());
        })
        {
            Username = username,
            Password = password,
        };

        await vm.LoginAsync();

        Assert.False(called);
        Assert.Equal("Enter a username and password.", vm.ErrorMessage);
        Assert.False(vm.IsAuthenticated);
        Assert.Null(vm.Session);
    }

    [Fact]
    public async Task LoginAsync_SuccessfulVerifier_AuthenticatesAndRaisesEvent()
    {
        var expected = SampleSession();
        AuthSession? raised = null;
        var vm = new LoginViewModel((_, _, _) => Task.FromResult<AuthSession?>(expected))
        {
            Username = "user",
            Password = "secret",
        };
        vm.Authenticated += (_, session) => raised = session;

        await vm.LoginAsync();

        Assert.True(vm.IsAuthenticated);
        Assert.Same(expected, vm.Session);
        Assert.Same(expected, raised);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task NotifySessionExpired_AfterSignIn_ClearsStateAndPrompts()
    {
        var vm = new LoginViewModel((_, _, _) => Task.FromResult<AuthSession?>(SampleSession()))
        {
            Username = "user",
            Password = "secret",
        };
        await vm.LoginAsync();
        Assert.True(vm.IsAuthenticated);

        vm.NotifySessionExpired();

        Assert.False(vm.IsAuthenticated);
        Assert.Null(vm.Session);
        Assert.Equal("Your session expired. Please sign in again.", vm.ErrorMessage);
    }

    [Fact]
    public async Task LoginAsync_NullVerifier_SetsInvalidCredentialsError()
    {
        var vm = new LoginViewModel((_, _, _) => Task.FromResult<AuthSession?>(null))
        {
            Username = "user",
            Password = "wrong",
        };

        await vm.LoginAsync();

        Assert.Equal("Invalid username or password.", vm.ErrorMessage);
        Assert.False(vm.IsAuthenticated);
        Assert.Null(vm.Session);
    }

    [Fact]
    public async Task LoginAsync_VerifierThrows_ShowsGenericMessage_KeepsDetailForDiagnostics()
    {
        var vm = new LoginViewModel((_, _, _) => throw new InvalidOperationException("server unreachable"))
        {
            Username = "user",
            Password = "secret",
        };

        await vm.LoginAsync();

        // Raw transport detail is NOT surfaced to the user, only kept for diagnostics
        // (type-qualified so the log distinguishes a network fault from a parse/server error).
        Assert.Equal("Sign-in failed. Check your connection and try again.", vm.ErrorMessage);
        Assert.Equal("InvalidOperationException: server unreachable", vm.ErrorDetail);
        Assert.False(vm.IsAuthenticated);
        Assert.Null(vm.Session);
    }

    [Fact]
    public async Task LoginAsync_CapturesBusyState_TrueWhileVerifying()
    {
        bool busyDuring = false;
        LoginViewModel vm = null!;
        vm = new LoginViewModel((_, _, _) =>
        {
            busyDuring = vm.IsBusy;
            return Task.FromResult<AuthSession?>(SampleSession());
        })
        {
            Username = "user",
            Password = "secret",
        };

        await vm.LoginAsync();

        Assert.True(busyDuring);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoginCommand_CanExecute_FalseWhileBusy()
    {
        var tcs = new TaskCompletionSource<AuthSession?>();
        var vm = new LoginViewModel((_, _, _) => tcs.Task)
        {
            Username = "user",
            Password = "secret",
        };

        Assert.True(vm.LoginCommand.CanExecute(null));

        var pending = vm.LoginAsync();
        Assert.False(vm.LoginCommand.CanExecute(null));

        tcs.SetResult(SampleSession());
        await pending;
        Assert.True(vm.LoginCommand.CanExecute(null));
    }
}
