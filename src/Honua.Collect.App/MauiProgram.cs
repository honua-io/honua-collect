using Honua.Collect.App.Services;
using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.Licensing;
using Honua.Collect.Core.Storage;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Collect.App;

public static class MauiProgram
{
	/// <summary>The named HTTP client configured with the server base address and auth handler.</summary>
	public const string ServerHttpClient = "honua";

	/// <summary>
	/// Named HTTP client for the server token endpoint (sign-in / token refresh). Points
	/// at the server but carries NO <see cref="AuthHeaderHandler"/>, so a token refresh
	/// can't recurse through the handler that triggers it.
	/// </summary>
	public const string TokenHttpClient = "honua-token";

	/// <summary>Named HTTP client for OpenStreetMap tile requests (no server auth).</summary>
	public const string TileHttpClient = "osm";

	/// <summary>Named HTTP client for the Anthropic API (no server auth).</summary>
	public const string AnthropicHttpClient = "anthropic";

	/// <summary>
	/// Per-request deadline for server/token calls. Replaces the 100s HttpClient
	/// default, which is far too long for interactive field use on flaky networks —
	/// a hung sync would otherwise block ~100s per attempt.
	/// </summary>
	private static readonly TimeSpan ServerRequestTimeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Per-request deadline for OSM tile fetches. Kept short so a slow tile releases
	/// the loader's concurrency slot quickly instead of holding it for the 100s
	/// default and stalling the whole map (head-of-line blocking).
	/// </summary>
	private static readonly TimeSpan TileRequestTimeout = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Per-request deadline for the Anthropic API. More generous than the server
	/// timeout because vision/extraction responses are slower, but still bounded so a
	/// hung request fails instead of inheriting the 100s default indefinitely.
	/// </summary>
	private static readonly TimeSpan AiRequestTimeout = TimeSpan.FromSeconds(90);

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Configuration loaded synchronously from the embedded asset — no endpoints or
		// keys in code, and no async-over-sync blocking on the startup thread.
		var settings = AppSettings.Load();
		builder.Services.AddSingleton(settings);

		// Auth: the session store holds the signed-in credential (falling back to the
		// dev key); the delegating handler validates/refreshes the session and attaches
		// it to every server request, so sign-in actually changes what the transport
		// sends and a near-expiry token is renewed just-in-time.
		builder.Services.AddSingleton<IAuthSessionStore>(_ => new AuthSessionStore(settings.DemoApiKey));

		// Session lifecycle: persist the signed-in session to the platform secure
		// store (Keystore/Keychain) so it resumes across restarts, honoring expiry on
		// load and surfacing a graceful re-sign-in when it lapses. A SessionRefresher is
		// wired over the token endpoint so a near-expiry session is renewed from its
		// refresh token before use (refused/failed refresh fails closed to re-sign-in).
		// The refresher runs over an UNAUTHENTICATED client (no AuthHeaderHandler) so a
		// refresh never recurses through this same handler.
		builder.Services.AddSingleton<ISessionPersistence, SecureStorageSessionPersistence>();
		builder.Services.AddSingleton(sp =>
		{
			var tokenHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenHttpClient);
			var refresher = new ServerTokenRefresher(tokenHttp);
			return new AuthSessionManager(
				sp.GetRequiredService<IAuthSessionStore>(),
				sp.GetRequiredService<ISessionPersistence>(),
				refresher.RefreshAsync);
		});

		// The transport handler validates/refreshes via the lifecycle just before
		// authenticating each request, then presents the (possibly renewed) bearer token.
		builder.Services.AddTransient(sp =>
		{
			var manager = sp.GetRequiredService<AuthSessionManager>();
			return new AuthHeaderHandler(
				sp.GetRequiredService<IAuthSessionStore>(),
				manager.EnsureValidAsync);
		});

		// Audit trail (BACKLOG E3): security- and data-relevant events are appended to
		// a durable, tamper-evident SQLite trail in the same encrypted at-rest posture
		// as the record store. Secrets are scrubbed before persisting; the trail can be
		// queried/exported off-device for SIEM/audit.
		var auditDbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-audit.db");
		builder.Services.AddSingleton<IAuditStore>(_ =>
			new SqliteAuditStore(auditDbPath, DbKeyProvider.GetOrCreateKeyAsync().GetAwaiter().GetResult()));
		builder.Services.AddSingleton(sp => new AuditTrail(sp.GetRequiredService<IAuditStore>()));
		builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<AuditTrail>());

		// On-device authorization (BACKLOG E2): the IdP's role claims on the session map
		// to capabilities via a data-driven capability map; the Presentation layer queries
		// IDeviceAuthorization.Can(action, resource) and authorization fails closed when no
		// session (or no granting role) is present. Denials are recorded to the audit trail.
		builder.Services.AddSingleton(_ => CapabilityMap.Build()
			.Role("field-worker",
				CollectPermission.CaptureRecords,
				CollectPermission.EditRecords,
				CollectPermission.SubmitRecords,
				CollectPermission.GenerateReports)
			.Role("supervisor",
				CollectPermission.CaptureRecords,
				CollectPermission.EditRecords,
				CollectPermission.SubmitRecords,
				CollectPermission.ReviewRecords,
				CollectPermission.DeleteRecords,
				CollectPermission.ExportData,
				CollectPermission.GenerateReports,
				CollectPermission.ManageAssignments)
			.Role("admin",
				CollectPermission.CaptureRecords,
				CollectPermission.EditRecords,
				CollectPermission.SubmitRecords,
				CollectPermission.ReviewRecords,
				CollectPermission.DeleteRecords,
				CollectPermission.ExportData,
				CollectPermission.GenerateReports,
				CollectPermission.ManageAssignments,
				CollectPermission.ConfigureForms)
			.Create());
		builder.Services.AddSingleton<IDeviceAuthorization>(sp => new DeviceAuthorization(
			sp.GetRequiredService<IAuthSessionStore>(),
			sp.GetRequiredService<CapabilityMap>(),
			sp.GetRequiredService<IAuditSink>()));

		// Licensing & entitlement enforcement: the LicenseService verifies a signed
		// license key offline against the embedded authority public key and is the
		// trusted source of the running edition. IEntitlements resolves to the current
		// entitlements (Community baseline until a valid key is activated), so gated
		// features (reports/export, AI capture) enforce the real license rather than a
		// hardcoded edition. The activated key is held in secure storage.
		builder.Services.AddSingleton<SecureStorageLicenseStore>();
		builder.Services.AddSingleton(_ => new LicenseService());
		builder.Services.AddTransient<IEntitlements>(sp => sp.GetRequiredService<LicenseService>().Entitlements);

		// Server client: auth handler + optional SPKI certificate pinning. Pinning is
		// opt-in (configured pins only); with none set, platform TLS validation applies
		// so self-hosted deployments aren't broken by a pin they didn't set.
		var pinningCallback = CertificatePinning.CreateValidationCallback(settings.PinnedCertificateSpki);
		builder.Services.AddHttpClient(ServerHttpClient, client =>
			{
				client.BaseAddress = settings.BaseUri;
				client.Timeout = ServerRequestTimeout;
			})
			.AddHttpMessageHandler<AuthHeaderHandler>()
			.ConfigurePrimaryHttpMessageHandler(() =>
			{
				var handler = new HttpClientHandler();
				if (pinningCallback is not null)
				{
					handler.ServerCertificateCustomValidationCallback =
						(request, certificate, chain, errors) => pinningCallback(request, certificate, chain, errors);
				}

				return handler;
			});

		// Token endpoint client: same server base address + cert pinning, but no auth
		// handler — sign-in and token refresh present their own credential and must not
		// recurse through the AuthHeaderHandler.
		builder.Services.AddHttpClient(TokenHttpClient, client =>
			{
				client.BaseAddress = settings.BaseUri;
				client.Timeout = ServerRequestTimeout;
			})
			.ConfigurePrimaryHttpMessageHandler(() =>
			{
				var handler = new HttpClientHandler();
				if (pinningCallback is not null)
				{
					handler.ServerCertificateCustomValidationCallback =
						(request, certificate, chain, errors) => pinningCallback(request, certificate, chain, errors);
				}

				return handler;
			});

		// Unauthenticated clients for third-party endpoints (still pooled by the factory).
		builder.Services.AddHttpClient(TileHttpClient, client =>
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("HonuaCollect/1.0 (+https://honua.io)");
			client.Timeout = TileRequestTimeout;
		});
		builder.Services.AddHttpClient(AnthropicHttpClient, client => client.Timeout = AiRequestTimeout);

		// The feature-sync transport over the auth-aware server client.
		builder.Services.AddTransient(sp =>
			new GeoServicesFeatureSync(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ServerHttpClient)));

		// The shared, thread-safe record book over a SQLCipher-encrypted store. The
		// book builds the store lazily so the encryption key is fetched from secure
		// storage off the UI thread (no startup blocking).
		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-records.db");
		builder.Services.AddSingleton(_ => new RecordBook(() => OpenEncryptedStoreAsync(dbPath)));

		builder.Logging.AddDebug();

		var app = builder.Build();
		ServiceHelper.Initialize(app.Services);

		// Activate any stored license key so entitlements are established before the
		// first gated screen. Off the UI thread; an absent/invalid key leaves the app
		// on the Community baseline.
		_ = ActivateStoredLicenseAsync(app.Services);
		return app;
	}

	/// <summary>Loads the persisted license key (if any) and applies it to the license service.</summary>
	private static async Task ActivateStoredLicenseAsync(IServiceProvider services)
	{
		try
		{
			var token = await services.GetRequiredService<SecureStorageLicenseStore>().LoadAsync();
			if (!string.IsNullOrWhiteSpace(token))
			{
				services.GetRequiredService<LicenseService>().Apply(token);
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[honua-license] activation skipped: {ex.GetType().Name}");
		}
	}

	/// <summary>
	/// SQLite primary result code for "file is not a database" (SQLITE_NOTADB). This is
	/// what SQLCipher reports when the on-disk bytes are a legacy *unencrypted* cache or
	/// cannot be decrypted with the current key — i.e. the only case where recreating
	/// the store is the right self-heal.
	/// </summary>
	private const int SqliteNotADatabase = 26;

	/// <summary>
	/// Opens the SQLCipher-encrypted record store, self-healing past a legacy
	/// unencrypted or undecryptable local cache by quarantining it and starting fresh.
	/// </summary>
	/// <remarks>
	/// The self-heal is deliberately narrow. The local DB holds field captures that may
	/// not yet have synced to the server, so it must NOT be wiped on a transient or
	/// environmental fault — a locked/busy DB, an I/O error, low disk, or a rotated key
	/// would all otherwise permanently destroy un-uploaded data (those records do not
	/// "re-sync from the server"). We therefore only recreate on SQLITE_NOTADB (the
	/// legacy-unencrypted / undecryptable case, whose bytes are unreadable with our key
	/// regardless); every other <see cref="Microsoft.Data.Sqlite.SqliteException"/> is
	/// surfaced to the caller instead of silently dropping the store. Even in the
	/// recreate case the old file is moved aside, not deleted, so it can be recovered.
	/// </remarks>
	private static async Task<IRecordStore> OpenEncryptedStoreAsync(string dbPath)
	{
		var key = await DbKeyProvider.GetOrCreateKeyAsync();
		var store = new SqliteRecordStore(dbPath, key);
		try
		{
			await store.LoadAllAsync(); // probe: opens + applies the key
			return store;
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADatabase)
		{
			// Legacy-unencrypted or undecryptable-with-current-key cache: unreadable
			// with our key in any case, so quarantine it and start fresh.
			QuarantineUnreadableStore(dbPath);
			return new SqliteRecordStore(dbPath, key);
		}
	}

	/// <summary>
	/// Moves an unreadable record DB aside (rather than deleting it) so a fresh store can
	/// be created while the old bytes remain available for manual/forensic recovery. If
	/// the file can't be renamed, it is deleted as a last resort — it is already
	/// undecryptable with our key, so nothing the app can read is lost.
	/// </summary>
	private static void QuarantineUnreadableStore(string dbPath)
	{
		if (!File.Exists(dbPath))
		{
			return;
		}

		try
		{
			var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
			File.Move(dbPath, $"{dbPath}.unreadable-{stamp}.bak");
		}
		catch (IOException)
		{
			File.Delete(dbPath);
		}
		catch (UnauthorizedAccessException)
		{
			File.Delete(dbPath);
		}
	}
}
