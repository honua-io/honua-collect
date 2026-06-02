using Honua.Collect.Core.Storage;

namespace Honua.Collect.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		// Hydrate the record store from local SQLite so Drafts/Outbox/Sent survive
		// app restarts. The database lives under the app's private data directory.
		var dbPath = Path.Combine(FileSystem.AppDataDirectory, "collect-records.db");
		CaptureStore.InitializeAsync(new SqliteRecordStore(dbPath)).GetAwaiter().GetResult();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
