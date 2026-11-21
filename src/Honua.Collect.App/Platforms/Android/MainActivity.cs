// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Honua.Collect.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
