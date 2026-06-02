# On-device verification — Honua Collect (Android)

The Honua Collect MAUI app was built, signed, installed, and **run end-to-end on
an Android emulator** (Google APIs x86_64, **API 35**, KVM-accelerated). These
screenshots are the captured evidence that the assembled, user-facing app
functions — not just the unit-tested logic layer.

## How it was run

```bash
# Toolchain bootstrapped into $HOME (no root): Temurin JDK 17 + Android SDK
export JAVA_HOME=$HOME/jdk17 ANDROID_HOME=$HOME/android-sdk
export PATH=$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH

# Self-contained Release APK (embedded assemblies; the Debug APK uses Fast
# Deployment and cannot be sideloaded standalone)
dotnet build src/Honua.Collect.App -t:SignAndroidPackage -f net10.0-android -c Release \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$HOME/jdk17 \
  -p:EmbedAssembliesIntoApk=true

emulator -avd honua -no-window -gpu swiftshader_indirect &
adb install -r src/Honua.Collect.App/bin/Release/net10.0-android/io.honua.collect-Signed.apk
adb shell am start -n io.honua.collect/crc6490b85cd101108deb.MainActivity
```

`apksigner` verifies the APK under the v1/v2/v3 signing schemes; package
`io.honua.collect` ("Honua Collect"), minSdk 21 / targetSdk 36.

## What the screenshots prove

| File | Demonstrates |
| --- | --- |
| `01-home.png` | Home screen renders (Shell + MainPage). |
| `02-form-initial.png` | The **dynamic form** renders from a `FormDefinition`: text/choice/toggle/media widgets, **live "required" validation**, an error summary, and **Submit disabled** while invalid. The conditional *Notes* field is hidden. |
| `03-conditional-notes-revealed.png` | After entering "Asset ID" (its error clears) and setting *Serviceable → No*, the **conditional *Notes* field appears** — live visibility evaluation on device. |
| `04-repeat-row-added.png` | Tapping **Add** on the **repeatable Deficiency section** creates a row with its own *Kind* (required) + *Severity* fields; the outstanding-error count rises 2 → 3. |

All capture behaviour is driven by the platform-neutral
`FormPageViewModel` (unit-tested in `Honua.Collect.Presentation.Tests`); the XAML
is a thin binding surface over it.
