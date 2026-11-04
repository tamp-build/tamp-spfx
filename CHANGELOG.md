# Changelog

All notable changes to `Tamp.Spfx` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] — 2026-11-04

### Added

- `Spfx.StartDevServerAsync`: `heft start` through Tamp.Heft's `HeftDevServer`, returning an `SpfxDevServer` that builds SharePoint debug-page URLs, including Form Customizer new, edit and view pages, from the project's `serve.json` serve configurations ([#1](https://github.com/tamp-build/tamp-spfx/issues/1)).
- `SpfxDebugUrl.Build`: the debug-page URL, matching `@microsoft/spfx-heft-plugins` 1.24.1 character for character (verified against vectors the rig computed). Unlike the rig, it throws on an unknown configuration name or a `{tenantDomain}` placeholder with no tenant domain, rather than producing a wrong or unusable URL.
- `SpfxServeConfig`: `config/serve.json` (hostname / ipAddress, port, https, initialPage, serveConfigurations), read as the rig reads it.
- `Spfx.TrustDevCertificate`: `heft trust-dev-cert`.

### Changed

- Requires `Tamp.Heft.V1` 0.2.0. CI now uses the published package instead of building tamp-heft from source.

## [0.1.0] — 2026-10-31

### Added

- Initial release, verified against SharePoint Framework 1.24.0 (Heft toolchain).
- `Spfx.Build` / `Test` / `PackageSolution` / `Clean`: Heft verbs with `--production` on by default. Gulp-based projects are refused with an explanation.
- `SpfxProject.Load`: reads `package.json`, `config/package-solution.json` and `config/config.json` (comments and trailing commas allowed) and reports the toolchain, the SPFx version, the solution, externals, and every rig output location (`.sppkg`, `lint.sarif`, Cobertura, JUnit, dependency audit).
- `SpfxSolutionVersion` (4-part, numeric ordering, `FromSemVer`) and `PackageSolutionEditor.SetVersion`, a version stamp that changes only the version literals and is idempotent.
- `SppkgInspector`: reads the package for its digest, app manifest, permission requests, features, de-duplicated components (custom-script requirement, external scripts, dependencies), extension registrations and per-asset digests. It never extracts to disk, prohibits DTDs, and caps entry count and size.
- `SppkgPolicy`: the deploy gate (SPFX001–SPFX025), including a comparison against the deployed release's inspection; `SppkgPolicyResult.ThrowIfFailed`.
- `SppkgSarif`: SARIF 2.1.0 output with logical locations and stable subjects.
