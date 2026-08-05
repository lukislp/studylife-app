# StudyLife App (iOS / Android / Mac / Windows)

[![CI/CD](https://github.com/lukislp/studylife-app/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/lukislp/studylife-app/actions/workflows/ci-cd.yml)
[![Release](https://img.shields.io/github/v/release/lukislp/studylife-app)](https://github.com/lukislp/studylife-app/releases)
[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![.NET MAUI](https://img.shields.io/badge/.NET_MAUI-10.0-512BD4)](https://dotnet.microsoft.com/apps/maui)

Native app shell for [StudyLife](https://github.com/lukislp/studylife) built on **.NET MAUI Blazor Hybrid**.
The entire frontend (Razor components, CSS, JS interop, all 26 languages) comes **directly
from the studylife repo via a project reference** — no copy, so the app UI is always
automatically identical to the web/PWA UI. The app talks to the same REST API as the
browser client.

```
repos/
├── studylife/                  github.com/lukislp/studylife (Server, Client, Shared)
└── studylife-app/               this repo (github.com/lukislp/studylife-app)
    └── src/StudyLife.App/      MAUI Blazor Hybrid (references ../../studylife/src/StudyLife.Client)
```

**Important:** Both repos must be checked out side by side, in the same parent directory,
each under its own repo name (`studylife` and `studylife-app`) — the project reference is
relative.

## What the app can do beyond the iOS PWA

| Feature | PWA (iOS) | Native App |
|---|---|---|
| Home screen quick actions ("Start Focus", "New Note", "Calendar") | ✗ (manifest shortcuts are ignored) | ✓ (long-press the icon, iOS + Android) |
| Timer/session notifications | only with the PWA installed + Web Push | ✓ native local notifications (even in the foreground) |
| App icon badge | limited | ✓ (iOS/Mac) |
| Screen stays on during focus timer (wake lock) | ✗ in WKWebView | ✓ native (`KeepScreenOn`) |
| Passkey login | ✓ (Safari) | ✓ via system browser sheet (see below) |
| Lock screen / Dynamic Island focus timer (Live Activity) | ✗ | ✓ (iOS; local updates on any tier, remote push updates while the app is fully closed need paid signing - see below) |
| Siri/Shortcuts "Open Focus" | ✗ | ✓ (iOS, navigates straight to the focus page) |
| Home screen widget (today's study time, streak) | ✗ | ✓ (iOS/Android) |
| Share sheet → new note | ✗ | ✓ (share any text from another app directly into a StudyLife note) |

## First launch

On first launch, the app asks for the **server URL** (e.g. `https://studylife.example.com`)
and validates it via a `manifest.json` request. The URL is stored in the native app
preferences. To change it: clear app data (iOS: reinstall the app, Android: "Clear
storage") and restart.

## Passkey login (how it works)

WebAuthn doesn't exist in native WebViews (WKWebView/Android WebView). The app therefore
opens the server's **actual** login page in a system browser sheet when logging
in/registering/linking a device (iOS: `ASWebAuthenticationSession`, Android: Custom
Tabs) — passkeys work fully there (Face ID, iCloud Keychain, Google Password Manager).
After completion, the page hands the session token back to the app via a
`studylife://auth?token=…` redirect (client-side, gated on `?app=1` — no server-side
special path, the normal web flow is untouched).

No associated-domains entitlement needed ⇒ **works with a free Apple account**.

Known limitation: "Add an additional passkey on THIS device" from Settings
(register/begin-additional) needs WebAuthn in the WebView and doesn't work in the app —
use the device-link flow (`/link`) instead, which works fully. Releasing/managing
passkeys in Settings works normally (plain HTTP).

## Building

Prerequisite: .NET 10 SDK + MAUI workloads (`dotnet workload restore` in the project
folder).

### Android (directly on Windows)

```powershell
cd src/StudyLife.App
dotnet publish -f net10.0-android -c Release
# APK: bin/Release/net10.0-android/publish/app.studylife.mobile-Signed.apk
```

The APK is debug-signed and directly sideloadable (allow "unknown sources" on the
device).

### iOS — the production path (own signing pipeline on the Mac)

> **Why not Sideloadly?** Its signer doesn't set the `CS_EXECSEG_MAIN_BINARY` flag on
> app extensions — AMFI then kills the Live Activity extension immediately ("has
> entitlements but is not a main binary", verified via the kernel log). The custom
> pipeline uses Apple's original `codesign` + `devicectl` and doesn't have this problem.

**One-time setup** (Mac with Xcode + .NET 10 SDK + `dotnet workload restore`):
1. Add the Apple ID in Xcode (Xcode → Settings → Accounts) — a free account works too.
2. Connect the iPhone once via USB to the Mac ("Trust"), enable Developer Mode on the
   iPhone.
3. `bash scripts/provision.sh` — creates a headless certificate + profiles (App and
   widget extension, via the xcodegen project in `native/ios-signing`) and registers the
   device.
4. Store the Mac login password for headless keychain access (stays local):
   `printf '%s' 'PASSWORD' > ~/.studylife-sign-pass && chmod 600 ~/.studylife-sign-pass`

**Building + installing** (from then on at any time, even entirely via SSH from the PC):
```bash
bash scripts/build-ios-ipa.sh      # .NET publish + swiftc bridge + Xcode widget extension
bash scripts/sign-and-install.sh   # Apple codesign (inside-out) + devicectl install (USB/Wi-Fi)
```
**7-day rule (free account):** simply run `sign-and-install.sh` again — or use the
launchd auto-renewal job (see `scripts/install-renewal-agent.sh`), so it happens by
itself over Wi-Fi and the signature never expires. Data/login is always preserved.

**Paid developer account:** normal signing/TestFlight/App Store. Two features detect the
paid entitlement themselves at runtime (`AppleSigningInfo`) — free-signed builds
automatically fall back, no switch to forget and no separate code paths to maintain:

- **Live Activity remote push** (lock screen countdown keeps updating through phase
  changes even while the app is fully terminated, via APNs) activates automatically once
  built with a paid, push-capable provisioning profile (`AppleSigningInfo.HasPushEntitlement`)
  and the server has an APNs key configured (`Apns__*`, see the main app repo's
  [`docs/ARCHITECTURE.md`](https://github.com/lukislp/studylife/blob/main/docs/ARCHITECTURE.md)). Live Activities themselves (the lock screen card while the
  app is open/backgrounded) work on **any** signing tier — only the remote-push keep-alive
  needs paid signing.
- **Native in-app passkey dialog** (Face ID directly in the app, without a browser sheet)
  is implemented (`AppleInAppPasskeys`, gated on `AppleSigningInfo.HasAssociatedDomains`)
  but **deliberately hardcoded off** (`NativeAppAuth.SupportsInAppPasskeys => false`)
  regardless of signing tier: since iOS 14, `swcd` only ever loads the
  `apple-app-site-association` file via Apple's own CDN, which in turn needs the domain
  to be crawlable from the public internet — this server is deliberately internal-only, so
  the CDN can never verify it and the native dialog fails deterministically
  (`ASAuthorizationError code=1004`). The system-browser flow (`AuthenticateAsync`) stays
  the only passkey path unless the server ever becomes publicly reachable, at which point
  flipping `SupportsInAppPasskeys` back to `AppleSigningInfo.HasAssociatedDomains` is the
  only change needed.

### Mac (Catalyst) / Windows

```bash
dotnet build -f net10.0-maccatalyst -c Release    # on the Mac
dotnet build -f net10.0-windows10.0.19041.0       # on Windows (dev/test target)
```

## Architecture notes

- `Components/AppRoot.razor` — root: first-launch dialog → boot sequence (session token,
  default language "de") like in `StudyLife.Client/Program.cs` → renders
  `StudyLife.Client.App`. Also consumes quick-action routes (`DeepLinkService`).
- `wwwroot/index.html` — 1:1 copy of the client `index.html` scripts + a native bridge
  block that redirects Wake Lock/Notifications/Badge/Vibration to
  `Services/NativeBridge.cs` (JSInvokable). Function names/signatures unchanged ⇒ client
  services notice no difference.
- Web Push stays PWA-only (`subscribePush` returns `null` in the app); the app uses local
  notifications instead. `window.print()` is a no-op in the WebView (as it has always
  been in installed PWAs).
- Additive changes in the studylife repo: `INativeAppAuth` (+ no-op registration in
  `Program.cs`) and `?app=1` branches in `Login/Register/Link.razor`. Nothing changes in
  the browser client.
- `Services/TimerLiveActivity.cs` — facade over the Swift bridge (`native/ios-liveactivity`);
  `TimerLiveActivityCoordinator` additionally registers the ActivityKit push token with the
  server (`PUT /api/timerstate/liveactivity-token`) so the worker can push phase transitions
  via APNs while the app is terminated (paid signing only, see above).
- `Services/HomeWidgetSnapshot.cs` — writes a JSON snapshot to the shared App Group
  container for the home screen widget (`native/ios-liveactivity/Widget/StudyTodayWidget.swift`);
  the widget itself makes no network calls, it only ever shows the state as of the last
  app contact.
- `Services/SharedNoteIntake.cs` — drains text handed in via the OS share sheet (Android
  `ACTION_SEND` intent, iOS share extension via an App Group inbox file) into a new note.

## Language

The Blazor UI (via the referenced Client project) is fully localized into 26 languages, same
as the web app. Native-only surfaces added by this repo — home screen widgets, Live Activity,
Siri Shortcuts, notification text, the Apple Watch companion app — are currently hardcoded in
German, since that's this app's primary usage context. Contributions to localize these are
welcome; see the web client's i18n setup in the main app repo for the established pattern.

## License

Copyright (C) 2026 Lukas Koerber

[AGPL-3.0](LICENSE) — consistent with the main [StudyLife](https://github.com/lukislp/studylife)
repo, since this app is built directly on top of its (AGPL-3.0) Client project via a project
reference.
