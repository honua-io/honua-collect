using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Enterprise;

public class AuthSessionSerializerTests
{
    private static AuthSession Sample() => new()
    {
        UserId = "u1",
        DisplayName = "User One",
        AccessToken = "access-123",
        RefreshToken = "refresh-456",
        ExpiresAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero),
        Scopes = new HashSet<string> { "collect.sync", "collect.capture" },
    };

    [Fact]
    public void Round_trips_all_fields()
    {
        var original = Sample();

        var restored = AuthSessionSerializer.TryDeserialize(AuthSessionSerializer.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.UserId, restored!.UserId);
        Assert.Equal(original.DisplayName, restored.DisplayName);
        Assert.Equal(original.AccessToken, restored.AccessToken);
        Assert.Equal(original.RefreshToken, restored.RefreshToken);
        Assert.Equal(original.ExpiresAtUtc, restored.ExpiresAtUtc);
        Assert.True(restored.Scopes.SetEquals(original.Scopes));
    }

    [Fact]
    public void Round_trips_a_minimal_session_without_optional_fields()
    {
        var minimal = new AuthSession
        {
            UserId = "u2",
            AccessToken = "at",
            ExpiresAtUtc = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero),
        };

        var restored = AuthSessionSerializer.TryDeserialize(AuthSessionSerializer.Serialize(minimal));

        Assert.NotNull(restored);
        Assert.Null(restored!.DisplayName);
        Assert.Null(restored.RefreshToken);
        Assert.False(restored.CanRefresh);
        Assert.Empty(restored.Scopes);
    }

    [Fact]
    public void Serialize_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => AuthSessionSerializer.Serialize(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    public void TryDeserialize_returns_null_for_blank_or_garbage(string? json)
    {
        Assert.Null(AuthSessionSerializer.TryDeserialize(json));
    }

    [Theory]
    [InlineData("{\"name\":\"x\"}")] // no uid / at
    [InlineData("{\"uid\":\"u1\"}")] // missing access token
    [InlineData("{\"at\":\"t\"}")] // missing user id
    public void TryDeserialize_returns_null_when_identifying_fields_missing(string json)
    {
        Assert.Null(AuthSessionSerializer.TryDeserialize(json));
    }
}
