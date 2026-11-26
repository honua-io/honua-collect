// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Honua.Field.App;

// Keep the launcher component used by the installed Honua Collect app.
[Activity(Name = "crc6490b85cd101108deb.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
