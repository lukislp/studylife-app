# Contributing to StudyLife App

Thanks for taking the time. The StudyLife companion app is a single-maintainer project, so the
process is deliberately small - but it is the same for every change, including the maintainer's
own.

## How changes get in

1. Open an issue first for anything bigger than a typo or an obvious bug fix, so the direction can
   be agreed before you spend time on it. Use the templates under `.github/ISSUE_TEMPLATE/`.
2. Fork the repository (or branch, if you have write access) and make your change on a branch.
3. Open a pull request against `main`. The pull-request template asks for what changed and why.
4. `main` is protected: a PR merges only after the test stage of
   [`.github/workflows/ci-cd.yml`](.github/workflows/ci-cd.yml) is green and the branch is up to
   date with `main` (enable auto-merge and it lands on its own once that is the case). Nobody
   pushes to `main` directly, not even the maintainer.

## This repository does not stand alone

The app's Blazor UI comes from the main [`studylife`](https://github.com/lukislp/studylife)
repository by project reference, not by copy: `StudyLife.App.slnx` pulls in
`../studylife/src/StudyLife.Client` and `../studylife/src/StudyLife.Shared`. Check both
repositories out as siblings:

```
your-workspace/
  studylife/
  studylife-app/
```

Every CI job does the same thing with a second checkout of `lukislp/studylife` into `./studylife`.
A UI change usually belongs in that repository; this one holds the native shell, platform
services and packaging.

## What a pull request needs

- **Conventional Commits.** The version and the changelog are generated from the commit messages
  (`feat:` = minor release, `fix:` = patch release, `build:`/`ci:`/`docs:`/`test:` = no release).
  Squash-merge keeps the PR title as the commit message, so give the PR a Conventional Commit
  title.
- **Green required checks.** `test-android`, `test-maccatalyst`, `test-windows`, `test-lint` and
  `review / dependency-review` are required; a red one blocks the merge. Note what these are:
  each one restricts the project to a single target framework, installs that platform's MAUI
  workload and runs a Release **publish**. They are build gates, not a unit-test suite - this
  repository has no test project.
- **Every platform still has to build.** A change that compiles for Android but breaks Mac
  Catalyst or Windows is caught by the matching job. iOS is built from the Mac by hand
  (`scripts/build-ios-ipa.sh`), not in CI, so iOS-specific code needs a device build before the
  PR is merged.
- **Formatting.** `test-lint` runs
  `dotnet format src/StudyLife.App/StudyLife.App.csproj --verify-no-changes` against the Android
  target framework, which stands in for all of them because `dotnet format` cannot be scoped to
  one framework. Run `dotnet format` before pushing.
- **Do not commit the CI target-framework edit.** Each job rewrites `TargetFrameworks` to a single
  `TargetFramework` with `sed` before building. That is a CI-only step; the csproj in the
  repository keeps the full multi-target list.

## Running things locally

Prerequisite: the .NET 10 SDK plus the MAUI workloads (`dotnet workload restore` in the project
folder).

```powershell
cd src/StudyLife.App
dotnet publish -f net10.0-android -c Release
# APK: bin/Release/net10.0-android/publish/app.studylife.mobile-Signed.apk
```

```bash
dotnet build -f net10.0-maccatalyst -c Release    # on the Mac
dotnet build -f net10.0-windows10.0.19041.0       # on Windows (dev/test target)
```

iOS goes through the Mac, once set up with `bash scripts/provision.sh`:

```bash
bash scripts/build-ios-ipa.sh      # .NET publish + swiftc bridge + Xcode widget extension
bash scripts/sign-and-install.sh   # codesign (inside-out) + devicectl install (USB/Wi-Fi)
```

## Security issues

Please do not open a public issue for a vulnerability - use the private reporting path described
in [SECURITY.md](SECURITY.md). The [Code of Conduct](CODE_OF_CONDUCT.md) applies to every
interaction in this repository.
