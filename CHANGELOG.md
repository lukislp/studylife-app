# [1.2.0](https://github.com/lukislp/studylife-app/compare/v1.1.0...v1.2.0) (2026-08-20)


### Features

* **health:** wire real INativeHealthData to the HealthKit HRV bridge ([0a3f672](https://github.com/lukislp/studylife-app/commit/0a3f6729504e6615ab3e64c179863a066671baaa))

# [1.1.0](https://github.com/lukislp/studylife-app/compare/v1.0.9...v1.1.0) (2026-08-20)


### Features

* **health:** add HRV read bridge for the Dashboard readiness score ([7336e5e](https://github.com/lukislp/studylife-app/commit/7336e5e597897e035c7c26430bdd2b83faa96bd5))

## [1.0.9](https://github.com/lukislp/studylife-app/compare/v1.0.8...v1.0.9) (2026-08-14)


### Bug Fixes

* remove ineffective print frame-resize workaround, rely on the CSS fix ([8f9901d](https://github.com/lukislp/studylife-app/commit/8f9901df3f4787d43df212e6896e53f082f34279))

## [1.0.8](https://github.com/lukislp/studylife-app/compare/v1.0.7...v1.0.8) (2026-08-14)


### Bug Fixes

* stop native iOS print from cutting off long pages mid-week ([bd0f310](https://github.com/lukislp/studylife-app/commit/bd0f31025cdd7795e2a3c16fbdb4203cfbfb7a38))

## [1.0.7](https://github.com/lukislp/studylife-app/compare/v1.0.6...v1.0.7) (2026-08-14)


### Bug Fixes

* add viewport-fit=cover to the app's own index.html ([0c98eb1](https://github.com/lukislp/studylife-app/commit/0c98eb199613afbf303039b2b14273a92a82cbf4))

## [1.0.6](https://github.com/lukislp/studylife-app/compare/v1.0.5...v1.0.6) (2026-08-14)


### Bug Fixes

* stop reserving native safe-area insets around the WebView ([5bdf32e](https://github.com/lukislp/studylife-app/commit/5bdf32efe2a0f534e09d9e8bc50f3409f74374b1))

## [1.0.5](https://github.com/lukislp/studylife-app/compare/v1.0.4...v1.0.5) (2026-08-08)


### Bug Fixes

* rebuild against studylife's latest shared UI ([7e772e4](https://github.com/lukislp/studylife-app/commit/7e772e4a8eff3d567151cbde3f477882fb215caa))

## [1.0.4](https://github.com/lukislp/studylife-app/compare/v1.0.3...v1.0.4) (2026-08-07)


### Bug Fixes

* add a mobile dashboard screenshot to the README ([ab5bddf](https://github.com/lukislp/studylife-app/commit/ab5bddfe0532482df3ad138d9d91240e9d51879a))

## [1.0.3](https://github.com/lukislp/studylife-app/compare/v1.0.2...v1.0.3) (2026-08-05)


### Bug Fixes

* use the dynamic license badge like the other repos ([bb660e9](https://github.com/lukislp/studylife-app/commit/bb660e97b37237f2f396a64aa40bd5bcfae0f334))

## [1.0.2](https://github.com/lukislp/studylife-app/compare/v1.0.1...v1.0.2) (2026-08-05)


### Bug Fixes

* use standard AGPL-3.0 license text so GitHub detects it correctly, add README badges ([cd7e2a6](https://github.com/lukislp/studylife-app/commit/cd7e2a657c9e5426ea15156579fb72291379bcf3))

## [1.0.1](https://github.com/lukislp/studylife-app/compare/v1.0.0...v1.0.1) (2026-08-05)


### Bug Fixes

* bump actions to their Node.js 24 major versions ([eab7a4c](https://github.com/lukislp/studylife-app/commit/eab7a4c18e1392c70a5ac004bcc367bd23165fed))
* clean up compiler warnings surfaced by the new CI build/lint jobs ([be77bad](https://github.com/lukislp/studylife-app/commit/be77bad7d652a6adc3d3036d999378f94e36bd7b)), closes [#if](https://github.com/lukislp/studylife-app/issues/if) [#if](https://github.com/lukislp/studylife-app/issues/if)

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
