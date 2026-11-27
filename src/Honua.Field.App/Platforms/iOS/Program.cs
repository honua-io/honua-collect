// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using ObjCRuntime;
using UIKit;

namespace Honua.Field.App;

public class Program
{
	// This is the main entry point of the application.
	static void Main(string[] args)
	{
		// if you want to use a different Application Delegate class from "AppDelegate"
		// you can specify it here.
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
