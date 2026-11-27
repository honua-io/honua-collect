// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Android.App;
using Android.Runtime;

namespace Honua.Field.App;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
