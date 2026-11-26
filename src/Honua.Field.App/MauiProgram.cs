// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.App.Services;
using Honua.Field.App.Views;
using Honua.Field.Core.Enterprise;
using Honua.Field.Core.Field.Related;
using Honua.Field.Core.History;
using Honua.Field.Core.Storage;
using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Field.App;

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

	/// <summary>
	/// Named HTTP client for large attachment/media uploads to the server. Same
	/// base address, auth handler, and certificate pinning as <see cref="ServerHttpClient"/>,
	/// but with an effectively-infinite <see cref="HttpClient.Timeout"/>: the upload
	/// is bounded by a per-request, file-size-derived deadline inside
	/// GeoServicesFeatureSync instead, so a multi-hundred-MB attachment on a slow
	/// field uplink isn't aborted by the short total timeout the small query/edit
	/// requests use.
	/// </summary>
	public const string UploadHttpClient = "honua-upload";

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

		// Audit trail (BACKLOG E3): security- and data-relevant events are appended to
		// a durable, tamper-evident SQLite trail in the same encrypted at-rest posture
		// as the record store. Secrets are scrubbed before persisting; the trail can be
		// queried/exported off-device for SIEM/audit.
		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-records.db");
		var auditDbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-audit.db");
		var databaseKey = DbKeyProvider.Create(dbPath, auditDbPath);
		builder.Services.AddSingleton<IAuditStore>(_ => new LazyAuditStore(async _ =>
			new SqliteAuditStore(auditDbPath, await databaseKey.GetOrCreateAsync().ConfigureAwait(false))));
		builder.Services.AddSingleton(sp => new AuditTrail(sp.GetRequiredService<IAuditStore>()));
		builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<AuditTrail>());

		// On-device authorization (BACKLOG E2): the IdP's role claims on the session map
		// to capabilities via a data-driven capability map; the Presentation layer queries
		// IDeviceAuthorization.Can(action, resource) and authorization fails closed when no
		// session (or no granting role) is present. Denials are recorded to the audit trail.
		builder.Services.AddSingleton(_ => CapabilityMap.Build()
			.Role("field-worker",
				FieldPermission.CaptureRecords,
				FieldPermission.EditRecords,
				FieldPermission.SubmitRecords,
				FieldPermission.GenerateReports)
			.Role("supervisor",
				FieldPermission.CaptureRecords,
				FieldPermission.EditRecords,
				FieldPermission.SubmitRecords,
				FieldPermission.ReviewRecords,
				FieldPermission.DeleteRecords,
				FieldPermission.ExportData,
				FieldPermission.GenerateReports,
				FieldPermission.ManageAssignments)
			.Role("admin",
				FieldPermission.CaptureRecords,
				FieldPermission.EditRecords,
				FieldPermission.SubmitRecords,
				FieldPermission.ReviewRecords,
				FieldPermission.DeleteRecords,
				FieldPermission.ExportData,
				FieldPermission.GenerateReports,
				FieldPermission.ManageAssignments,
				FieldPermission.ConfigureForms)
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

		// Unauthenticated clients for third-party endpoints (still pooled by the factory).
		builder.Services.AddHttpClient(TileHttpClient, client =>
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd("HonuaField/1.0 (+https://honua.io)");
			client.Timeout = TileRequestTimeout;
		});
		builder.Services.AddHttpClient(AnthropicHttpClient, client => client.Timeout = AiRequestTimeout);

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
		builder.Services.AddSingleton(_ => new LazyRecordStore(async () =>
			await SqliteRecordStore.OpenEncryptedAsync(dbPath, await databaseKey.GetOrCreateAsync().ConfigureAwait(false)).ConfigureAwait(false)));
		builder.Services.AddSingleton<IRecordStore>(sp => sp.GetRequiredService<LazyRecordStore>());
		builder.Services.AddSingleton(sp => new RecordBook(sp.GetRequiredService<IRecordStore>()));
		builder.Services.AddSingleton<IRelatedRecordStore>(_ => new LazyRelatedRecordStore(async _ =>
			new SqliteRelatedRecordStore(dbPath, await databaseKey.GetOrCreateAsync().ConfigureAwait(false))));
		builder.Services.AddSingleton<IRecordHistoryStore>(_ => new LazyRecordHistoryStore(async _ =>
			new SqliteRecordHistoryStore(dbPath, await databaseKey.GetOrCreateAsync().ConfigureAwait(false))));
		builder.Services.AddSingleton(sp => new RecordRevertService(
			sp.GetRequiredService<IRecordStore>(),
			sp.GetRequiredService<IRecordHistoryStore>()));
		builder.Services.AddSingleton<IInboxPullProgressStore, PreferencesInboxPullProgressStore>();

		// Shell tab pages resolved via constructor injection (no static service locator).
		builder.Services.AddTransient<PublishedInspectionPages>();
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<RecordsPage>();
		builder.Services.AddTransient<SyncPage>();
		builder.Services.AddTransient<GeometryCapturePage>();
		builder.Services.AddTransient<SignaturePage>();
		builder.Services.AddTransient<LoginPage>();
		builder.Services.AddTransient<Honua.Field.App.Views.InboxPage>();
		builder.Services.AddTransient<ExportPage>();

		builder.Logging.AddDebug();

		return builder.Build();
	}
}
