# 1.0.0 (2026-08-05)


### Bug Fixes

* install all non-Windows workloads everywhere, drop broken Windows RID override ([a9e21a2](https://github.com/lukislp/studylife-app/commit/a9e21a2b4f1ba5d17ac36fdcc3da91ba0fca2cc3))
* override TargetFrameworks (plural) to actually single-target restore ([a58e35e](https://github.com/lukislp/studylife-app/commit/a58e35e2f83f3e4452c27eccede3ee08625af7e9))
* patch the csproj's TargetFrameworks directly instead of a global -p: override ([b67f9e9](https://github.com/lukislp/studylife-app/commit/b67f9e9f45b34ed2b619f14eeae0253f6cc6ad01))
* remove the stale plural TargetFrameworks line on the Windows job ([3fa4057](https://github.com/lukislp/studylife-app/commit/3fa4057b161f84ca713cc06042df87578cf54c15))
* rename TargetFrameworks to singular TargetFramework, not just shrink the list ([38aaecd](https://github.com/lukislp/studylife-app/commit/38aaecdf809ed88827fe0a6f4d08dc4015ba9ef0))
* single-target the android/maccatalyst builds, move lint to macOS ([9e36f72](https://github.com/lukislp/studylife-app/commit/9e36f72121e547399f6282a954fe15500ac015d5))
* use the MAUI-specific workload IDs, not the bare platform SDKs ([b192c34](https://github.com/lukislp/studylife-app/commit/b192c3481c8a5cc6d18bac36443585cfd47a721c))


### Features

* add CI/CD pipeline with build-only releases (Android/macOS/Windows) ([ca02725](https://github.com/lukislp/studylife-app/commit/ca027252878cfa7cbba31c4517eba32e75314389))
