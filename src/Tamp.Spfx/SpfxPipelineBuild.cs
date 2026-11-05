using Tamp.Heft.V1;

namespace Tamp.Spfx;

/// <summary>
/// One-import base class carrying the SPFx build graph, in the order it has to run:
/// <b>preflight → collect → publish → enforce</b>. Adopters inherit, override
/// <see cref="SpfxWebDirectory"/>, and run <c>tamp SpfxAll</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the order ships rather than being documented.</b> Every SPFx build re-derived
/// <c>Restore → Stamp → Test → Package → Gate</c>, which made the gate's position an adopter
/// decision — and that is exactly where it goes wrong. tamp#151 is the demonstration: a gate placed
/// upstream of evidence publication suppresses the evidence <i>in the runs where findings exist</i>,
/// so evidence arrives reliably when there is nothing to report and unreliably when there is. Core
/// fixed that for the security chain by shipping the order; this is the same hazard in the SPFx chain.
/// </para>
/// <para>
/// The property that matters: <b>publication is never contingent on what the gate is about to
/// decide.</b> <see cref="SpfxPublish"/> and <see cref="SpfxGate"/> are both
/// <c>AssuredAfterFailure()</c>, so the <c>.sppkg</c> inspection and its SARIF are written even when
/// a test failed or the policy is about to fail the build.
/// </para>
/// <para>
/// <b>Preflight comes first on purpose.</b> The Node compatibility check only pays for itself if it
/// runs before anything builds: its whole value is replacing a confusing failure inside Heft with a
/// named one. See <see cref="SpfxPreflight"/>.
/// </para>
/// <para>
/// Every target is <see langword="virtual"/>. The seams meant to be filled are
/// <see cref="SpfxWebDirectory"/> (required), <see cref="SpfxSolutionVersionSource"/>,
/// <see cref="SpfxDeployPolicy"/>, <see cref="SpfxProductionPackage"/> and
/// <see cref="SpfxRequireSupportedNode"/>.
/// </para>
/// </remarks>
public abstract class SpfxPipelineBuild : TampBuild
{
    // ------------------------------------------------------------------
    // Required
    // ------------------------------------------------------------------

    /// <summary>The SPFx project directory — the one holding <c>package.json</c>.</summary>
    protected abstract string SpfxWebDirectory { get; }

    // ------------------------------------------------------------------
    // Seams
    // ------------------------------------------------------------------

    /// <summary>
    /// The version to stamp into <c>package-solution.json</c>, or null to leave it as committed.
    /// Default null: a version source is a per-project decision (a parameter, a git describe, a CI
    /// build number), and silently inventing one would make the shipped version a surprise.
    /// </summary>
    protected virtual string? SpfxSolutionVersionSource => null;

    /// <summary>Package with <c>--production</c>. Default true — a release package is the default intent.</summary>
    protected virtual bool SpfxProductionPackage => true;

    /// <summary>
    /// The deploy policy the gate enforces, or null for none. Default null: policy belongs to the
    /// adopter, exactly as it does in core's security pipeline.
    /// </summary>
    protected virtual SppkgPolicy? SpfxDeployPolicy => null;

    /// <summary>
    /// Fail <see cref="SpfxPreflight"/> when the running Node major is not one the project's SPFx
    /// version supports. <b>Default false</b> — warn, do not fail.
    /// </summary>
    /// <remarks>
    /// An unsupported-but-working pair is a real situation during an upgrade, and a hard failure on a
    /// newly released Node major would break builds that are fine. Turning this on is the adopter's
    /// call, consistent with how the deploy policy is handled.
    /// <para>
    /// Note what it does <b>not</b> do: an <see cref="SpfxNodeVerdict.Unknown"/> verdict never fails
    /// the build regardless, because "nothing was checked" is not evidence of a problem. It is still
    /// logged as prominently as a failure would be, since it is not evidence of the absence of one
    /// either.
    /// </para>
    /// </remarks>
    protected virtual bool SpfxRequireSupportedNode => false;

    /// <summary>
    /// Compatibility data for the Node check. Default is the table shipped with this package; point
    /// it at <see cref="SpfxNodeSupport.LoadFrom"/> to teach a build about an SPFx release newer than
    /// this package knows.
    /// </summary>
    protected virtual SpfxNodeSupport SpfxNodeCompatibility => SpfxNodeSupport.Bundled;

    /// <summary>Where the pipeline writes its evidence. Default <c>{RootDirectory}/artifacts/spfx</c>.</summary>
    protected virtual AbsolutePath SpfxArtifactsDir => RootDirectory / "artifacts" / "spfx";

    /// <summary>The SARIF describing the packaged <c>.sppkg</c>.</summary>
    protected virtual AbsolutePath SpfxSppkgSarifFile => SpfxArtifactsDir / "sppkg.sarif";

    // ------------------------------------------------------------------
    // State shared between stages
    // ------------------------------------------------------------------

    /// <summary>The loaded project. Read once in preflight so every later stage sees the same thing.</summary>
    private SpfxProject? _project;

    /// <summary>The project, loading it if a stage is run on its own.</summary>
    protected SpfxProject Project => _project ??= SpfxProject.Load(SpfxWebDirectory);

    /// <summary>The preflight Node verdict, for the gate's receipt.</summary>
    private SpfxNodeCheck? _nodeCheck;

    // ------------------------------------------------------------------
    // Preflight
    // ------------------------------------------------------------------

    /// <summary>
    /// Read the project and check the SPFx/Node pair <b>before anything builds</b>.
    /// </summary>
    /// <remarks>
    /// Microsoft supports a narrow Node window per SPFx version — one or two majors, moving every few
    /// releases — and an unsupported pair does not fail clearly: it fails inside Heft, or in a native
    /// module build, or produces a silently different bundle. Naming it here is the entire point, and
    /// it only works first.
    /// <para>
    /// The project's own <c>engines.node</c> is reported when it disagrees with the bundled table,
    /// because the rig declares its supported range there and that is newer than any transcription.
    /// </para>
    /// </remarks>
    protected virtual Target SpfxPreflight => _ => _
        .Description("Preflight: load the SPFx project and check the SPFx/Node pair before anything builds.")
        .Executes(() =>
        {
            var project = Project;
            Console.WriteLine($"[spfx] {project.PackageName ?? "(unnamed)"} — SPFx {project.SpfxVersion ?? "unknown"}, toolchain {project.Toolchain}");

            var node = SpfxResolveNodeVersion();
            var check = SpfxNodeCompatibility.Check(project, node);
            _nodeCheck = check;

            switch (check.Verdict)
            {
                case SpfxNodeVerdict.Supported:
                    Console.WriteLine($"[spfx] {check}");
                    break;

                case SpfxNodeVerdict.Unsupported when SpfxRequireSupportedNode:
                    throw new InvalidOperationException($"{check} Set SpfxRequireSupportedNode to false to downgrade this to a warning.");

                case SpfxNodeVerdict.Unsupported:
                    Console.WriteLine($"[spfx] WARNING: {check}");
                    Console.WriteLine("[spfx] Set SpfxRequireSupportedNode to true to make this fail the build instead.");
                    break;

                default:
                    // Never fails: "nothing was checked" is not evidence of a problem. Logged as
                    // loudly as a failure, because it is not evidence of the absence of one either.
                    Console.WriteLine($"[spfx] WARNING: {check}");
                    break;
            }
        });

    /// <summary>
    /// The running Node version, as <c>node --version</c> reports it. Override when Node is resolved
    /// some other way — a version manager shim, a container, a pinned toolchain.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing when Node cannot be found, so a missing Node yields an
    /// <see cref="SpfxNodeVerdict.Unknown"/> verdict that says so, instead of an unrelated crash
    /// during what is supposed to be a diagnostic step.
    /// </remarks>
    protected virtual string? SpfxResolveNodeVersion()
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("node")
            {
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            return output.Length == 0 ? null : output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Collect
    // ------------------------------------------------------------------

    /// <summary><c>npm ci</c> in the web directory.</summary>
    protected virtual Target SpfxRestore => _ => _
        .Description("Collect: install the project's locked dependencies (npm ci).")
        .DependsOn(nameof(SpfxPreflight))
        .AssuredAfterFailure()
        .Executes(() => SpfxRestorePlan());

    /// <summary>
    /// The restore command. Default <c>npm ci</c> in the web directory.
    /// </summary>
    /// <remarks>
    /// Built as a plain <see cref="CommandPlan"/> rather than through a wrapper: this package drives
    /// Heft, not npm, and taking a dependency on another wrapper for one command would be a poor
    /// trade. Override to use <c>Tamp.Npm</c>'s <c>Npm.Ci(...)</c>, pnpm, or a workspace-aware
    /// install — the point of the pipeline is the ordering, not this command.
    /// </remarks>
    protected virtual CommandPlan SpfxRestorePlan() => new()
    {
        Executable = "npm",
        Arguments = ["ci"],
        WorkingDirectory = SpfxWebDirectory,
    };

    /// <summary>
    /// Stamp <see cref="SpfxSolutionVersionSource"/> into <c>package-solution.json</c>. A no-op when
    /// no version source is configured, so the committed version ships unchanged.
    /// </summary>
    protected virtual Target SpfxStamp => _ => _
        .Description("Collect: stamp the solution version into package-solution.json, when a version source is configured.")
        .DependsOn(nameof(SpfxPreflight))
        .AssuredAfterFailure()
        .Executes(() =>
        {
            if (SpfxSolutionVersionSource is not { Length: > 0 } version)
            {
                Console.WriteLine("[spfx] no SpfxSolutionVersionSource — leaving package-solution.json's version as committed.");
                return;
            }

            var solutionVersion = SpfxSolutionVersion.Parse(version);
            PackageSolutionEditor.SetVersion(Project.PackageSolutionPath, solutionVersion);
            Console.WriteLine($"[spfx] stamped solution version {solutionVersion} into {Project.PackageSolutionPath}");
        });

    /// <summary>Build and test through the rig.</summary>
    protected virtual Target SpfxTest => _ => _
        .Description("Collect: build and test the project through the Heft rig.")
        .DependsOn(nameof(SpfxRestore), nameof(SpfxStamp))
        .AssuredAfterFailure()
        .Executes(() => Spfx.Test(s => s.SetWorkingDirectory(SpfxWebDirectory).SetProduction(SpfxProductionPackage)));

    /// <summary>Produce the <c>.sppkg</c>.</summary>
    protected virtual Target SpfxPackage => _ => _
        .Description("Collect: package the solution into a .sppkg.")
        .DependsOn(nameof(SpfxTest))
        .AssuredAfterFailure()
        .Executes(() => Spfx.PackageSolution(s => s.SetWorkingDirectory(SpfxWebDirectory).SetProduction(SpfxProductionPackage)));

    /// <summary>The collect stage as a named target, so the publish stage is one edge in the graph.</summary>
    protected virtual Target SpfxCollect => _ => _
        .Description("Collect stage: restore, stamp, test and package. Produces evidence; enforces nothing.")
        .DependsOn(nameof(SpfxPackage))
        .AssuredAfterFailure()
        .Executes(() => { });

    // ------------------------------------------------------------------
    // Publish
    // ------------------------------------------------------------------

    /// <summary>
    /// Inspect the packaged <c>.sppkg</c> and write its SARIF — <b>unconditionally</b>.
    /// </summary>
    /// <remarks>
    /// Assured, so the evidence exists even when a test failed or the gate is about to fail the
    /// build. An adopter who put <see cref="SppkgPolicy"/> in a target upstream of their SARIF write
    /// lost the SARIF on exactly the runs that had a policy violation, and nothing in the package
    /// stopped them. This is what stops them.
    /// </remarks>
    protected virtual Target SpfxPublish => _ => _
        .Description($"Publish stage: inspect the .sppkg, evaluate the policy and write {nameof(SpfxSppkgSarifFile)}. Unconditional — never contingent on what the gate decides.")
        .DependsOn(nameof(SpfxCollect))
        .AssuredAfterFailure()
        .Executes(() =>
        {
            var sppkg = Project.Outputs.Sppkg;
            if (!File.Exists(sppkg))
            {
                // Not a throw: the collect stage already failed and said why, and a second failure
                // here would bury it. Said plainly so an absent SARIF has exactly one meaning.
                Console.WriteLine($"[spfx] no .sppkg at {sppkg} — packaging did not complete, so there is nothing to inspect and no SARIF is written.");
                return;
            }

            SpfxArtifactsDir.CreateDirectory();

            // The policy is EVALUATED here, not enforced: SppkgSarif describes a policy result, so
            // producing the evidence requires running the policy. With none configured, the default
            // SppkgPolicy still yields an inspection-describing SARIF. Whether any finding fails the
            // build is SpfxGate's decision, which is the whole point of the split.
            var inspection = SppkgInspector.Inspect(sppkg);
            _policyResult = (SpfxDeployPolicy ?? new SppkgPolicy()).Evaluate(inspection);

            SppkgSarif.WriteSarif(_policyResult, SpfxSppkgSarifFile.Value);
            Console.WriteLine(
                $"[spfx] inspected {Path.GetFileName(sppkg)}: {inspection.Components.Count} component(s), "
                + $"{_policyResult.Findings.Count} finding(s) -> {SpfxSppkgSarifFile.Value}");
        });

    /// <summary>The evaluated policy result, produced in publish and judged in enforce.</summary>
    private SppkgPolicyResult? _policyResult;

    // ------------------------------------------------------------------
    // Enforce
    // ------------------------------------------------------------------

    /// <summary>
    /// Apply <see cref="SpfxDeployPolicy"/> to the inspected package. Runs <b>after</b> publication,
    /// so failing it costs no evidence.
    /// </summary>
    /// <remarks>
    /// No policy is built in. A deploy policy is the adopter's, exactly as a severity threshold is in
    /// core's security chain; what ships here is the guarantee that enforcing one cannot suppress the
    /// evidence that explains the decision.
    /// </remarks>
    protected virtual Target SpfxGate => _ => _
        .Description("Enforce stage: judge the policy result produced in publish. Runs after publication, so failing it costs no evidence.")
        .DependsOn(nameof(SpfxPublish))
        .AssuredAfterFailure()
        .Executes(() =>
        {
            if (_nodeCheck is { Verdict: SpfxNodeVerdict.Unsupported } unsupported)
                Console.WriteLine($"[spfx] note: this package was built on an unsupported Node — {unsupported}");

            if (_policyResult is not { } result)
                throw new InvalidOperationException(
                    "There is no policy result to judge, because no .sppkg was produced — the collect stage "
                    + "failed and its error is above. Refusing to report a policy pass for a package that was "
                    + "never built.");

            if (SpfxDeployPolicy is null)
            {
                Console.WriteLine(
                    $"[spfx] no SpfxDeployPolicy configured — nothing enforced. The inspection SARIF was still "
                    + $"written ({result.Findings.Count} finding(s) against the default policy).");
                return;
            }

            if (result.Passed)
            {
                Console.WriteLine($"[spfx] deploy policy satisfied ({result.Inspection.Components.Count} component(s), {result.Findings.Count} finding(s), none blocking).");
                return;
            }

            foreach (var finding in result.Findings)
                Console.WriteLine($"[spfx] {finding.Severity}: {finding.RuleId} — {finding.Message}");

            Console.WriteLine(
                $"[spfx] the .sppkg and {SpfxSppkgSarifFile.Value} were written before this check, so the evidence "
                + "for every finding is on disk.");

            // The library's own exception type, so an adopter catching SppkgPolicyException around a
            // custom flow keeps working and gets the result object rather than a string.
            throw new SppkgPolicyException(result);
        });

    /// <summary>
    /// End-to-end SPFx chain: <b>preflight</b> (project + Node check) → <b>collect</b> (restore,
    /// stamp, test, package) → <b>publish</b> (inspect + SARIF, unconditional) → <b>enforce</b>
    /// (deploy policy, last).
    /// </summary>
    /// <remarks>
    /// Named <c>SpfxAll</c> rather than <c>Spfx</c> because a member called <c>Spfx</c> on this type
    /// would shadow the <see cref="Spfx"/> facade class for every other member in it — C# resolves
    /// the member first, and <c>Spfx.Test(...)</c> would stop compiling.
    /// </remarks>
    protected virtual Target SpfxAll => _ => _
        .Description("End-to-end SPFx chain: preflight -> collect -> publish -> enforce. Publication never depends on what the gate decides.")
        .DependsOn(nameof(SpfxPreflight), nameof(SpfxCollect), nameof(SpfxPublish), nameof(SpfxGate));
}
