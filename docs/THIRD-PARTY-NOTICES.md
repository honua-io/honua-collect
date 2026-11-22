# Third-party notices

Honua Collect is built on open-source software. This file lists the third-party components distributed with Honua Collect binaries and mobile app packages, together with their licenses and required attribution notices. Each component remains under its own license; the Honua Collect license (see `LICENSE`) applies only to Honua Geo Inc.'s own code. Test-only dependencies are not distributed and are not listed.

The .NET package table is verified against `Directory.Packages.props` and `src/**/*.csproj` by `scripts/check-license-hygiene.py` in CI.

## .NET packages (shipped in libraries and app binaries)

<!-- nuget-table:start (verified by scripts/check-license-hygiene.py) -->
| Package | Version | License | Project |
|---|---|---|---|
| Honua.Mobile.Maui | 0.1.0-alpha.1 | Apache-2.0 | https://github.com/honua-io/honua-mobile |
| Honua.Mobile.Offline | 0.1.0-alpha.1 | Apache-2.0 | https://github.com/honua-io/honua-mobile |
| Honua.Mobile.Sdk | 0.1.0-alpha.1 | Apache-2.0 | https://github.com/honua-io/honua-mobile |
| Honua.Sdk.Field | 2.2.0 | Apache-2.0 | https://github.com/honua-io/honua-sdk-dotnet |
| Microsoft.Data.Sqlite.Core | 11.0.0 | MIT | https://docs.microsoft.com/dotnet/standard/data/sqlite/ |
| Microsoft.Extensions.Http | 11.0.0 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Debug | 11.0.0 | MIT | https://dot.net/ |
| Microsoft.Maui.Controls | 10.0.130 | MIT | https://github.com/dotnet/maui |
| Plugin.Maui.Audio | 4.0.0 | MIT | https://github.com/jfversluis/Plugin.Maui.Audio |
| SkiaSharp | 4.156.1 | MIT | https://go.microsoft.com/fwlink/?linkid=868515 |
| SQLitePCLRaw.bundle_e_sqlcipher | 2.1.11 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| ZXing.Net | 0.16.11 | Apache-2.0 | https://github.com/micjahn/ZXing.Net/ |
<!-- nuget-table:end -->

## Native libraries and Apache-2.0 attribution notices

The following attribution notices are preserved in accordance with Apache License 2.0 Section 4(d) and BSD-3-Clause redistribution terms:

- **SQLitePCL.raw (`SQLitePCLRaw.bundle_e_sqlcipher`)** — Copyright Eric Sink and contributors. Licensed under the Apache License, Version 2.0. Bundles SQLCipher (Copyright Zetetic LLC, BSD-3-Clause) and SQLite (Public Domain).
- **ZXing.Net (`ZXing.Net`)** — Copyright 2012 Michael Jahn and ZXing authors. Licensed under the Apache License, Version 2.0. Port of ZXing ("Zebra Crossing") barcode scanning library (Copyright ZXing authors, Apache-2.0).
- **SkiaSharp (`SkiaSharp`)** — Copyright Microsoft Corporation. Licensed under the MIT License. Bundles the Skia graphics library (Copyright Google Inc., BSD-3-Clause).
- **Honua SDK packages (`Honua.Sdk.Field`, `Honua.Mobile.*`)** — Copyright Honua Geo Inc. Licensed under the Apache License, Version 2.0.
