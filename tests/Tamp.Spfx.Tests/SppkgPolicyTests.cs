using Xunit;

namespace Tamp.Spfx.Tests;

public sealed class SppkgPolicyTests
{
    private static readonly SppkgInspection Probe = SppkgInspector.Inspect(Fixture.Sppkg);

    private static readonly SppkgPolicy Permissive = new()
    {
        AllowedPermissions = new[] { new SpfxPermissionRequest("Microsoft Graph", "Sites.Read.All"), new SpfxPermissionRequest("Contoso API", "user_impersonation") },
        AllowedScriptHosts = new[] { "code.jquery.com" },
        AllowCustomScript = true,
    };

    private static IEnumerable<SppkgFinding> Errors(SppkgPolicyResult r) => r.Findings.Where(f => f.Severity == SppkgSeverity.Error);

    [Fact]
    public void The_Default_Policy_Stops_The_Probe_For_Every_Unreviewed_Risk()
    {
        var r = new SppkgPolicy().Evaluate(Probe);

        Assert.False(r.Passed);
        Assert.Equal(
            new[] { SppkgRules.Permission, SppkgRules.Permission, SppkgRules.CustomScript, SppkgRules.ExternalScript }.OrderBy(x => x),
            Errors(r).Select(f => f.RuleId).OrderBy(x => x));
        Assert.Contains(r.Findings, f => f.RuleId == SppkgRules.TenantWideExtension && f.Severity == SppkgSeverity.Warning && f.ComponentId == Fixture.ExtensionId);
    }

    [Fact]
    public void Allow_Listing_Everything_Passes_And_Leaves_Notes_As_Evidence()
    {
        var r = Permissive.Evaluate(Probe);

        Assert.True(r.Passed);
        Assert.Equal(2, r.Findings.Count(f => f.RuleId == SppkgRules.Permission && f.Severity == SppkgSeverity.Note));
        Assert.Contains(r.Findings, f => f.RuleId == SppkgRules.ExternalScript && f.Severity == SppkgSeverity.Note);
        Assert.Contains(r.Findings, f => f.RuleId == SppkgRules.CustomScript && f.Severity == SppkgSeverity.Note);
        Assert.Same(r, r.ThrowIfFailed());
    }

    [Fact]
    public void ThrowIfFailed_Lists_Every_Error()
    {
        var ex = Assert.Throws<SppkgPolicyException>(() => new SppkgPolicy().Evaluate(Probe).ThrowIfFailed());
        Assert.Contains("spfx-probe.sppkg failed the deploy policy", ex.Message);
        Assert.Contains("Microsoft Graph / Sites.Read.All", ex.Message);
        Assert.Contains("code.jquery.com", ex.Message);
        Assert.Equal(4, ex.Result.Errors.Count());
    }

    [Fact]
    public void AllowAnyPermission_Skips_The_Allow_List()
    {
        var r = (Permissive with { AllowedPermissions = Array.Empty<SpfxPermissionRequest>(), AllowAnyPermission = true }).Evaluate(Probe);
        Assert.True(r.Passed);
    }

    [Fact]
    public void Tenant_Wide_Can_Be_Forbidden_And_Its_Extensions_Escalated()
    {
        var r = (Permissive with { AllowTenantWideDeployment = false, TenantWideExtensionSeverity = SppkgSeverity.Error }).Evaluate(Probe);
        Assert.Equal(new[] { SppkgRules.TenantWide, SppkgRules.TenantWideExtension }, Errors(r).Select(f => f.RuleId).OrderBy(x => x));
    }

    [Fact]
    public void Expected_Version_Id_And_Size_Are_Checked()
    {
        Assert.True((Permissive with { ExpectedVersion = SpfxSolutionVersion.Parse("1.2.3.4"), ExpectedSolutionId = "{" + Fixture.SolutionId.ToUpperInvariant() + "}" }).Evaluate(Probe).Passed);

        var r = (Permissive with { ExpectedVersion = SpfxSolutionVersion.Parse("1.2.3.5"), ExpectedSolutionId = Guid.NewGuid().ToString(), MaxPackageBytes = 1000 }).Evaluate(Probe);
        Assert.Equal(new[] { SppkgRules.Version, SppkgRules.Identity, SppkgRules.Size }.OrderBy(x => x), Errors(r).Select(f => f.RuleId).OrderBy(x => x));
        Assert.Contains("stale build", Errors(r).Single(f => f.RuleId == SppkgRules.Version).Message);
    }

    [Fact]
    public void Script_Hosts_Wildcards_And_Plain_Http()
    {
        static SppkgInspection WithScripts(params string[] urls) => Probe with
        {
            Components = new[] { Probe.Components[0] with { RequiresCustomScript = false, ExternalScripts = urls.Select(u => new SppkgExternalScript("m", u)).ToList() } },
        };
        var policy = Permissive with { AllowedScriptHosts = new[] { "*.contoso.com", "code.jquery.com" } };

        Assert.True(policy.Evaluate(WithScripts("https://cdn.contoso.com/a.js", "//static.contoso.com/b.js", "https://code.jquery.com/x.js")).Passed);

        var r = policy.Evaluate(WithScripts("https://contoso.com.evil.io/a.js", "http://cdn.contoso.com/a.js", "https://code.jquery.com.evil.io/x.js", "https://not a url"));
        var messages = Errors(r).Select(f => f.Message).ToList();
        Assert.Equal(4, messages.Count);
        Assert.Contains(messages, m => m.Contains("contoso.com.evil.io is not allow-listed"));
        Assert.Contains(messages, m => m.Contains("plain http"));
        Assert.Contains(messages, m => m.Contains("not a valid URL"));

        Assert.True((policy with { AllowInsecureScriptUrls = true }).Evaluate(WithScripts("http://cdn.contoso.com/a.js")).Passed);
    }

    [Fact]
    public void Read_Problems_Unsafe_Entries_And_A_Malformed_Version_Fail()
    {
        var bad = Probe with
        {
            ReadProblems = new[] { "f/x.xml: boom" },
            UnsafeEntries = new[] { "../x" },
            App = Probe.App with { Version = "1.0" },
        };
        var r = Permissive.Evaluate(bad);
        Assert.Equal(new[] { SppkgRules.Unreadable, SppkgRules.UnsafeEntry, SppkgRules.Version }.OrderBy(x => x), Errors(r).Select(f => f.RuleId).OrderBy(x => x));
    }

    // ---- against the deployed release ----

    [Fact]
    public void The_Same_Version_Again_Is_Not_An_Upgrade()
    {
        var r = Permissive.Evaluate(Probe, previous: Probe);
        Assert.Equal(SppkgRules.VersionNotIncreased, Assert.Single(Errors(r)).RuleId);
    }

    [Fact]
    public void Drift_Since_The_Deployed_Release_Is_Surfaced()
    {
        var previous = Probe with
        {
            App = Probe.App with
            {
                Version = "1.2.3.3",
                SkipFeatureDeployment = false,
                WebApiPermissionRequests = new[] { new SppkgPermissionRequest("microsoft graph", "sites.read.all") },
            },
            ExternalScriptUrls = Array.Empty<string>(),
            Components = new[] { Probe.Components[0], new SppkgComponent { Id = "gone", Alias = "Old" } },
        };
        var current = Probe with { Components = new[] { Probe.Components[0], Probe.Components[1] } };

        var r = Permissive.Evaluate(current, previous);

        Assert.True(r.Passed);
        var drift = r.Findings.Where(f => f.RuleId.StartsWith("SPFX02", StringComparison.Ordinal)).ToList();
        Assert.Equal(SppkgSeverity.Warning, drift.Single(f => f.RuleId == SppkgRules.NewPermission).Severity);
        Assert.Contains("Contoso API", drift.Single(f => f.RuleId == SppkgRules.NewPermission).Message);   // Graph matched case-insensitively
        Assert.Single(drift, f => f.RuleId == SppkgRules.NewTenantWide);
        Assert.Single(drift, f => f.RuleId == SppkgRules.NewExternalScript);
        Assert.Equal("gone", drift.Single(f => f.RuleId == SppkgRules.ComponentRemoved).ComponentId);
        Assert.Contains(drift, f => f.RuleId == SppkgRules.ComponentAdded);
        Assert.DoesNotContain(drift, f => f.RuleId == SppkgRules.VersionNotIncreased);
    }

    [Fact]
    public void New_Permissions_Can_Require_A_Human()
    {
        var previous = Probe with { App = Probe.App with { Version = "1.0.0.0", WebApiPermissionRequests = Array.Empty<SppkgPermissionRequest>() } };
        var r = (Permissive with { NewPermissionSeverity = SppkgSeverity.Error }).Evaluate(Probe, previous);
        Assert.Equal(2, Errors(r).Count(f => f.RuleId == SppkgRules.NewPermission));
    }

    [Fact]
    public void Findings_Carry_Stable_Subjects()
    {
        var r = new SppkgPolicy().Evaluate(Probe);
        Assert.Contains(r.Findings, f => f.Subject == "permission:Microsoft Graph / Sites.Read.All");
        Assert.Contains(r.Findings, f => f.Subject == "script:https://code.jquery.com/jquery-3.7.1.min.js");
        Assert.Contains(r.Findings, f => f.Subject == "custom-script:" + Fixture.WebPartId);
        Assert.All(r.Findings, f => Assert.False(string.IsNullOrEmpty(f.Subject)));
    }

    [Fact]
    public void Null_Inspection_Throws() => Assert.Throws<ArgumentNullException>(() => new SppkgPolicy().Evaluate(null!));
}
