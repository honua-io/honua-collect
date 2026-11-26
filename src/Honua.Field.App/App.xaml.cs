// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Honua.Field.App.Services;

namespace Honua.Field.App;

public partial class App : Application
{
	private readonly LazyRecordStore _records;

	public App(LazyRecordStore records)
	{
		_records = records;
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
		var message = new Label { Text = "Opening field data…" };
		var retry = new Button { Text = "Retry", IsVisible = false };
		var startup = new ContentPage
		{
			Title = "Honua Field",
			Content = new VerticalStackLayout
			{
				Padding = 24,
				Spacing = 12,
				VerticalOptions = LayoutOptions.Center,
				Children = { message, retry },
			},
		};
		var window = new Window(startup);
		startup.Loaded += OpenRecords;
		retry.Clicked += OpenRecords;
		return window;

		async void OpenRecords(object? sender, EventArgs args)
		{
			startup.Loaded -= OpenRecords;
			retry.IsVisible = false;
			message.Text = "Opening field data…";
			try
			{
				// SQLCipher key derivation and schema upgrades can take time on older devices.
				await Task.Run(() => _records.GetStoreAsync());
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Open field database] {ex.GetType().Name}");
				message.Text = ex is InvalidOperationException
					? ex.Message
					: "Field data could not be opened. Unlock the device and retry. If this persists, keep the app installed and contact your administrator.";
				retry.IsVisible = true;
				return;
			}

			window.Page = new AppShell();
		}
	}
}
