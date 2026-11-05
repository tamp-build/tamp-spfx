# Tamp.Spfx

> Tamp build support for **SharePoint Framework 1.22+** (the Heft toolchain). It has typed build / test / package verbs, a model of the project's configuration, solution version stamping that doesn't reformat the file, and a **`.sppkg` inspector with a deploy policy gate**. The gate is the check a human reviewer would otherwise do before deploying: it flags API permission requests, tenant-wide extensions, scripts loaded from outside the package, and components that need custom script. It writes its results as JSON evidence and as SARIF.

| Package | Status |
|---|---|
| `Tamp.Spfx` | 0.3.0 |

```bash
dotnet add package Tamp.Spfx
```

Multi-targets net8 / net9 / net10. Builds on [`Tamp.Heft.V1`](https://github.com/tamp-build/tamp-heft). Verified against SPFx **1.24.0** (`@microsoft/spfx-web-build-rig` 1.24.0, Heft 1.3.2). The new `@microsoft/spfx-cli` scaffolder is deliberately *not* wrapped here; it gets its own satellite.

## A machine-controlled pipeline

```csharp
using Tamp;
using Tamp.Heft.V1;   // the fluent setters (SetWorkingDirectory, SetClean, …) live here
using Tamp.Npm.V10;
using Tamp.Spfx;

AbsolutePath Web => RootDirectory / "web";
SpfxProject Spfx_ => SpfxProject.Load(Web);
[Parameter] readonly string Version = "1.0.0";          // e.g. from GitVersion
[Parameter] readonly int BuildNumber = 0;               // monotonically increasing

Target Restore => _ => _.Executes(() => Npm.Ci(s => s.SetWorkingDirectory(Web)));

Target Stamp => _ => _.Executes(() =>
    PackageSolutionEditor.SetVersion(Spfx_, SpfxSolutionVersion.FromSemVer(Version, BuildNumber)));

// npm run build == heft test --clean --production && heft package-solution --production
Target Test => _ => _.DependsOn(nameof(Restore), nameof(Stamp))
    .Executes(() => Spfx.Test(s => s.SetWorkingDirectory(Web).SetClean()));

Target Package => _ => _.DependsOn(nameof(Test))
    .Executes(() => Spfx.PackageSolution(s => s.SetWorkingDirectory(Web)));

Target Gate => _ => _.DependsOn(nameof(Package)).Executes(() =>
{
    var inspection = SppkgInspector.Inspect(Spfx_.Outputs.Sppkg);
    inspection.WriteJson(ArtifactsDirectory / "spfx" / "sppkg-inspection.json");   // keep it: next release diffs against it

    var previous = File.Exists(LastDeployed) ? SppkgInspection.ReadJson(LastDeployed) : null;
    var result = new SppkgPolicy
    {
        ExpectedVersion = SpfxSolutionVersion.FromSemVer(Version, BuildNumber),
        ExpectedSolutionId = Spfx_.Solution.Id,
        AllowedPermissions = [new("Microsoft Graph", "Sites.Read.All")],
        AllowedScriptHosts = ["cdn.contoso.com"],
    }.Evaluate(inspection, previous);

    SppkgSarif.WriteSarif(result, ArtifactsDirectory / "security" / "sppkg.sarif");
    result.ThrowIfFailed();
});
```

Run it once against the [test fixture's](tests/Tamp.Spfx.Tests/Fixtures/README.md) package and the gate stops on exactly what nobody approved:

```
  Note    SPFX010 requests Microsoft Graph / Sites.Read.All (allowed).
  Error   SPFX010 requests Contoso API / user_impersonation, which the policy does not allow.
  Warning SPFX014 extension 'ProbeBannerApplicationCustomizer' (ClientSideExtension.ApplicationCustomizer) is registered to run on every site when deployed tenant-wide.
  Error   SPFX012 component 'Hello ProbeWebPart' requires custom script to be enabled on the site.
  Note    SPFX013 component 'Hello ProbeWebPart' loads jquery from https://code.jquery.com/jquery-3.7.1.min.js (allowed host).
```

## Verbs

| Facade | Command line |
|---|---|
| `Spfx.Build` | `heft build --production` |
| `Spfx.Test` | `heft test --production` (writes JUnit and Cobertura under `jest-output/`) |
| `Spfx.PackageSolution` | `heft package-solution --production` (`SetPrettyPrintXml`, `SetNoSppkg`) |
| `Spfx.Clean` | `heft clean` |
| `Spfx.TrustDevCertificate` | `heft trust-dev-cert` |
| `Spfx.StartDevServerAsync` | `heft start`, managed (see [Dev server](#dev-server-and-debug-urls)) |

`--production` is on by default; `s.SetProduction(false)` gives a debug build. A gulp-based project (SPFx 1.21 and earlier) is refused with an explanation rather than handed to Heft.

> **`package-solution` does not rebuild.** It packages whatever the last build left behind, and exits 0 even after a failed build. Always run it after a successful `Test` or `Build` in the same mode, as the template's `npm run build` does. `SppkgPolicy.ExpectedVersion` catches the stale-package case when you stamp a version per build.

## Dev server and debug URLs

`Spfx.StartDevServerAsync` runs `heft start` through Tamp.Heft's [`HeftDevServer`](https://github.com/tamp-build/tamp-heft#dev-server). It waits until `temp/build/manifests.js` is served, reports the URL, and kills the process tree on dispose. It also returns the SharePoint debug-page URL for any `serve.json` serve configuration, so browser automation opens exactly the page `heft start` would.

```csharp
Target FormEvidence => _ => _
    .DependsOn(nameof(Restore))
    .Executes(async () =>
    {
        await using var server = await Spfx.StartDevServerAsync(s => s.SetWorkingDirectory(Web));
        // https://contoso.sharepoint.com/_layouts/15/SPListForm.aspx?debugManifestsFile=...&loadSPFX=true&componentId=...&PageType=6&...
        var editForm = server.DebugUrl("myForm_EditForm", tenantDomain: "contoso.sharepoint.com");
        await RunPlaywrightAsync(editForm);
    });
```

- **One URL per serve configuration.** `DebugUrl(name)` takes a key of `serveConfigurations`; omit it for `default`. `server.ConfigurationNames` lists them. `{tenantDomain}` comes from the argument, else `SPFX_SERVE_TENANT_DOMAIN`, as for the rig.
- **The rig's own rules.** The URL carries `debugManifestsFile`, `debug=true` and `noredir=true`. For extensions it adds `loadSPFX=true` plus `customActions`, `fieldCustomizers` or the Form Customizer's fields (`componentId`, `PageType`, `RootFolder`, `ID`, `ContentTypeID`, `properties`), and drops `debug=true`. The page URL's own query string is replaced, as the rig does. The tests compare against URLs computed by `@microsoft/spfx-heft-plugins` 1.24.1 itself (`tests/Tamp.Spfx.Tests/Fixtures/debug-urls`).
- **Two deliberate differences.** A configuration name that isn't in `serve.json` throws and lists the names; the rig silently falls back to `default`. A `{tenantDomain}` placeholder with no tenant domain also throws; the rig produces `https://{tenantdomain}/...`.
- **Built on demand, never stored.** The debug URL isn't written to the dev server's state file. `SpfxDebugUrl.Build(SpfxServeConfig.Load(dir), ...)` builds one without a running server.
- **Lifecycle.** The process is `HeftDevServer`'s, built on Tamp.Core's `ManagedProcess`. Inside a `tamp` run, Ctrl+C cancels the start along with the build, and a server still running when the build ends is stopped for you, so a server started in one target can serve later ones but never outlives the build. `server.StopReason` (`requested`, `signal`, `exited`, `timeout`) says why it stopped, matching the `service.stopped` event; `HasExited`, `ExitCode` and `WaitForExitAsync` are there too. Disposing is idempotent.
- **Trusting the dev certificate.** `Spfx.TrustDevCertificate` runs `heft trust-dev-cert` once per machine, so browsers accept `https://localhost:4321` without a warning. It prompts on Windows and macOS, so it isn't for headless CI.

## Project model: `SpfxProject.Load(dir)`

This reads `package.json`, `config/package-solution.json` and `config/config.json`, accepting the comments and trailing commas the SPFx generators write. It runs nothing. It exposes:

- the SPFx version, and the toolchain (Heft, gulp or unknown);
- the solution: name, id, version, `skipFeatureDeployment`, `includeClientSideAssets`, `webApiPermissionRequests` and features;
- the CDN externals;
- **`Outputs`**, where the 1.24 rig writes everything:

| Output | Path |
|---|---|
| `.sppkg` | `sharepoint/` + `paths.zippedPackage` |
| ESLint SARIF (from the rig's lint task, no extra setup) | `release/analysis-logs/lint.sarif` |
| Coverage (Cobertura) | `jest-output/coverage/cobertura/cobertura-coverage.xml` |
| Coverage summary | `jest-output/coverage/coverage-summary.json` |
| Test results (JUnit) | `jest-output/JUnit.xml` |
| Component dependency audit | `release/component-dependency-audit/` |

`isDomainIsolated` is read but not treated as a security property: the 1.24 packager ignores it (isolated web parts are retired) and it never reaches the package.

## Version stamping

- **`SpfxSolutionVersion`** is the 4-part `major.minor.build.revision` that SharePoint compares numerically.
- **`FromSemVer("1.4.2-rc.1", revision: 512)`** gives `1.4.2.512`. SharePoint has no place for pre-release or build metadata, so supply an increasing revision (a CI run number) so pre-releases still order correctly.
- **`PackageSolutionEditor.SetVersion`** rewrites only the version string literals (`solution.version` and, by default, every feature version). Comments, key order, indentation, line endings and a BOM are left untouched, and the call is idempotent.

## `.sppkg` inspection

`SppkgInspector.Inspect(path)` reads the package itself, which is the exact set of bytes you would upload:

- **The package:** its SHA-256 (bind evidence and attestations to this) and size.
- **`AppManifest.xml`:** version, product id, `SkipFeatureDeployment`, and every `WebApiPermissionRequest`.
- **Features.**
- **Each component, listed once** (the packager repeats components per feature):
  - type and alias;
  - whether it requires custom script (`ReturnIfCustomScriptDisabled="false"`);
  - its supported hosts;
  - the SPFx libraries it depends on;
  - **every script it loads from outside the package**: CDN externals, localized-resource URLs, and non-SharePoint module base URLs (a CDN-hosted bundle).
- **Extension registrations:** `CustomAction` (per site) and `ClientSideComponentInstance` (applied to every site on tenant-wide deploy).
- **Every `ClientSideAssets/` file**, with its digest.

The package is treated as untrusted input:

- nothing is extracted to disk;
- XML is parsed with DTDs prohibited and no resolver;
- entry names are never used as paths, and unsafe ones are reported;
- package size, entry count and each entry's *decompressed* size are capped, so a zip bomb fails the inspection instead of the build machine.

`ToJson` / `WriteJson` / `ReadJson` round-trip the inspection. Keep the deployed release's copy as evidence; the next release is diffed against it.

## The deploy policy

The rules and their severities under the default `SppkgPolicy`. *Allowed* entries become notes, so the evidence still records them.

| Rule | Checks | Default |
|---|---|---|
| SPFX001 | Part of the package could not be read | error |
| SPFX002 | Unsafe archive entry path | error |
| SPFX003 | Version malformed, or not `ExpectedVersion` | error |
| SPFX004 | Solution id not `ExpectedSolutionId` | error |
| SPFX005 | Over `MaxPackageBytes` | error |
| SPFX010 | API permission request not in `AllowedPermissions` | error (`AllowAnyPermission` to skip) |
| SPFX011 | Tenant-wide deployable, when `AllowTenantWideDeployment = false` | allowed by default, as in the templates |
| SPFX012 | Component requires custom script | error (`AllowCustomScript`) |
| SPFX013 | Script loaded from a host not in `AllowedScriptHosts` (`*.contoso.com` wildcards), or over plain http | error |
| SPFX014 | Extension that runs on every site when deployed tenant-wide | warning (`TenantWideExtensionSeverity`) |

Pass the previous release's inspection to `Evaluate` to also compare against what is deployed:

| Rule | Checks | Default |
|---|---|---|
| SPFX020 | Version not greater than the deployed one (SharePoint would not upgrade) | error |
| SPFX021 | Permission request the deployed release did not make | warning (`NewPermissionSeverity`) |
| SPFX022 | Solution became tenant-wide deployable | warning |
| SPFX023 | New external script origin | warning |
| SPFX024 | Component removed (pages using it will break) | warning |
| SPFX025 | Component added | note |

## SARIF

`SppkgSarif.ToSarif(result)` puts the findings in the Tamp security pipeline: `Tamp.Sarif` merge and dedup, then findings ingest. A finding is about the package, not a source file, so each result has a **logical location**, `sppkg://<solution id>/<subject>` (for example `…/permission:Microsoft Graph / Sites.Read.All` or `…/script:https://…`), instead of a made-up file path. The subjects stay the same from one release to the next, so a dashboard can follow one finding over time.

## The rest of the security chain

Everything else an SPFx repository needs already exists as a Tamp satellite:

| Concern | Satellite |
|---|---|
| Restore | `Tamp.Npm.V10` (`Npm.Ci`, `Npm.Audit`) |
| SBOM | `Tamp.Syft` (reads `package-lock.json`) |
| CVE scanning | `Tamp.Grype`, `Tamp.OsvScanner` |
| SAST | `Tamp.OpenGrep`, `Tamp.CodeQL`, `Tamp.SonarScanner` |
| Lint | the rig's own `lint.sarif` (above) |
| Secrets | `Tamp.Trufflehog` |

## Known upstream issue: Jest finds no tests on Windows

With SPFx 1.24.0 (Heft Jest plugin 2.0.19, Jest 30), `heft test` on **Windows** runs zero test suites, and coverage reports `NaN`. The same project on Linux finds and runs its tests. CI on `ubuntu-latest` is unaffected. This is in the rig, not in this package.

## One-import pipeline: `SpfxPipelineBuild`

Every SPFx build re-derived the same graph, which made the gate's position an adopter decision — and
that is exactly where it goes wrong. Inherit the graph instead:

```csharp
class Build : SpfxPipelineBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    protected override string SpfxWebDirectory => RootDirectory / "web";

    // Seams worth filling. All optional.
    [Parameter] readonly string? Version;
    protected override string? SpfxSolutionVersionSource => Version;
    protected override bool SpfxRequireSupportedNode => true;
    protected override SppkgPolicy? SpfxDeployPolicy => new()
    {
        AllowedPermissions = [new("Microsoft Graph", "Sites.Read.All")],
        AllowTenantWideDeployment = false,
    };
}
```

```bash
tamp SpfxAll     # preflight -> collect -> publish -> enforce
```

### The order is the deliverable

```
SpfxPreflight    load the project, check the SPFx/Node pair     <- before anything builds
      |
SpfxCollect      restore -> stamp -> test -> package
      |
SpfxPublish      inspect the .sppkg, evaluate, write the SARIF   <- unconditional
      |
SpfxGate         judge the result and fail on policy             <- last
```

**Publication is never contingent on what the gate is about to decide.** An adopter who put
`SppkgPolicy` in a target upstream of their SARIF write lost the SARIF on exactly the runs that had a
policy violation, and nothing in this package stopped them. Every stage from collect onward is
`AssuredAfterFailure()`, so the inspection and its SARIF are written even when a test failed or the
policy is about to fail the build. This is the same hazard core fixed for the security chain in
[tamp#151](https://github.com/tamp-build/tamp/issues/151), and the same remedy.

Note where the policy is *evaluated*: in **publish**, because the SARIF describes a policy result, so
producing the evidence requires running the policy. Only the verdict — whether a finding fails the
build — belongs to the gate. With no policy configured, the default still yields an
inspection-describing SARIF.

`SpfxAll` is spelled that way rather than `Spfx` because a member named `Spfx` on the build class
would shadow the `Spfx` facade for every other member in it.

## SPFx and Node compatibility

Microsoft supports a narrow Node window per SPFx version — one or two majors, moving every few
releases. Building on an unsupported Node **does not fail clearly**: it fails inside Heft, or in a
native-module build, or produces a silently different bundle.

`SpfxPreflight` names it before anything builds, which is the only time the check is worth anything:

```
[spfx] WARNING: SPFx 1.22.0 supports Node 22, but Node v18.19.0 is running. Building on an
unsupported Node does not fail clearly: expect an error inside Heft, a native-module build failure,
or a silently different bundle.
```

**Warn by default; `SpfxRequireSupportedNode` to fail.** An unsupported-but-working pair is a real
situation during an upgrade, and a hard failure on a newly released Node major would break builds
that are fine.

### Three outcomes, not two

| verdict | meaning |
|---|---|
| `Supported` | the running Node major is listed for this SPFx version |
| `Unsupported` | it is not — warned, or failed when `SpfxRequireSupportedNode` is set |
| `Unknown` | **nothing was checked** — the SPFx version could not be read, or is newer than the data |

`Unknown` is never reported as `Supported`, and never fails the build either: "nothing was checked"
is not evidence of a problem, and not evidence of its absence. It is logged as loudly as a failure.

### The table is data, not code

`spfx-node-compatibility.json` ships embedded, transcribed from
[Microsoft's compatibility reference](https://learn.microsoft.com/en-us/sharepoint/dev/spfx/compatibility)
with the date it was taken. A newer SPFx release can be taught to an existing build without waiting
for a `Tamp.Spfx` release:

```csharp
protected override SpfxNodeSupport SpfxNodeCompatibility => SpfxNodeSupport.LoadFrom("spfx-node.json");
```

Two details the table carries that a flattened version would lose: Microsoft's `v16.13+` qualifier is
a `minimumMinor`, so Node 16.9 is correctly rejected for SPFx 1.17; and where patches within one
minor differ — 1.8.0/1.8.1 allow Node 8 while 1.8.2 adds 10 — the minor entry holds the **narrower**
set, because claiming support Microsoft does not is the one direction this must never fail in.

The project's own `engines.node` is read as a cross-check and reported when it disagrees, since the
rig declares its supported range there and that is newer than any transcription. It is a textual read
of the majors a range enumerates, **not** semver satisfaction: an unbounded `>=22.14.0` allows 24 and
26 as well, so that form yields no opinion rather than a wrong one.

## Releasing

Tag `v<X.Y.Z>` once `Tamp.Heft.V1` at the pinned version is on NuGet. CI builds against a checkout of `tamp-heft` until then (`TampHeftMode=project`).

## License

MIT.
