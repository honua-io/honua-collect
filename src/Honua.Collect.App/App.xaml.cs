using System.Diagnostics;

namespace Honua.Collect.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		// Safety net for fire-and-forget event handlers (async void) and background
		// tasks: surface otherwise-swallowed exceptions to the log instead of losing
		// them. A production build would route these to telemetry.
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Debug.WriteLine($"[Unobserved] {e.Exception}");
			e.SetObserved();
		};
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			Debug.WriteLine($"[Unhandled] {e.ExceptionObject}");
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The record book hydrates lazily (RecordBook.InitializeAsync) the first time
		// a screen reads it, so startup stays off the UI thread.
		return new Window(new AppShell());
	}
}
