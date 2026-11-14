# StudyLife App (iOS / Android / Mac / Windows)

[![CI/CD](https://github.com/lukislp/studylife-app/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/lukislp/studylife-app/actions/workflows/ci-cd.yml) [![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/lukislp/studylife-app/badge)](https://scorecard.dev/viewer/?uri=github.com/lukislp/studylife-app) [![CodeQL](https://github.com/lukislp/studylife-app/actions/workflows/github-code-scanning/codeql/badge.svg)](https://github.com/lukislp/studylife-app/security/code-scanning)
[![Release](https://img.shields.io/github/v/release/lukislp/studylife-app)](https://github.com/lukislp/studylife-app/releases)
[![License: AGPL-3.0](https://img.shields.io/github/license/lukislp/studylife-app)](LICENSE)
[![.NET MAUI](https://img.shields.io/badge/.NET_MAUI-10.0-512BD4)](https://dotnet.microsoft.com/apps/maui)

Native app shell for [StudyLife](https://github.com/lukislp/studylife) built on **.NET MAUI Blazor Hybrid**.
The entire frontend (Razor components, CSS, JS interop, all 26 languages) comes **directly
from the studylife repo via a project reference** — no copy, so the app UI is always
automatically identical to the web/PWA UI. The app talks to the same REST API as the
browser client.

https://github.com/user-attachments/assets/53ada52d-7724-4734-8135-cc72395e1a1c

```
repos/
├── studylife/                  github.com/lukislp/studylife (Server, Client, Shared)
└── studylife-app/               this repo (github.com/lukislp/studylife-app)
    └── src/StudyLife.App/      MAUI Blazor Hybrid (references ../../studylife/src/StudyLife.Client)
```

**Important:** Both repos must be checked out side by side, in the same parent directory,
each under its own repo name (`studylife` and `studylife-app`) — the project reference is
relative.

![StudyLife mobile dashboard, showing the same shared UI this app's Blazor Hybrid shell renders natively](docs/screenshot.png)

*Captured from the live demo in a phone-sized viewport — this app's native shell renders the
exact same `StudyLife.Client` UI shown here, project-referenced rather than copied (see above).*

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
| Study readiness score + sleep consistency (Dashboard) | ✗ | ✓ (iOS, via Apple HealthKit - Heart Rate Variability + Sleep Analysis, read-only, on-device only) |
| Movement-break reminder (Focus Timer) | ✗ | ✓ (iOS, via Apple HealthKit step count, read-only) |
| Cardio fitness (VO2max) trend (Evaluation) | ✗ | ✓ (iOS, via Apple HealthKit, read-only - populated from Apple Watch outdoor walk/run workouts) |

## First launch

StudyLife is self-hosted, and the first screen says so: there is no central service, so the
app needs the address of your own server or of the one your school, university or organisation
runs (a link leads to the setup guide). The address (e.g. `https://studylife.example.com`, the
`https://` may be left out) is checked in plain words: nothing typed, not an address, no
answer from the server, or an answer that does not look like a StudyLife server (the app asks
`GET api/instance`, which every current server answers with an installation id and a version).
After a warning, a second tap on "Use anyway" saves the address regardless, for a server that
is down for a moment or is older than that endpoint. A server whose TLS certificate this device
does not trust (a private certificate authority, a self-signed certificate, a name that is not
on the certificate) gets its own message: install the certificate authority's profile on the
device or use the server's public address. There is no "use anyway" for it and the app never
relaxes certificate validation; every request would fail the same way. The address is stored in
the native app preferences. The rules live in `StudyLife.App.Core` (`ServerAddress`,
`ServerChecker`, `ConnectionFailure`, `ServerEntry`).

### Changing the server

**Setup, "Server"** shows the host the app is connected to with **Change server**; the
sign-in screen shows the same switch while signed out, so a mistyped address is no dead end.
That bar is rendered by the app shell itself (`AppRoot`), not by the shared sign-in page, because
it has to work without a working server; `SignInServerStatus` decides it in one place: signed out
with a saved address it is always there. The shell asks the saved server once (`api/instance`)
and names a problem next to the button: not reachable, certificate not trusted, not a StudyLife
server. When the server pinned its passkey domain (`Fido2:ServerDomain`, reported by
`api/instance`) and the saved address lies outside it (an internal name such as
`studylife.home.lan` for a server whose passkeys belong to its public domain), the bar says that
passkeys only work under that domain and offers **Use this address**, which runs the same
hand-over and switches to the public address.
After a confirmation, one hand-over runs (`ServerHandOver`), in this order: unregister this
device's push token on the old server, sign out on the old server (both with a five second
limit, skipped silently when the server is unreachable), drop the session and the offline
caches, wipe the widget, Apple Watch and Spotlight copies (`NativeLocalCopies.WipeAsync`, the
same function the account deletion uses), forget the address. A step that fails never stops
the next one. The app then restarts at the first-start screen. The account and its data on
the old server are untouched.

### Apple Health

The app never asks for Health access at start-up. Setup shows **Connect Apple Health**
(on iOS, where HealthKit exists); only that tap shows the system prompt, once. The state
comes from the system each time (`HealthAccessCoordinator`): connected (nothing to do, also for
installs that allowed access before), or declined, where Setup explains where to turn it on
in the Health app instead of asking again. Until access is granted, finished focus rounds are
not written to Health and the readiness tiles stay hidden.

The dialog and the rest of the app use the **language the system sets for the app** (see
"Language" below).

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

## Installing on Android

Every [release](https://github.com/lukislp/studylife-app/releases/latest) attaches
`StudyLife-android.apk`, signed with the project's release key. Its versionName is the
release version and its versionCode is derived from it (see
[Release signing](#android-release-signing-maintainers)), so each newer release installs as
an update over the previous one and keeps the app's data.

1. Download `StudyLife-android.apk` from the latest release.
2. Optionally verify it (next section).
3. Open it on the phone and allow installing from this source when Android asks.

To update, install the APK of a newer release the same way.

> **One-time step for builds up to v1.8.28.** Those releases were debug-signed with a key
> that changed on every build and all had versionCode 1, so Android refuses to update them
> ("App not installed"). Uninstall that build once, then install the current release.
> Uninstalling deletes the app's local data on the phone (server address, sign-in, local
> settings); everything stored on your StudyLife server stays. From then on every update
> installs in place.

### Verifying the download

Each asset has a keyless Sigstore signature bundle (`<asset>.sigstore.json`) and the release
carries a build provenance attestation (`provenance.intoto.jsonl`), both minted by this
repository's release workflow on `main`. A notarized `StudyLife-macos.zip` (see "Apple release
signing" below) is signed by the TestFlight workflow instead and has its own
`StudyLife-macos.zip.intoto.jsonl`; the regular expression below accepts both workflows:

```bash
gh release download --repo lukislp/studylife-app --pattern 'StudyLife-android.apk*'
cosign verify-blob --bundle StudyLife-android.apk.sigstore.json \
  --certificate-identity-regexp '^https://github.com/lukislp/studylife-app/\.github/workflows/(ci-cd|apple-testflight)\.yml@refs/heads/main$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com StudyLife-android.apk
gh attestation verify StudyLife-android.apk --repo lukislp/studylife-app
sha256sum StudyLife-android.apk
```

The release run's job summary ("Android APK (release)") lists the APK's SHA-256 and the
SHA-256 fingerprint of the signing certificate. The certificate is the same for every
release; check it with either of:

```bash
apksigner verify --print-certs StudyLife-android.apk   # Android SDK build-tools
keytool -printcert -jarfile StudyLife-android.apk      # any JDK
```

A different fingerprint than the previous release means the APK was not signed with the
project's key - do not install it.

## Building

Prerequisite: .NET 10 SDK + MAUI workloads (`dotnet workload restore` in the project
folder).

### Android (directly on Windows)

```powershell
cd src/StudyLife.App
dotnet publish -f net10.0-android -c Release
# APK: bin/Release/net10.0-android/publish/app.studylife.mobile-Signed.apk
```

A local build is signed with your machine's Android debug key: fine for sideloading onto your
own device, but it cannot update a release install (and a release cannot update it), because
Android only accepts an update signed with the same certificate.

### Android release signing (maintainers)

Pull requests build and sign a test APK with a throwaway key generated on the runner
(artifact `android-test-apk`), which exercises the release path without any secret. Releases
are built by the `release-android` job of `.github/workflows/ci-cd.yml` with
`scripts/build-android-apk.sh` after the dry run of the lktec-bot release module has determined the version:

- versionName = the release version, e.g. `1.8.29`
- versionCode = `major * 10000 + minor * 100 + patch` (1.8.29 -> 10829). Monotonic while minor
  and patch stay below 100; the script rejects anything else instead of producing a lower
  code. Android's limit (2100000000) leaves room up to major 209999. Builds up to v1.8.28 all
  had versionCode 1.

The job reads four repository secrets and fails - and with it the whole release - when one of
the first three is missing; a debug-signed APK is never published:

| Secret | Content |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | the keystore file, base64 encoded |
| `ANDROID_KEYSTORE_PASSWORD` | keystore password |
| `ANDROID_KEY_ALIAS` | alias of the key inside the keystore |
| `ANDROID_KEY_PASSWORD` | optional, key password; defaults to the keystore password |

Creating the key once (any JDK's `keytool`; PKCS12 keystores use the store password for the
key as well, so `ANDROID_KEY_PASSWORD` stays unset):

```bash
keytool -genkeypair -v -keystore studylife-release.jks -storetype PKCS12 \
  -alias studylife -keyalg RSA -keysize 4096 -validity 10000 \
  -dname "CN=StudyLife"
keytool -list -v -keystore studylife-release.jks -alias studylife | grep SHA256   # fingerprint

base64 -w0 studylife-release.jks | gh secret set ANDROID_KEYSTORE_BASE64 --repo lukislp/studylife-app
gh secret set ANDROID_KEYSTORE_PASSWORD --repo lukislp/studylife-app   # prompts for the value
gh secret set ANDROID_KEY_ALIAS --repo lukislp/studylife-app --body studylife
```

Keep the keystore file and its password backed up outside GitHub (a secret cannot be read
back). Losing the key means no release can ever update existing installs again - every user
would have to uninstall once more. `-validity 10000` (about 27 years) keeps the certificate
valid for the app's lifetime.

The job summary of each release run lists the signer certificate's SHA-256; it must match the
fingerprint printed above.

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
  or installed from TestFlight/the App Store (Apple strips `embedded.mobileprovision` there;
  those builds are always signed with `aps-environment`, `scripts/apple/entitlements/ios-app.plist`)
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

### Apple release signing (maintainers)

Releases never wait for Apple signing or for an approval: every push to `main` with a
release publishes the tag, the GitHub release, the Android APK, the Windows ZIP and an ad-hoc
signed `StudyLife-macos.zip` (job `release-macos`, no Apple secret). The iPhone app (with its
Apple Watch app) and the Mac app go to **TestFlight** (internal testers only, no Beta App
Review) only when the owner starts the separate workflow **Apple TestFlight**
(`.github/workflows/apple-testflight.yml`) for a release of his choice - releases can be
skipped and shipped together later. The local Mac pipeline above (direct device installs) is
unchanged.

| Job | Workflow, trigger | What it does |
| --- | --- | --- |
| `test-ios` | `ci-cd.yml`, every PR and push | builds the full iOS bundle (`scripts/apple/release-ios.sh`) and signs it ad hoc |
| `test-maccatalyst` | `ci-cd.yml`, every PR and push | builds the Mac app, signs both variants ad hoc with their real entitlements, launch-checks them (`scripts/apple/release-maccatalyst.sh`) |
| `release-macos` | `ci-cd.yml`, push to `main` with a release | the same build with the release version, ad hoc signed (hardened runtime): `StudyLife-macos.zip` for the GitHub release |
| `resolve` | `apple-testflight.yml`, manual | checks the chosen tag (form `vX.Y.Z`, has a GitHub release, its commit is on `main`) and resolves it to that commit |
| `testflight-ios` | `apple-testflight.yml`, manual, approval | App Store signature, upload of `StudyLife-ios.ipa` to TestFlight (skipped with `macos_zip_only`) |
| `testflight-macos` | `apple-testflight.yml`, manual, approval | App Sandbox + Mac App Store profile + installer signature, upload of `StudyLife-macos.pkg` to TestFlight; with the Developer ID secrets also a notarized `StudyLife-macos.zip` |
| `publish-notarized-zip` | `apple-testflight.yml`, after `testflight-macos` | only with a notarized ZIP: signs it (Sigstore), attests its provenance and replaces the release's ad-hoc signed ZIP |

**Shipping a TestFlight build.** Actions -> "Apple TestFlight" -> "Run workflow" (branch
`main`) -> enter the release tag, e.g. `v1.8.30`, or leave it empty for the latest release ->
"Run workflow". Then approve the two deployments of the environment `release` in the run
("Review deployments" -> select both -> "Approve and deploy"). The jobs check out exactly the
tagged commit (the sibling `lukislp/studylife` checkout is its current `main`, as in the
release run) and use no caches. After Apple's processing (usually minutes, sometimes an hour)
the builds show up in App Store Connect -> TestFlight.

**What App Store Connect checks that this repository answers.** An upload is rejected
(ITMS-91053) when a bundle uses a required reason API without declaring it in its privacy
manifest; the manifest, the purpose strings and the reasoning are in
[Privacy and permissions](#privacy-and-permissions), and `test-ios`/`test-maccatalyst` fail
when the finished bundle lacks either. The App Privacy questionnaire is answered in App Store
Connect by the owner; the expected answer and its two caveats are in the same section. Export
compliance needs no answer (`ITSAppUsesNonExemptEncryption` is false in both Info.plists).

The TestFlight jobs use the GitHub Environment **`release`** (required reviewer: the owner,
deployment branches: `main` only). Its secrets never reach pull requests or other jobs, and
nothing runs with them before the owner approves. A missing TestFlight secret fails that job;
nothing unsigned is uploaded.

**The Mac release ZIP.** Every release carries the ad-hoc signed `StudyLife-macos.zip`
(Gatekeeper asks once: right-click "Open", or `xattr -cr`). If the optional Developer ID
secrets are set, a TestFlight run also builds a Developer ID signed, notarized and stapled ZIP,
and `publish-notarized-zip` uploads it over the ZIP of that release together with a new
`StudyLife-macos.zip.sigstore.json` (signer: `apple-testflight.yml` on `main`) and
`StudyLife-macos.zip.intoto.jsonl` (its build provenance). The release's
`provenance.intoto.jsonl` still lists the digest of the replaced ad-hoc ZIP; it stays valid for
the other assets, and `gh attestation verify StudyLife-macos.zip --repo lukislp/studylife-app`
finds the new attestation. Without the Developer ID secrets the run leaves the release
untouched (notice in the run).

*Only the notarized ZIP.* For a release that is already in TestFlight (or when only the Mac
download matters), start "Apple TestFlight" with **`macos_zip_only`** ticked: `testflight-ios`
is skipped, `testflight-macos` builds and signs the Mac app as usual but uploads nothing to
TestFlight (so no `rebuild` is needed), and `publish-notarized-zip` replaces the release's ZIP.
Approve the one `release` deployment. In this mode the Developer ID secrets are mandatory: the
job fails at "Apple signing secrets configured?" with the names of the missing secrets
instead of quietly falling back to the ad-hoc ZIP. The TestFlight secrets of `testflight-macos`
are still needed, because the same script also builds the `.pkg`.

*What the job checks before publishing.* `scripts/apple/release-maccatalyst.sh` signs the
app inside out with the Developer ID Application identity, hardened runtime and secure
timestamp (entitlements: `scripts/apple/entitlements/maccatalyst-developer-id.plist`),
submits it with `xcrun notarytool submit --wait` (authenticated with the App Store Connect API
key `ASC_API_KEY_*`; on rejection the notary log is printed), staples the ticket and then
runs `codesign --verify --deep --strict`, `xcrun stapler validate` and
`spctl -a -vvv -t exec` (must report `source=Notarized Developer ID`), launches the app once,
zips it with `ditto -c -k --sequesterRsrc --keepParent`, unpacks that ZIP again and repeats the
three checks on the unpacked app. Its SHA-256 is in the job summary of both jobs.

No Developer ID provisioning profile is needed: the Mac app uses no capability that requires
one (push, App Groups, HealthKit, Associated Domains and the widget/share/watch extensions
exist on iOS only; the Mac build is a single bundle).

*Verifying the notarized ZIP on a Mac:*

```bash
gh release download v1.8.46 --repo lukislp/studylife-app --pattern 'StudyLife-macos.zip*'
cosign verify-blob --bundle StudyLife-macos.zip.sigstore.json \
  --certificate-identity-regexp '^https://github.com/lukislp/studylife-app/\.github/workflows/apple-testflight\.yml@refs/heads/main$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com StudyLife-macos.zip
gh attestation verify StudyLife-macos.zip --repo lukislp/studylife-app
shasum -a 256 StudyLife-macos.zip                  # matches the job summary
mkdir unpacked && ditto -x -k StudyLife-macos.zip unpacked
APP="$(find unpacked -maxdepth 1 -name '*.app' | head -1)"
codesign --verify --deep --strict --verbose=2 "$APP"
codesign -dvv "$APP" 2>&1 | grep -E '^(Authority|TeamIdentifier)='   # Developer ID Application: ..., team ID
xcrun stapler validate "$APP"                      # "The validate action worked!"
spctl -a -vvv -t exec "$APP"                       # "accepted", "source=Notarized Developer ID"
```

(Use the tag you want to check.) Opening the app by double-click must then work without any
Gatekeeper warning, also offline (stapled ticket).

*Rotating the Developer ID certificate.* Developer ID Application certificates are valid for
several years (the expiry date is shown in Certificates, Identifiers & Profiles); renew it
before it expires or right away if its key may have leaked. Create a new certificate signing
request (Keychain Access or `openssl req -new -newkey rsa:2048 -nodes`), create a new
**Developer ID Application** certificate from it in the portal (Account Holder role), export
certificate + private key as `.p12` (legacy encryption, see below), and replace
`APPLE_DEVELOPER_ID_P12_BASE64` (`base64 -i developer-id.p12 | tr -d '\n'`) and
`APPLE_DEVELOPER_ID_P12_PASSWORD` in the environment `release`. Then run the workflow with
`macos_zip_only` for the latest release to prove the new certificate. Already published ZIPs
stay valid after the old certificate expires (the signature has a secure timestamp and the
notarization ticket stays); only on a suspected key leak revoke the old certificate, which
makes Gatekeeper reject everything signed with it, then republish the ZIPs that are still
offered with a `macos_zip_only` run per release. Nothing else (profiles, API key) depends on
this certificate.

Versions: `CFBundleShortVersionString` is the release version, `CFBundleVersion` uses the
Android versionCode formula (`major * 10000 + minor * 100 + patch`, 1.8.30 -> 10830), set on
the app and on every embedded bundle (App Store Connect rejects mismatches). Every release
therefore has a new, higher build number. `ITSAppUsesNonExemptEncryption` is `false` (the
app only uses system TLS and hashing), so builds need no export-compliance answer.

**Bundle identifiers and capabilities** (Certificates, Identifiers & Profiles -> Identifiers;
the App Group is `group.app.studylife.mobile`):

| Bundle ID | Bundle | Capabilities |
| --- | --- | --- |
| `app.studylife.mobile` | iOS app, also the Mac Catalyst app (same ID, one App Store Connect record with iOS and macOS) | App Groups, Push Notifications, HealthKit |
| `app.studylife.mobile.widgets` | widgets / Live Activity extension | App Groups |
| `app.studylife.mobile.share` | share extension | App Groups |
| `app.studylife.mobile.watchkitapp` | Apple Watch app | App Groups |
| `app.studylife.mobile.watchkitapp.widgets` | watch complications | App Groups |

Associated Domains is not needed: the in-app passkey dialog is switched off (see "Paid
developer account" above), so release builds do not carry that entitlement. The entitlements
each bundle is signed with are in `scripts/apple/entitlements/` and
`src/StudyLife.App/Platforms/MacCatalyst/Entitlements.plist`; `scripts/apple/signing.py`
adds the identity keys from the profile and fails with the missing capability's name if a
profile does not grant a declared entitlement.

**Certificates and profiles:**

| Item | Used for | Valid |
| --- | --- | --- |
| Apple Distribution certificate | iOS app + bundles, Mac TestFlight app | 1 year |
| Mac Installer Distribution certificate (common name "3rd Party Mac Developer Installer") | the TestFlight `.pkg` | 1 year |
| Developer ID Application certificate (optional; required for `macos_zip_only`) | notarized `StudyLife-macos.zip` | several years (see the portal) |
| App Store profiles (iOS) for the five bundle IDs above | iOS | 1 year, or until the certificate is revoked |
| Mac Catalyst App Store profile for `app.studylife.mobile` | Mac TestFlight | 1 year |
| App Store Connect API key, role **Developer** (team key) | TestFlight upload (`altool`) and notarization (`notarytool`) | until revoked |

Export each certificate with its private key as `.p12`. macOS only imports the legacy
encryption: from OpenSSL 3 use `openssl pkcs12 -export -legacy ...` (Keychain Access exports
are fine). `scripts/apple/keychain.sh` imports them into a temporary keychain together with
Apple's intermediate certificates (downloaded, SHA-256 pinned) and deletes the keychain and
every decoded file at the end of the job.

**Secrets of the `release` environment** (all base64 values are single-line base64 of the
file):

| Secret | Content | Needed by |
| --- | --- | --- |
| `APPLE_TEAM_ID` | 10-character team ID | both |
| `APPLE_DISTRIBUTION_P12_BASE64` / `APPLE_DISTRIBUTION_P12_PASSWORD` | Apple Distribution `.p12` and its password | both |
| `APPLE_MAC_INSTALLER_P12_BASE64` / `APPLE_MAC_INSTALLER_P12_PASSWORD` | Mac Installer Distribution `.p12` | `testflight-macos` |
| `APPLE_DEVELOPER_ID_P12_BASE64` / `APPLE_DEVELOPER_ID_P12_PASSWORD` | Developer ID Application `.p12` (optional, both or neither; required with `macos_zip_only`) | `testflight-macos` |
| `APPLE_PROFILE_IOS_BASE64` | profile for `app.studylife.mobile` (iOS App Store) | `testflight-ios` |
| `APPLE_PROFILE_IOS_WIDGETS_BASE64` | profile for `app.studylife.mobile.widgets` | `testflight-ios` |
| `APPLE_PROFILE_IOS_SHARE_BASE64` | profile for `app.studylife.mobile.share` | `testflight-ios` |
| `APPLE_PROFILE_WATCH_BASE64` | profile for `app.studylife.mobile.watchkitapp` | `testflight-ios` |
| `APPLE_PROFILE_WATCH_WIDGETS_BASE64` | profile for `app.studylife.mobile.watchkitapp.widgets` | `testflight-ios` |
| `APPLE_PROFILE_MAC_BASE64` | Mac Catalyst App Store profile for `app.studylife.mobile` | `testflight-macos` |
| `ASC_API_KEY_P8_BASE64` | App Store Connect API key (`AuthKey_<id>.p8`) | both |
| `ASC_API_KEY_ID` / `ASC_API_ISSUER_ID` | key ID and issuer ID of that key | both |

The jobs check each profile before signing (team, bundle ID, platform, App Store type,
expiry; a warning 30 days before it expires), so a wrong or expired file fails with a
readable message instead of an App Store Connect rejection.

**Mac sandbox.** The TestFlight Mac build runs in the App Sandbox with network client access,
read access to files the user picks (`<InputFile>`: backup restore, ICS import) and printing.
Voice dictation is not wired up on Mac Catalyst, so no microphone entitlement. The Developer ID
build is not sandboxed (hardened runtime, `com.apple.security.cs.allow-jit` for the .NET
runtime). The two Mac builds store their settings in different places (sandbox container vs.
`~/Library`), so switching between them starts with a fresh server URL and sign-in.

**Network egress.** The TestFlight jobs run Harden-Runner in block mode with Apple's upload,
notary, timestamp and certificate hosts allowed. If Apple moves an upload host and a release
job fails on a blocked domain ("Post Harden Runner" lists it), set the repository variable
`APPLE_RELEASE_EGRESS_POLICY` to `audit`, re-run the failed job, add the host to the list in
`apple-testflight.yml` and delete the variable again. A blocked upload host does not fail fast:
`altool` keeps retrying without output, so `upload-testflight.sh` stops it after 30 minutes
(`UPLOAD_TIMEOUT_SECONDS`) with an error pointing here (run it with `ALTOOL_VERBOSE=1` for
altool's own log).

**Re-runs.** TestFlight accepts each build number only once. If one TestFlight job succeeded and
the other failed, use "Re-run failed jobs". To upload a tag that already reached TestFlight
again (e.g. after renewing a profile), start the workflow with the input `rebuild` set to `1`
(then `2`, ...): the build number becomes `10830.1`, a new, higher build of the same version
(`CFBundleVersion` allows up to three dot-separated integers). Without it the second upload
fails at the end of the job with App Store Connect's duplicate-build error, and the job log
points to `rebuild`.

**Renewals.** Apple Distribution and Mac Installer certificates expire after a year; create new
ones, regenerate all six profiles (a profile is bound to its certificate), and replace the
`.p12`, password and profile secrets. Profiles alone also expire yearly - regenerate and
replace them. The Developer ID certificate lives longer and needs no profile (see "Rotating
the Developer ID certificate" above). The API key stays valid until it is revoked.

## Architecture notes

- `src/StudyLife.App.Core` — plain net10.0 library with the decisions that need no device
  (server address rules, `GET api/instance` check, TLS trust failures, the sign-in bar, the
  hand-over order, when Health access is asked for); unit-tested in `tests/StudyLife.App.Core.Tests` (CI job `test-unit`,
  `dotnet test tests/StudyLife.App.Core.Tests`). The MAUI app implements its small interfaces
  (`AppHandOverSteps`, `NativeHealthData`).
- `Components/AppRoot.razor` — root: first-launch dialog → boot sequence (session token,
  UI language from the system setting for the app via `UiLanguageStartup`, before the dialog texts load) like in
  `StudyLife.Client/Program.cs` → renders
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
- `Services/HealthBridge.cs` — facade over the Swift Apple HealthKit bridge
  (`native/ios-liveactivity/HealthBridge.swift`); backs the studylife repo's
  `INativeHealthData` (HRV, sleep onset, step count, cardio fitness/VO2max) - read-only, one
  combined authorization prompt shown when the user taps "Connect Apple Health" in Setup (never
  at startup), data never leaves the device.
- `Services/NativeTelemetry.cs` + `Services/TelemetryBridge.cs` — back the studylife repo's
  `INativeTelemetry`/`IClientPlatform` (see "Telemetry (native)" below).

## Telemetry (native)

This app implements the studylife repo's client telemetry contract
(`docs/ARCHITECTURE.md` there, "Telemetry") via `INativeTelemetry`/`IClientPlatform` -
`Services/NativeTelemetry.cs`, `Services/AppClientPlatform.cs`, `Services/AppLaunchTiming.cs`,
`Services/TelemetryBridge.cs` (+ `native/ios-liveactivity/TelemetryBridge.swift`).

**What is collected, native-only, on top of the web contract's boot/vitals/api/error events:**

- `app_launch` — `webviewReadyMs` (all platforms, Stopwatch from `MauiProgram.CreateMauiApp` to
  the BlazorWebView's first rendered component) and `coldMs`/`warmMs` (iOS only, MetricKit
  averages - never approximated any other way).
- `app_resource` — iOS only, MetricKit's daily peak memory / cumulative CPU time / cellular+wifi
  bytes aggregate.
- `error` with `kind: native_crash | native_hang` — iOS crash/hang diagnostics from MetricKit,
  Android crashes from an `AppDomain.UnhandledException`/`AndroidEnvironment.UnhandledExceptionRaiser`
  handler. Only the exception type, a sanitized stack (frame lines only, capped at 4 KB, no
  message text) and its SHA-256 hash ever leave the device - never the exception message, which
  can contain user content.
- `health_query` — one event per Apple HealthKit read (`NativeHealthData.cs`): `kind`
  (`hrv`/`sleep`/`steps`/`vo2max`), `durationMs`, a coarse `authorization` guess (HealthKit's
  bridge doesn't expose the real authorization status - "granted" is inferred from "data came
  back", "undetermined" from an empty result, which conflates denied/undetermined/no-data-in-range)
  and a bucketed `result` (`empty`/`below_minimum`/`sufficient`/`error`) - **never** the actual
  sample/night count, and never any health value itself.
- `push` — `registered` (APNs token registration succeeded) / `received` (a notification arrived
  while foregrounded, or was tapped) - no push payload content.
- `error` with `kind: dotnet | js` — unexpected failures the app caught itself (a print dialog
  that could not be shown, a Live Activity that did not come up although the device said it
  supports one, an unhandled JavaScript error in the web view): only a short code or the error
  type and a sanitized stack, never a message. At most 5 per code and app run. See
  "Developer diagnostics" below.

**Consent and network egress:** this app never sends anything over the network itself. Every
event above is written to `NativeTelemetry`'s on-device queue (a small JSON file in
`FileSystem.AppDataDirectory`, so events recorded before the WebView/consent decision even exist
- e.g. a cold-start crash - survive a relaunch); the studylife repo's `TelemetryService` drains
that queue on its own flush cycle and is the only thing that ever calls `POST /api/telemetry`,
gated on the same `UserSettings.TelemetryConsent` toggle as the browser client's own telemetry
(off/undecided ⇒ nothing native leaves the device either).

### Developer diagnostics

Nothing diagnostic is ever shown to a user of a release build. `Services/DeveloperDiagnostics.cs`
is the single place that decides what happens to a diagnostic; printing (`NativeBridge`), the
Live Activity self-check (`TimerLiveActivity`) and unexpected JavaScript errors (`wwwroot/index.html`
-> `NativeBridge.ReportJsError`) all go through it:

| Build | Platform log | Telemetry queue (`error` event) | Red banner + notification | JS error |
|---|---|---|---|---|
| Release / store / CI | yes | yes (sent only after consent, to your own server) | never | the normal Blazor error bar ("An unhandled error has occurred." + Reload), no stack |
| `Debug`, or `-p:DeveloperDiagnostics=true` | yes | yes | yes | developer banner with message and stack instead of the error bar |

"Not supported here" is not a diagnostic: Live Activities are unavailable on Mac Catalyst,
below iOS 16.1 (iPadOS 17 on iPad) and when the user switched them off, and in all of these the timer just
runs without a lock screen card, silently.

To chase a problem on your own phone with a release-configuration build, add
`-p:DeveloperDiagnostics=true` to the build. The platform log of a release build is visible with
`xcrun devicectl device process launch --console ...` or in Console.app (lines start with
`[Diag:`).

**Finding crashes:** native crashes land in Grafana/Loki as `ClientError` log events with
`kind=native_crash` (or `native_hang`), tagged with `platform`/`appVersion`/`stackHash` - query
`{app="studylife-server"} | json | kind="native_crash"` (adjust the Loki label to whatever this
deployment names the server's log stream) and cross-reference `stackHash` to correlate repeat
crashes across sessions without needing the full stack every time.

**Symbolicating an iOS MetricKit stack with `atos`:** MetricKit frames are pre-symbolicated
where Apple already has the symbol (system frameworks), but frames from this app's own binary
need the matching dSYM. `scripts/build-ios-ipa.sh` copies every dSYM produced by a build to
`artifacts/dsym/<git-short-sha>/` (gitignored - these are build outputs, keyed by the exact
commit that produced them). Given a `stack` line like `StudyLife.App + 12345 (0xabcde)` from a
Loki log entry:

```bash
atos -o artifacts/dsym/<sha>/StudyLife.App.app.dSYM/Contents/Resources/DWARF/StudyLife.App \
     -arch arm64 -l <app's load address, if known> 0xabcde
```

Without a known load address, `-l` can be omitted and the raw `offsetIntoBinaryTextSegment`
passed as the address instead - close enough to identify the function/file for triage purposes.

## Privacy and permissions

### Permissions the app asks for

Each prompt has a purpose string in `Platforms/iOS/Info.plist` (German base value) and in all 26
`Platforms/iOS/<language>.lproj/InfoPlist.strings`; Mac Catalyst uses the same translation files
(the csproj bundles them into both targets) and its own `Platforms/MacCatalyst/Info.plist`. A
capability the app does not use has no string.

| Prompt | Key | What uses it |
| --- | --- | --- |
| Microphone | `NSMicrophoneUsageDescription` | Notes dictation: `getUserMedia` + `MediaRecorder` in the web view. The recording goes to the user's own server for transcription. |
| Speech recognition | `NSSpeechRecognitionUsageDescription` | Fallback of the same dictation: the web view's `webkitSpeechRecognition` (Apple's speech recognition), used when recording is not possible. Without the key the first use would crash the app. |
| Local network | `NSLocalNetworkUsageDescription` | Connecting to a StudyLife server on the user's LAN (a NAS, a Raspberry Pi, a `.local` name). |
| Health (iOS) | `NSHealthShareUsageDescription`, `NSHealthUpdateUsageDescription` | Readiness score, movement breaks, VO2max; Mindful Minutes. Read on the device only. |

Notifications and push need no purpose string (runtime prompt). On Mac Catalyst the dictation
needs the entitlement `com.apple.security.device.audio-input` as well, which is in
`Platforms/MacCatalyst/Entitlements.plist` (App Sandbox, TestFlight/Mac App Store) and in
`scripts/apple/entitlements/maccatalyst-developer-id.plist` (hardened runtime, notarized ZIP).
Why Mac gets dictation instead of hiding it: the Notes page of the shared UI always shows the
dictation button and has no switch for native capabilities (it lives in the studylife repository),
so the app cannot take the feature away on one platform; it works through the same web view code
as on iOS, so the Mac build gets the strings and the entitlement. Not yet exercised on a Mac - see
the pull request that added this for what to try.

### Privacy manifest

`Platforms/iOS/Resources/PrivacyInfo.xcprivacy` is the manifest of the app, for iOS and (same
file, the csproj includes it in both targets) Mac Catalyst: no tracking, no tracking domains, no
collected data types, and the required reason APIs the bundle uses:

| Category | Reason | Evidence |
| --- | --- | --- |
| File timestamp | `C617.1` | .NET runtime/BCL baseline ([Microsoft](https://learn.microsoft.com/dotnet/maui/ios/privacy-manifest)); the app's own `File.Exists`/`ReadAllText` in the app container and the App Group inbox |
| System boot time | `35F9.1` | .NET runtime baseline; `Stopwatch` for the launch timing (elapsed time between two events in the app) |
| Disk space | `E174.1` | .NET runtime baseline only; the app never reads free space |
| User defaults | `CA92.1` | MAUI `Preferences` (`ServerUrlStore`, `StreakMilestones`) and `NSUserDefaults.StandardUserDefaults` (`AppDelegate`): values only the app reads and writes. The widgets and the Watch app share data through files in the App Group, not through `UserDefaults(suiteName:)`, so there is no `1C8F.1` |

The widget, share and Watch extensions have no manifest: their Swift code uses no required reason
API (Apple's documentation asks for a manifest in each bundle whose own code uses one; system
frameworks and the Swift runtime do not count). The Swift bridge compiled into the app
(`native/ios-liveactivity`) is part of the app bundle and covered by its manifest.
Sources: Apple, [Privacy manifest files](https://developer.apple.com/documentation/bundleresources/privacy-manifest-files)
and [Describing use of required reason API](https://developer.apple.com/documentation/bundleresources/describing-use-of-required-reason-api).

### Privacy gate

`scripts/privacy/check.py` (Python standard library, tests beside it) fails the CI job
`test-privacy` when

- a manifest is not a valid property list, declares tracking, tracking domains or collected data
  types, or uses a category or reason Apple does not define,
- the sources of a bundle use a required reason API its manifest does not declare (the app's
  manifest also has to carry the .NET baseline above), or a bundle without manifest uses one at all,
- a purpose string is missing from an `Info.plist` or from any `InfoPlist.strings`.

[`scripts/privacy/policy.toml`](scripts/privacy/policy.toml) is the one place for the rules: the
categories with their approved reason codes and the API names that put a file into a category,
the bundles with their sources and manifests, and the purpose strings. When the gate reports an
API, add the reason to the manifest (and an entry to the table above) - or, for an extension,
add a `PrivacyInfo.xcprivacy` to its target in `native/ios-signing/project.yml` and set it in the
policy. `python3 scripts/privacy/check.py --inventory` prints which bundle uses what. The Apple
build scripts (`scripts/apple/release-ios.sh`, `release-maccatalyst.sh`, run by `test-ios` and
`test-maccatalyst`) check the finished bundle for the manifest and the localised strings.

### Privacy policy

The app publisher's privacy policy is published on `legal.lktec.org`:
[German](https://legal.lktec.org/studylife/) and [English](https://legal.lktec.org/en/studylife/).
It says that the operator of a StudyLife server is responsible for that server.

- **App Store Connect "Privacy Policy URL"** (guideline 5.1.1(i)): `https://legal.lktec.org/en/studylife/`
  (`https://legal.lktec.org/studylife/` for a German-language listing).
- **In the app** the link belongs in the About section of Setup, which is part of the shared UI in the
  `studylife` repository (`SetupAboutCard`), not in this repository; German UI links the German page,
  every other language the English one.

### App Store Connect: App Privacy

For the app as distributed from this repository the expected answer is **Data Not Collected**:
the app talks to the server the user types in on first launch and to nothing the developer
operates (no analytics SDK, no advertising identifier, no tracking; the telemetry events and the
push token go to that server, and telemetry only after the user's consent). That is the
reading of Apple's definition ("collect" means data leaves the device to the developer or third
parties), which is the developer's to confirm in App Store Connect. Two caveats the code makes
visible: a demo or review instance operated by the developer is a server that does collect (account,
notes, ...) and would change the answer, and the Marketplace browser calls the public GitHub API
(`api.github.com`) from the device, which exposes the device's IP address to GitHub.

### What stays on the device after sign-out

Nothing of the account's content. Every sign-out removes the copies the app keeps outside the web
view (`NativeLocalCopies.WipeAsync`, the one function account deletion and the server switch use
as well): the home screen widget snapshot (iOS App Group container, Android app data) and a
widget refresh, the copy on a paired Apple Watch (replaced by an empty snapshot), the "next
session starts soon" Live Activity, and the Spotlight index entries (notes and courses). This
holds for a manual sign-out, for a session the server declared invalid (expired or revoked), and
for the sign-out that account deletion and the server switch end in: the shared client has a single
"logged out" hook for all of them and `AppRoot` composes the wipe behind it (`SignOutLocalCopies`).
Only the saved server address stays (it belongs to the device, see "Changing the server"), and a
note shared into the app that has not been sent yet waits for the next sign-in instead of being
dropped.

## Language

The Blazor UI (via the referenced Client project) is fully localized into 26 languages, same
as the web app. **The app has no language picker: its language is the operating system's
language setting for the app.**

- **iPhone, iPad, Mac:** Settings, StudyLife, Language (the list comes from
  `CFBundleLocalizations` in `Platforms/iOS/Info.plist` and `Platforms/MacCatalyst/Info.plist`).
  Without a choice there, the app follows the device language.
- **Android 13 and later:** Settings, Apps, StudyLife, Language (declared in
  `Platforms/Android/Resources/xml/locales_config.xml`); older Android versions follow the system
  language.
- **Windows:** the Windows display language.

Where the web app shows its language picker, the app shows a short hint with a button that
opens that setting (`IClientPlatform.LanguageFromSystem`, the one switch for it, implemented in
`AppClientPlatform`). `SystemAppLanguage` reads the setting; the shared rule `UiLanguage.FromSystem`
in the `studylife` repository maps it to one of the 26 languages (English when none matches). On
every start, and when the app returns to the foreground after the setting changed, the app applies
it and, signed in, reports it to the server (the same `ReportLanguageAsync` the web picker uses),
so the server's own texts (push notifications, reports, reminders) follow it. The sign-in, sign-up
and device-link pages the app opens in the system browser get the app's language as `applang`, so
they do not fall back to whatever that browser stored.

Native-only surfaces added by this repo (home screen widgets, Live Activity, Siri Shortcuts, the
Apple Watch companion app, the iOS permission prompts and home-screen shortcuts) use standard
iOS/watchOS `.lproj`/`Localizable.strings` localization and so follow the same setting. Siri's
spoken trigger phrases are the one exception, currently German/English only. The notifications
the shell raises itself (stand-up nudge, streak milestone) use the `NativeNotificationText` table
of the Client project. Still German-only native code: the Android home screen widget
(`Platforms/Android/StudyTodayWidgetProvider.cs`).

## License

Copyright (C) 2026 Lukas Koerber

[AGPL-3.0](LICENSE) — consistent with the main [StudyLife](https://github.com/lukislp/studylife)
repo, since this app is built directly on top of its (AGPL-3.0) Client project via a project
reference.

Music in the demo video above: "Happy Beats & Business Moves Vol. 12" by Sascha Ende
([ende.app](https://ende.app/en/song/12881-happy-beats-business-moves-vol-12)), licensed under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

### What is in the bundle

- **Own code, AGPL-3.0.** The app's C# and Swift code in this repository and the
  `StudyLife.Client` / `StudyLife.Shared` libraries compiled into it from the
  [studylife](https://github.com/lukislp/studylife) repository (including its `wwwroot`). All of
  it is copyright Lukas Koerber, the sole copyright holder, under AGPL-3.0.
- **Third-party packages.** NuGet packages (the .NET MAUI and ASP.NET Core Blazor libraries under
  MIT, Markdig under BSD-2-Clause), the .NET/Mono runtime (MIT) and the DM Sans / DM Mono web
  fonts (SIL OFL-1.1). There is no GPL, AGPL or LGPL third-party code in the bundle.
- **One file-level copyleft component.** `Toolbelt.Blazor.I18nText` is MPL-2.0. The licence only
  covers that package's own files, which are used unmodified; its source is at
  <https://github.com/jsakamoto/Toolbelt.Blazor.I18nText/>.

[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) lists every third-party package with its
licence, copyright holder and project and carries the licence texts, with the copyright notices
of the MIT/BSD packages printed above the licence text they belong to. It names no versions and no
years, so dependency updates do not touch it. It is generated and ships
inside the app as the raw asset `THIRD-PARTY-NOTICES.md`.

### Licence gate

`scripts/licences/check.py` resolves the NuGet graph of the iOS and Mac Catalyst targets
(`dotnet restore`), reads each package's licence from its `.nuspec` and compares it with
[`scripts/licences/policy.toml`](scripts/licences/policy.toml), the single place that says which
licences are allowed, which families are denied (GPL, AGPL, LGPL) and which packages are
exceptions, each with a written reason. A licence that cannot be read counts as unknown and
fails. The licence is read from the version actually resolved, so a licence change in an update
is caught. The CI job `test-licences` fails on a violation and on a notices file that is out of
date, which means a package was added or removed or its licence, copyright holder or project URL
changed (never a plain version bump or a new copyright year); then regenerate it with `python3 scripts/licences/check.py --update`. The
`dependency-review` check additionally refuses GPL/AGPL/LGPL dependencies on a pull request.

### App Store distribution

The FSF's position is that GPL/AGPL/LGPL-licensed third-party code cannot be distributed through
the App Store. As things stand, the bundle contains AGPL-3.0 code of a single copyright holder
and no GPL/AGPL/LGPL third-party code, plus the MPL-2.0 component above. This section states
facts only; it grants no additional permission.
