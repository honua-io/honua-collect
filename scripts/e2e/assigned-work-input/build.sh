#!/usr/bin/env bash
# Build the disposable emulator's credential-entry helper, independent of Field.
set -euo pipefail
umask 077
source_dir=$(cd "$(dirname "$0")" && pwd)
out=${1:?output directory required}
mkdir -p "$out/classes"
out=$(cd "$out" && pwd)
sdk=${ANDROID_HOME:-${ANDROID_SDK_ROOT:?Android SDK required}}
platform=$(find "$sdk/platforms" -name android.jar | sort -V | tail -1)
tools=$(find "$sdk/build-tools" -name apksigner | sort -V | tail -1)
tools=${tools%/apksigner}
javac --release 11 -cp "$platform" -d "$out/classes" "$source_dir/FixtureInput.java"
"$tools/d8" --lib "$platform" --min-api 26 --output "$out" "$out/classes/io/honua/fixtureinput/FixtureInput.class"
"$tools/aapt" package -f -M "$source_dir/AndroidManifest.xml" -I "$platform" -F "$out/unsigned.apk"
(cd "$out" && "$tools/aapt" add unsigned.apk classes.dex >/dev/null)
keytool -genkeypair -keystore "$out/helper.jks" -storepass android -keypass android \
  -alias fixture -keyalg RSA -dname "CN=Disposable emulator test" >/dev/null 2>&1
"$tools/apksigner" sign --ks "$out/helper.jks" --ks-pass pass:android --out "$out/input.apk" "$out/unsigned.apk"
