# Changelog

All notable changes to `Tamp.Spfx` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-11-05 - the SPFx pipeline ships its own order, and the Node pair is checked first

### Changed

- **Releases now create a GitHub Release and announce their version on the issues they carry**, matching core ([tamp#159](https://github.com/tamp-build/tamp/issues/159), [tamp#164](https://github.com/tamp-build/tamp/issues/164)). A published version used to leave a bare tag: the Releases tab advertised an older version, nobody watching was notified, and an issue closed before its release was cut never got its version. `CHANGELOG.md` stays the single source of truth and both are derived from it. The publish job gains `contents: write` and `issues: write`, scoped to that job alone.

### Added

- **`SpfxPipelineBuild` — the SPFx graph, in the order it has to run** ([#9](https://github.com/tamp-build/tamp-spfx/issues/9)). Every adopter re-derived `Restore → Stamp → Test → Package → Gate`, which made the gate's position a per-project decision. That is exactly where it goes wrong: a gate upstream of evidence publication suppresses the evidence **in the runs where findings exist**, so evidence arrives reliably when there is nothing to report and unreliably when there is. [tamp#151](https://github.com/tamp-build/tamp/issues/151) is the demonstration, and core's remedy was to ship the order rather than document it.

  Four stages — **preflight** (project + Node check) → **collect** (restore, stamp, test, package) → **publish** (inspect, evaluate, SARIF) → **enforce** (policy verdict) — with every stage from collect onward `AssuredAfterFailure()`, so the inspection and its SARIF are written even when a test failed or the policy is about to fail the build.

  The policy is **evaluated in publish**, not in the gate: `SppkgSarif` describes a policy *result*, so producing the evidence requires running the policy. Only the verdict belongs to enforcement. With no policy configured the default still yields an inspection-describing SARIF.

  Seams: `SpfxWebDirectory` (required), `SpfxSolutionVersionSource`, `SpfxDeployPolicy`, `SpfxProductionPackage`, `SpfxRequireSupportedNode`, `SpfxNodeCompatibility`, `SpfxRestorePlan()`. Policy and version source both default to **nothing**, because inventing a version would make the shipped version a surprise and inventing a policy would enforce an opinion this package does not hold.

  The aggregate target is `SpfxAll`, not `Spfx`: a member named `Spfx` on the build class would shadow the `Spfx` facade for every other member in it.

- **SPFx/Node compatibility checking, before the build** ([#9](https://github.com/tamp-build/tamp-spfx/issues/9)). Nothing validated the pair, and an unsupported one does not fail clearly — it fails inside Heft, or in a native-module build, or produces a silently different bundle. `SpfxPreflight` names it first, which is the only point at which the check is worth anything.

  **Three outcomes, not two.** `Unknown` — the SPFx version could not be read, or is newer than the data — is never reported as `Supported`, because reporting an unchecked pair as a good pair is how a check becomes worse than no check. It never fails the build either: "nothing was checked" is not evidence of a problem, nor of its absence, so it is logged as loudly as a failure. **Warn by default**, `SpfxRequireSupportedNode` to fail, since an unsupported-but-working pair is a real situation mid-upgrade.

  **The table is data**, not a compiled-in dictionary: `spfx-node-compatibility.json` is embedded and transcribed from Microsoft's compatibility reference with the date it was taken, and `SpfxNodeSupport.LoadFrom` teaches a build about a newer SPFx release without waiting for a package release. Two details a flattened table would lose are kept: `v16.13+` is a `minimumMinor`, so Node 16.9 is correctly rejected for SPFx 1.17; and where patches within one minor differ — 1.8.0/1.8.1 allow Node 8 while 1.8.2 adds 10 — the minor entry holds the **narrower** set, because claiming support Microsoft does not is the one direction this must never fail in.

  `SpfxProject` now reads `engines.node`, used as a cross-check and reported when it disagrees with the bundled table, since the rig declares its own range there and that is newer than any transcription. It is a textual read of the majors a range enumerates, **not** semver satisfaction — an unbounded `>=22.14.0` allows 24 and 26 too, so that form yields no opinion rather than a wrong one. It is also the most common form a rig writes, so a wrong answer there would have fired on nearly every project.

## [0.3.0] — 2026-11-04

### Added

- `SpfxDevServer.StopReason`, `HasExited`, `IsStopping`, `ExitCode` and `WaitForExitAsync`: the dev server's lifecycle, passed through from `HeftDevServer`. `StopReason` is `requested`, `signal`, `exited` or `timeout` (`Tamp.ServiceStopReason`), the same reason `service.stopped` carries, so a deliberate stop can be told from a failure without reading the exit code, which is non-zero after a kill ([#4](https://github.com/tamp-build/tamp-spfx/issues/4)).

### Changed

- Requires `Tamp.Heft.V1` 0.3.0 / `Tamp.Core` 1.18.0. `HeftDevServer` now runs on Tamp.Core's `ManagedProcess`, so inside a `tamp` run the dev server honours the build's cancellation (Ctrl+C, SIGTERM, SIGHUP) as well as the token passed to `Spfx.StartDevServerAsync`, emits `service.ready` / `service.stopped`, and is stopped by Tamp.Core's end-of-build backstop if a target doesn't dispose it: a server started in one target can serve later targets but never outlives the build. `SpfxDevServer` still owns no process state of its own; disposing it (once or several times, through either layer) stops the server once.
- The rest of the Tamp family (`Tamp.Sarif`, and the build script's `Tamp.NetCli.V10`, `Tamp.Components`, `Tamp.Components.NetCli.V10`) moves to 1.18.0 with `Tamp.Core`.
- `SpfxDebugUrl.Build` is unchanged and still matches `@microsoft/spfx-heft-plugins` 1.24.1 character for character.

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
