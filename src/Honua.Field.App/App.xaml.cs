// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Honua.Field.App.Services;
#if HONUA_ASSIGNED_WORK_PILOT
using Honua.Field.Core.Enterprise;
using Honua.Field.Core.Storage;
#endif

namespace Honua.Field.App;

public partial class App : Application
{
	private readonly LazyRecordStore _records;

#if HONUA_ASSIGNED_WORK_PILOT
	private readonly AuthSessionManager _auth;
	private readonly IAuthSessionStore _sessions;
	private readonly AssignedWorkPilot _assignedWork;

	public App(LazyRecordStore records, AuthSessionManager auth, IAuthSessionStore sessions, AssignedWorkPilot assignedWork)
#else
	public App(LazyRecordStore records)
#endif
	{
		_records = records;
#if HONUA_ASSIGNED_WORK_PILOT
		_auth = auth;
		_sessions = sessions;
		_assignedWork = assignedWork;
#endif
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

#if HONUA_ASSIGNED_WORK_PILOT
			try
			{
				if (_sessions.Current is null)
				{
					await _auth.RestoreAsync();
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Restore assigned-work session] {ex.GetType().Name}");
			}
#endif
			window.Page = new AppShell();
#if HONUA_ASSIGNED_WORK_PILOT
			ConnectAssignedWork(window);
#endif
		}
	}
#if HONUA_ASSIGNED_WORK_PILOT
	private void ConnectAssignedWork(Window window)
	{
		var lifecycle = new CancellationTokenSource();
		var token = lifecycle.Token;
		_sessions.Changed += Synchronize;
		Connectivity.Current.ConnectivityChanged += Synchronize;
		window.Resumed += Synchronize;
		window.Destroying += Detach;
		Synchronize(null, EventArgs.Empty);

		async void Synchronize(object? sender, EventArgs args)
		{
			try
			{
				await _assignedWork.SynchronizeAsync(Connectivity.Current.NetworkAccess == NetworkAccess.Internet, token);
			}
			catch (OperationCanceledException)
			{
				// A session change or window teardown canceled this attempt.
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Assigned-work sync] {ex.GetType().Name}");
			}
		}

		void Detach(object? sender, EventArgs args)
		{
			_sessions.Changed -= Synchronize;
			Connectivity.Current.ConnectivityChanged -= Synchronize;
			window.Resumed -= Synchronize;
			window.Destroying -= Detach;
			lifecycle.Cancel();
			lifecycle.Dispose();
		}
	}
#endif

}
