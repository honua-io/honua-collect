using Honua.Collect.App.Services;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.Storage;
using Honua.Collect.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Collect.App;

public static class MauiProgram
{
	/// <summary>The named HTTP client configured with the server base address and auth handler.</summary>
	public const string ServerHttpClient = "honua";

	/// <summary>Named HTTP client for OpenStreetMap tile requests (no server auth).</summary>
	public const string TileHttpClient = "osm";

	/// <summary>Named HTTP client for the Anthropic API (no server auth).</summary>
	public const string AnthropicHttpClient = "anthropic";

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
		// dev key); the delegating handler attaches it to every server request, so
		// sign-in actually changes what the transport sends.
		builder.Services.AddSingleton<IAuthSessionStore>(_ => new AuthSessionStore(settings.DemoApiKey));
		builder.Services.AddTransient<AuthHeaderHandler>();

		// Session lifecycle: persist the signed-in session to the platform secure
		// store (Keystore/Keychain) so it resumes across restarts, honoring expiry on
		// load and surfacing a graceful re-sign-in when it lapses. No refresher is
		// wired yet — the server's generateToken issues no refresh token — so the
		// manager simply keeps a near-expiry token until it expires (the refresh seam
		// is there for when the server contract supports it).
		builder.Services.AddSingleton<ISessionPersistence, SecureStorageSessionPersistence>();
		builder.Services.AddSingleton(sp => new AuthSessionManager(
			sp.GetRequiredService<IAuthSessionStore>(),
			sp.GetRequiredService<ISessionPersistence>()));

		// Server client: auth handler + optional SPKI certificate pinning. Pinning is
		// opt-in (configured pins only); with none set, platform TLS validation applies
		// so self-hosted deployments aren't broken by a pin they didn't set.
		var pinningCallback = CertificatePinning.CreateValidationCallback(settings.PinnedCertificateSpki);
		builder.Services.AddHttpClient(ServerHttpClient, client => client.BaseAddress = settings.BaseUri)
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

		// Unauthenticated clients for third-party endpoints (still pooled by the factory).
		builder.Services.AddHttpClient(TileHttpClient,
			client => client.DefaultRequestHeaders.UserAgent.ParseAdd("HonuaCollect/1.0 (+https://honua.io)"));
		builder.Services.AddHttpClient(AnthropicHttpClient);

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
		return app;
	}

	/// <summary>
	/// Opens the SQLCipher-encrypted record store, self-healing past a legacy
	/// unencrypted or corrupt local cache (which can't be opened with the key) by
	/// recreating it — the records re-sync from the server.
	/// </summary>
	private static async Task<IRecordStore> OpenEncryptedStoreAsync(string dbPath)
	{
		var key = await DbKeyProvider.GetOrCreateKeyAsync();
		var store = new SqliteRecordStore(dbPath, key);
		try
		{
			await store.LoadAllAsync(); // probe: opens + applies the key
			return store;
		}
		catch (Microsoft.Data.Sqlite.SqliteException)
		{
			if (File.Exists(dbPath))
			{
				File.Delete(dbPath);
			}

			return new SqliteRecordStore(dbPath, key);
		}
	}
}
