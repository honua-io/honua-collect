using Honua.Collect.App.Services;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Honua.Collect.App;

public static class MauiProgram
{
	/// <summary>The named HTTP client configured with the server base address and auth handler.</summary>
	public const string ServerHttpClient = "honua";

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

		// Configuration loaded from the bundled asset — no endpoints or keys in code.
		var settings = AppSettings.LoadAsync().GetAwaiter().GetResult();
		builder.Services.AddSingleton(settings);

		// Auth: the session store holds the signed-in credential (falling back to the
		// dev key); the delegating handler attaches it to every server request, so
		// sign-in actually changes what the transport sends.
		builder.Services.AddSingleton<IAuthSessionStore>(_ => new AuthSessionStore(settings.DemoApiKey));
		builder.Services.AddTransient<AuthHeaderHandler>();
		builder.Services.AddHttpClient(ServerHttpClient, client => client.BaseAddress = settings.BaseUri)
			.AddHttpMessageHandler<AuthHeaderHandler>();

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
