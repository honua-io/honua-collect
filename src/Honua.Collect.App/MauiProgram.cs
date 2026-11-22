// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.App.Services;
using Honua.Collect.App.Views;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.Field.Related;
using Honua.Collect.Core.History;
using Honua.Collect.Core.Storage;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Collect.App;

public static class MauiProgram
{
	public const string ServerHttpClient = "honua";

	public const string TokenHttpClient = "honua-token";

	public const string UploadHttpClient = "honua-upload";

	public const string TileHttpClient = "osm";

	private static readonly TimeSpan ServerRequestTimeout = TimeSpan.FromSeconds(30);

	private static readonly TimeSpan TileRequestTimeout = TimeSpan.FromSeconds(20);

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
				refresher.RefreshAsync,
				logger: sp.GetService<ILogger<AuthSessionManager>>());
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

		// Audit trail: security- and data-relevant events are appended to
		// a durable, tamper-evident SQLite trail in the same encrypted at-rest posture
		// as the record store. Secrets are scrubbed before persisting; the trail can be
		// queried/exported off-device for SIEM/audit.
		var auditDbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-audit.db");
		builder.Services.AddSingleton<IAuditStore>(_ => new LazyAuditStore(async _ =>
			new SqliteAuditStore(auditDbPath, await DbKeyProvider.GetOrCreateKeyAsync().ConfigureAwait(false))));
		builder.Services.AddSingleton(sp => new AuditTrail(sp.GetRequiredService<IAuditStore>()));
		builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<AuditTrail>());

		// On-device authorization: the IdP's role claims on the session map
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

		// Upload client: identical to the server client (base address + auth +
		// optional cert pinning) but WITHOUT a short total timeout. Attachment bodies
		// can be tens to hundreds of MB; HttpClient.Timeout is a total per-request
		// deadline that covers the streamed body, so the 30s ServerRequestTimeout
		// would abort any large upload on a slow field link regardless of progress.
		// GeoServicesFeatureSync.AddAttachmentAsync applies its own size-derived
		// per-request deadline instead, so this client is left effectively unbounded.
		builder.Services.AddHttpClient(UploadHttpClient, client =>
			{
				client.BaseAddress = settings.BaseUri;
				client.Timeout = Timeout.InfiniteTimeSpan;
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

		builder.Services.AddHttpClient(TileHttpClient, client =>
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("HonuaCollect/1.0 (+https://honua.io)");
			client.Timeout = TileRequestTimeout;
		});

		// The feature-sync transport over the auth-aware server client, with a
		// dedicated unbounded-timeout client for large attachment uploads.
		builder.Services.AddTransient(sp =>
		{
			var factory = sp.GetRequiredService<IHttpClientFactory>();
			return new GeoServicesFeatureSync(
				factory.CreateClient(ServerHttpClient),
				uploadHttp: factory.CreateClient(UploadHttpClient));
		});
		builder.Services.AddSingleton<Func<GeoServicesFeatureSync>>(sp =>
			() => sp.GetRequiredService<GeoServicesFeatureSync>());

		// The shared, thread-safe record book over a SQLCipher-encrypted store. The
		// book builds the store lazily so the encryption key is fetched from secure
		// storage off the UI thread (no startup blocking).
		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-records.db");
		builder.Services.AddSingleton<IRecordStore>(_ => new LazyRecordStore(() => OpenEncryptedStoreAsync(dbPath)));
		builder.Services.AddSingleton(sp => new RecordBook(sp.GetRequiredService<IRecordStore>()));
		builder.Services.AddSingleton<IRelatedRecordStore>(_ => new LazyRelatedRecordStore(async _ =>
			new SqliteRelatedRecordStore(dbPath, await DbKeyProvider.GetOrCreateKeyAsync().ConfigureAwait(false))));
		builder.Services.AddSingleton<IRecordHistoryStore>(_ => new LazyRecordHistoryStore(async _ =>
			new SqliteRecordHistoryStore(dbPath, await DbKeyProvider.GetOrCreateKeyAsync().ConfigureAwait(false))));
		builder.Services.AddSingleton(sp => new RecordRevertService(
			sp.GetRequiredService<IRecordStore>(),
			sp.GetRequiredService<IRecordHistoryStore>()));
		builder.Services.AddSingleton<IInboxPullProgressStore, PreferencesInboxPullProgressStore>();

		// Shell tab pages resolved via constructor injection (no static service locator).
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<RecordsPage>();
		builder.Services.AddTransient<SyncPage>();
		builder.Services.AddTransient<GeometryCapturePage>();
		builder.Services.AddTransient<SignaturePage>();
		builder.Services.AddTransient<LoginPage>();
		builder.Services.AddTransient<Honua.Collect.App.Views.InboxPage>();
		builder.Services.AddTransient<ExportPage>();

		builder.Logging.AddDebug();

		return builder.Build();
	}

	private const int SqliteNotADatabase = 26;

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
