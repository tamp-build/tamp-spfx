using Tamp;
using Tamp.Spfx;
using Xunit;

namespace Tamp.Spfx.Tests;

/// <summary>
/// #9 part 1 — every adopter hand-wrote the same graph, which made the gate's position an adopter
/// decision. tamp#151 is the demonstration that the position is exactly where this goes wrong: a gate
/// upstream of evidence publication suppresses the evidence in the runs where findings exist.
/// </summary>
/// <remarks>
/// The wiring is the deliverable here, so it is asserted rather than left implicit — the same way
/// core's security pipeline pins its own staging.
/// </remarks>
public class SpfxPipelineBuildTests
{
    private sealed class TestBuild : SpfxPipelineBuild
    {
        private readonly string _web;
        private readonly SppkgPolicy? _policy;
        private readonly bool _requireNode;

        public TestBuild(string web = "web", SppkgPolicy? policy = null, bool requireNode = false)
        {
            _web = web;
            _policy = policy;
            _requireNode = requireNode;
        }

        protected override string SpfxWebDirectory => _web;
        protected override SppkgPolicy? SpfxDeployPolicy => _policy;
        protected override bool SpfxRequireSupportedNode => _requireNode;

        public Target T_Preflight => SpfxPreflight;
        public Target T_Restore => SpfxRestore;
        public Target T_Stamp => SpfxStamp;
        public Target T_Test => SpfxTest;
        public Target T_Package => SpfxPackage;
        public Target T_Collect => SpfxCollect;
        public Target T_Publish => SpfxPublish;
        public Target T_Gate => SpfxGate;
        public Target T_All => SpfxAll;

        public string WebDirectory => SpfxWebDirectory;
        public bool ProductionPackage => SpfxProductionPackage;
        public bool RequireSupportedNode => SpfxRequireSupportedNode;
        public string? VersionSource => SpfxSolutionVersionSource;
        public SppkgPolicy? Policy => SpfxDeployPolicy;
        public SpfxNodeSupport NodeCompatibility => SpfxNodeCompatibility;
        public AbsolutePath SarifFile => SpfxSppkgSarifFile;
        public CommandPlan RestorePlan() => SpfxRestorePlan();
    }

    private static RecordingDef Materialize(Target target)
    {
        var definition = new RecordingDef();
        target(definition);
        return definition;
    }

    // ─── the ordering, which is the whole point ───────────────────────────

    [Fact]
    public void The_Chain_Is_Preflight_Collect_Publish_Enforce()
        => Assert.Equal(
            ["SpfxPreflight", "SpfxCollect", "SpfxPublish", "SpfxGate"],
            Materialize(new TestBuild().T_All).RecordedDependencies);

    [Fact]
    public void Publication_Depends_On_The_Collect_Stage_And_The_Gate_On_Publication()
    {
        // The edge that matters: nothing between collect and publish can be made conditional on a
        // finding, and the gate cannot run before the evidence is written.
        Assert.Equal(["SpfxCollect"], Materialize(new TestBuild().T_Publish).RecordedDependencies);
        Assert.Equal(["SpfxPublish"], Materialize(new TestBuild().T_Gate).RecordedDependencies);
    }

    [Fact]
    public void Preflight_Runs_Before_Anything_Builds()
    {
        // The Node check's only value is replacing a confusing failure inside Heft with a named one,
        // which requires it to run first. Restore and stamp both wait for it.
        Assert.Equal(["SpfxPreflight"], Materialize(new TestBuild().T_Restore).RecordedDependencies);
        Assert.Equal(["SpfxPreflight"], Materialize(new TestBuild().T_Stamp).RecordedDependencies);
        Assert.Empty(Materialize(new TestBuild().T_Preflight).RecordedDependencies);
    }

    [Fact]
    public void Test_Waits_For_Restore_And_Stamp_And_Package_For_Test()
    {
        // heft package-solution does not rebuild, so packaging before a successful test would ship
        // stale bundles.
        Assert.Equal(["SpfxRestore", "SpfxStamp"], Materialize(new TestBuild().T_Test).RecordedDependencies);
        Assert.Equal(["SpfxTest"], Materialize(new TestBuild().T_Package).RecordedDependencies);
        Assert.Equal(["SpfxPackage"], Materialize(new TestBuild().T_Collect).RecordedDependencies);
    }

    [Fact]
    public void Every_Stage_From_Collect_Onward_Survives_An_Upstream_Failure()
    {
        // Without this, a failed test suppresses the .sppkg inspection and its SARIF — losing the
        // evidence on exactly the runs that needed it. This is the property, so it is asserted.
        var build = new TestBuild();
        foreach (var (name, target) in new (string, Target)[]
        {
            ("SpfxRestore", build.T_Restore),
            ("SpfxStamp", build.T_Stamp),
            ("SpfxTest", build.T_Test),
            ("SpfxPackage", build.T_Package),
            ("SpfxCollect", build.T_Collect),
            ("SpfxPublish", build.T_Publish),
            ("SpfxGate", build.T_Gate),
        })
        {
            Assert.True(Materialize(target).RecordedAssuredAfterFailure,
                $"{name} must be assured-after-failure so evidence survives an upstream failure");
        }
    }

    [Fact]
    public void Every_Target_Has_A_Description()
    {
        var build = new TestBuild();
        foreach (var target in new[]
        {
            build.T_Preflight, build.T_Restore, build.T_Stamp, build.T_Test,
            build.T_Package, build.T_Collect, build.T_Publish, build.T_Gate, build.T_All,
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(Materialize(target).RecordedDescription));
        }
    }

    // ─── the seams ────────────────────────────────────────────────────────

    [Fact]
    public void Policy_And_Version_Source_Default_To_Nothing()
    {
        // Both belong to the adopter. Inventing a version would make the shipped version a surprise,
        // and inventing a policy would enforce an opinion core deliberately does not hold.
        var build = new TestBuild();

        Assert.Null(build.Policy);
        Assert.Null(build.VersionSource);
    }

    [Fact]
    public void An_Unsupported_Node_Warns_Rather_Than_Fails_By_Default()
    {
        // An unsupported-but-working pair is a real situation during an upgrade, and a hard failure
        // on a newly released Node major would break builds that are fine.
        Assert.False(new TestBuild().RequireSupportedNode);
    }

    [Fact]
    public void Packaging_Is_Production_By_Default()
        => Assert.True(new TestBuild().ProductionPackage);

    [Fact]
    public void The_Restore_Plan_Is_Npm_Ci_In_The_Web_Directory()
    {
        var plan = new TestBuild("my-web").RestorePlan();

        Assert.Equal("npm", plan.Executable);
        Assert.Equal(["ci"], plan.Arguments);
        Assert.Equal("my-web", plan.WorkingDirectory);
    }

    [Fact]
    public void The_Bundled_Node_Data_Is_The_Default()
        => Assert.Same(SpfxNodeSupport.Bundled, new TestBuild().NodeCompatibility);

    [Fact]
    public void The_Sarif_Lands_Under_The_Artifacts_Directory()
        => Assert.EndsWith(Path.Combine("artifacts", "spfx", "sppkg.sarif"), new TestBuild().SarifFile.Value, StringComparison.Ordinal);
}
