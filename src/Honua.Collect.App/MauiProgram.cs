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
		builder.Services.AddHttpClient(ServerHttpClient, client => client.BaseAddress = settings.BaseUri)
			.AddHttpMessageHandler<AuthHeaderHandler>();

		// Unauthenticated clients for third-party endpoints (still pooled by the factory).
		builder.Services.AddHttpClient(TileHttpClient,
			client => client.DefaultRequestHeaders.UserAgent.ParseAdd("HonuaCollect/1.0 (+https://honua.io)"));
		builder.Services.AddHttpClient(AnthropicHttpClient);

		// The feature-sync transport over the auth-aware server client.
		builder.Services.AddTransient(sp =>
			new GeoServicesFeatureSync(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ServerHttpClient)));

		// One durable record store + the shared, thread-safe record book.
		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-records.db");
		builder.Services.AddSingleton<IRecordStore>(_ => new SqliteRecordStore(dbPath));
		builder.Services.AddSingleton<RecordBook>();

		builder.Logging.AddDebug();

		var app = builder.Build();
		ServiceHelper.Initialize(app.Services);
		return app;
	}
}
