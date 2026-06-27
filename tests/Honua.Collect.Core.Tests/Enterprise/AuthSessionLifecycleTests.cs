using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Enterprise;

public class AuthSessionLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static AuthSession Session(TimeSpan validFor, string? refresh = null, string token = "at") => new()
    {
        UserId = "u1",
        DisplayName = "User One",
        AccessToken = token,
        RefreshToken = refresh,
        ExpiresAtUtc = Now + validFor,
        Scopes = new HashSet<string> { "collect.sync" },
    };

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class FakePersistence : ISessionPersistence
    {
        public AuthSession? Stored { get; set; }

        public int Saves { get; private set; }

        public int Clears { get; private set; }

        public int Loads { get; private set; }

        public Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult(Stored);
        }

        public Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default)
        {
            Saves++;
            Stored = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Clears++;
            Stored = null;
            return Task.CompletedTask;
        }
    }

    private static (AuthSessionManager manager, AuthSessionStore store, FakePersistence persistence, TestClock clock) Build(
        SessionRefresher? refresher = null)
    {
        var store = new AuthSessionStore();
        var persistence = new FakePersistence();
        var clock = new TestClock();
        var manager = new AuthSessionManager(store, persistence, refresher, clock, TimeSpan.FromMinutes(5));
        return (manager, store, persistence, clock);
    }

    // --- construction ---------------------------------------------------------

    [Fact]
    public void Constructor_null_dependencies_throw()
    {
        var store = new AuthSessionStore();
        var persistence = new FakePersistence();

        Assert.Throws<ArgumentNullException>(() => new AuthSessionManager(null!, persistence));
        Assert.Throws<ArgumentNullException>(() => new AuthSessionManager(store, null!));
    }

    // --- restore (session resume) --------------------------------------------

    [Fact]
    public async Task RestoreAsync_no_saved_session_signs_out()
    {
        var (manager, store, persistence, _) = Build();

        var resumed = await manager.RestoreAsync();

        Assert.Null(resumed);
        Assert.Null(store.Current);
        Assert.Equal(0, persistence.Clears);
    }

    [Fact]
    public async Task RestoreAsync_valid_saved_session_resumes_it()
    {
        var (manager, store, persistence, _) = Build();
        persistence.Stored = Session(TimeSpan.FromHours(1));

        var resumed = await manager.RestoreAsync();

        Assert.NotNull(resumed);
        Assert.Equal("u1", store.Current!.UserId);
        Assert.Equal(0, persistence.Clears);
    }

    [Fact]
    public async Task RestoreAsync_expired_saved_session_is_discarded_and_cleared()
    {
        var (manager, store, persistence, _) = Build();
        persistence.Stored = Session(TimeSpan.FromMinutes(-1)); // already expired

        var resumed = await manager.RestoreAsync();

        Assert.Null(resumed);
        Assert.Null(store.Current);
        Assert.Equal(1, persistence.Clears); // stale token dropped, not presented
    }

    // --- sign in / out --------------------------------------------------------

    [Fact]
    public async Task SignInAsync_makes_live_and_persists()
    {
        var (manager, store, persistence, _) = Build();
        var session = Session(TimeSpan.FromHours(1));

        await manager.SignInAsync(session);

        Assert.Same(session, store.Current);
        Assert.Same(session, persistence.Stored);
        Assert.Equal(1, persistence.Saves);
    }

    [Fact]
    public async Task SignInAsync_null_throws()
    {
        var (manager, _, _, _) = Build();
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.SignInAsync(null!));
    }

    [Fact]
    public async Task SignOutAsync_clears_live_and_persisted()
    {
        var (manager, store, persistence, _) = Build();
        await manager.SignInAsync(Session(TimeSpan.FromHours(1)));

        await manager.SignOutAsync();

        Assert.Null(store.Current);
        Assert.Null(persistence.Stored);
        Assert.Equal(1, persistence.Clears);
    }

    // --- ensure valid (proactive refresh + expiry) ---------------------------

    [Fact]
    public async Task EnsureValidAsync_no_session_returns_null()
    {
        var (manager, _, _, _) = Build();
        Assert.Null(await manager.EnsureValidAsync());
    }

    [Fact]
    public async Task EnsureValidAsync_active_session_is_returned_without_refresh()
    {
        var refreshCalls = 0;
        var (manager, store, _, _) = Build((s, _) =>
        {
            refreshCalls++;
            return Task.FromResult<AuthSession?>(s);
        });
        store.Set(Session(TimeSpan.FromHours(1)));

        var result = await manager.EnsureValidAsync();

        Assert.NotNull(result);
        Assert.Equal(0, refreshCalls);
    }

    [Fact]
    public async Task EnsureValidAsync_expiring_without_refresher_keeps_current()
    {
        var (manager, store, _, _) = Build();
        var session = Session(TimeSpan.FromMinutes(2)); // inside the 5-min skew
        store.Set(session);

        var result = await manager.EnsureValidAsync();

        Assert.Same(session, result);
    }

    [Fact]
    public async Task EnsureValidAsync_expiring_with_no_refresh_token_skips_refresh()
    {
        var refreshCalls = 0;
        var (manager, store, _, _) = Build((s, _) =>
        {
            refreshCalls++;
            return Task.FromResult<AuthSession?>(s);
        });
        store.Set(Session(TimeSpan.FromMinutes(2), refresh: null)); // CanRefresh == false

        var result = await manager.EnsureValidAsync();

        Assert.NotNull(result);
        Assert.Equal(0, refreshCalls);
    }

    [Fact]
    public async Task EnsureValidAsync_expiring_refresh_succeeds_swaps_session()
    {
        var refreshed = Session(TimeSpan.FromHours(1), refresh: "rt2", token: "fresh");
        var (manager, store, persistence, _) = Build((_, _) => Task.FromResult<AuthSession?>(refreshed));
        store.Set(Session(TimeSpan.FromMinutes(2), refresh: "rt1"));

        var result = await manager.EnsureValidAsync();

        Assert.Same(refreshed, result);
        Assert.Same(refreshed, store.Current);
        Assert.Same(refreshed, persistence.Stored); // refreshed session is persisted
    }

    [Fact]
    public async Task EnsureValidAsync_single_flights_concurrent_refreshes()
    {
        // AUD-255: a burst of near-expiry requests must trigger exactly one refresh,
        // not a stampede that rotates refresh tokens into mutual 401s. The waiters
        // reuse the freshly stored session instead of refreshing again.
        var refreshed = Session(TimeSpan.FromHours(1), refresh: "rt2", token: "fresh");
        var calls = 0;
        var gate = new TaskCompletionSource();
        SessionRefresher refresher = async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task.ConfigureAwait(false); // hold the first refresh so the rest queue
            return refreshed;
        };
        var (manager, store, _, _) = Build(refresher);
        store.Set(Session(TimeSpan.FromMinutes(2), refresh: "rt1"));

        var tasks = Enumerable.Range(0, 5).Select(_ => manager.EnsureValidAsync()).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Same(refreshed, r));
        Assert.Same(refreshed, store.Current);
    }

    [Fact]
    public async Task EnsureValidAsync_expiring_refresh_refused_keeps_current()
    {
        var current = Session(TimeSpan.FromMinutes(2), refresh: "rt1");
        var (manager, store, _, _) = Build((_, _) => Task.FromResult<AuthSession?>(null));
        store.Set(current);

        var result = await manager.EnsureValidAsync();

        Assert.Same(current, result); // still valid until it actually expires
    }

    [Fact]
    public async Task EnsureValidAsync_expired_no_refresher_signs_out_and_raises_event()
    {
        var (manager, store, persistence, _) = Build();
        store.Set(Session(TimeSpan.FromMinutes(-1), refresh: "rt1")); // expired
        var raised = 0;
        manager.SessionExpired += (_, _) => raised++;

        var result = await manager.EnsureValidAsync();

        Assert.Null(result);
        Assert.Null(store.Current);
        Assert.Equal(1, persistence.Clears);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EnsureValidAsync_expired_refresh_succeeds_swaps_session()
    {
        var refreshed = Session(TimeSpan.FromHours(1), refresh: "rt2", token: "fresh");
        var raised = 0;
        var (manager, store, _, _) = Build((_, _) => Task.FromResult<AuthSession?>(refreshed));
        manager.SessionExpired += (_, _) => raised++;
        store.Set(Session(TimeSpan.FromMinutes(-1), refresh: "rt1"));

        var result = await manager.EnsureValidAsync();

        Assert.Same(refreshed, result);
        Assert.Equal(0, raised); // recovered, no expiry prompt
    }

    [Fact]
    public async Task EnsureValidAsync_expired_refresh_returns_already_expired_is_rejected()
    {
        var stillExpired = Session(TimeSpan.FromMinutes(-1), refresh: "rt2");
        var raised = 0;
        var (manager, store, _, _) = Build((_, _) => Task.FromResult<AuthSession?>(stillExpired));
        manager.SessionExpired += (_, _) => raised++;
        store.Set(Session(TimeSpan.FromMinutes(-1), refresh: "rt1"));

        var result = await manager.EnsureValidAsync();

        Assert.Null(result);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EnsureValidAsync_expired_refresh_throws_is_caught_and_expires()
    {
        var raised = 0;
        var (manager, store, _, _) = Build((_, _) => throw new HttpRequestException("network down"));
        manager.SessionExpired += (_, _) => raised++;
        store.Set(Session(TimeSpan.FromMinutes(-1), refresh: "rt1"));

        var result = await manager.EnsureValidAsync();

        Assert.Null(result);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EnsureValidAsync_refresh_cancellation_propagates()
    {
        var (manager, store, _, _) = Build((_, _) => throw new OperationCanceledException());
        store.Set(Session(TimeSpan.FromMinutes(-1), refresh: "rt1"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.EnsureValidAsync());
    }
}
