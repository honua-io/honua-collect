using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Enterprise;

public class AuthAndConfigTests
{
    private static AuthSession Session(DateTimeOffset expires, string? refresh = "rt") => new()
    {
        UserId = "u1",
        AccessToken = "at",
        RefreshToken = refresh,
        ExpiresAtUtc = expires,
        Scopes = new HashSet<string> { "collect.capture", "collect.submit" },
    };

    // --- E1 SSO session -------------------------------------------------------

    [Fact]
    public void Session_state_transitions_active_expiring_expired()
    {
        var expires = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var session = Session(expires);
        var skew = TimeSpan.FromMinutes(5);

        Assert.Equal(AuthSessionState.Active, session.StateAt(expires.AddMinutes(-30), skew));
        Assert.Equal(AuthSessionState.Expiring, session.StateAt(expires.AddMinutes(-2), skew));
        Assert.Equal(AuthSessionState.Expired, session.StateAt(expires, skew));
        Assert.True(session.IsExpired(expires.AddSeconds(1)));
    }

    [Fact]
    public void Scope_and_refresh_capability_are_reported()
    {
        var session = Session(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.True(session.HasScope("collect.capture"));
        Assert.False(session.HasScope("collect.admin"));
        Assert.True(session.CanRefresh);
        Assert.False(Session(default, refresh: null).CanRefresh);
    }

    // --- E4 managed app config ------------------------------------------------

    [Fact]
    public void Empty_config_is_unmanaged_with_defaults()
    {
        Assert.False(ManagedAppConfig.Empty.IsManaged);
        Assert.Null(ManagedAppConfig.Empty.ServerUrl);
        Assert.False(ManagedAppConfig.Empty.Policy.DisableExport);
    }

    [Fact]
    public void Config_exposes_server_branding_and_policy()
    {
        var config = new ManagedAppConfig(new Dictionary<string, string>
        {
            ["server.url"] = "https://collect.acme.gov",
            ["branding.appName"] = "Acme Field",
            ["branding.primaryColor"] = "#0A7E3C",
            ["policy.requireDeviceLock"] = "true",
            ["policy.disableExport"] = "yes",
            ["policy.maxDraftAgeDays"] = "14",
        });

        Assert.True(config.IsManaged);
        Assert.Equal("https://collect.acme.gov", config.ServerUrl);
        Assert.Equal("Acme Field", config.Branding.AppName);
        Assert.Equal("#0A7E3C", config.Branding.PrimaryColorHex);

        var policy = config.Policy;
        Assert.True(policy.RequireDeviceLock);
        Assert.True(policy.DisableExport);
        Assert.False(policy.DisableScreenshots);
        Assert.Equal(14, policy.MaxDraftAgeDays);
    }

    [Fact]
    public void Typed_getters_fall_back_and_parse()
    {
        var config = new ManagedAppConfig(new Dictionary<string, string> { ["a"] = "1", ["b"] = "nope" });

        Assert.Equal("default", config.GetString("missing", "default"));
        Assert.True(config.GetBool("a"));
        Assert.Null(config.GetInt("b"));
        Assert.Equal(1, config.GetInt("a"));
    }
}
