using Xunit;

namespace Tamp.Spfx.Tests;

public sealed class SpfxProjectTests
{
    [Fact]
    public void Reads_The_Probe_Project()
    {
        var p = SpfxProject.Load(Fixture.Project);

        Assert.Equal("spfx-probe", p.PackageName);
        Assert.Equal("1.24.0", p.SpfxVersion);
        Assert.Equal(SpfxToolchain.Heft, p.Toolchain);
        Assert.Equal("spfx-probe-client-side-solution", p.Solution.Name);
        Assert.Equal(Fixture.SolutionId, p.Solution.Id);
        Assert.Equal("1.2.3.4", p.Solution.Version);
        Assert.True(p.Solution.SkipFeatureDeployment);
        Assert.True(p.Solution.IncludeClientSideAssets);
        Assert.Equal(new[] { new SpfxPermissionRequest("Microsoft Graph", "Sites.Read.All"), new SpfxPermissionRequest("Contoso API", "user_impersonation") },
            p.Solution.WebApiPermissionRequests);
        Assert.Equal(2, p.Solution.Features.Count);
        Assert.Equal("https://code.jquery.com/jquery-3.7.1.min.js", p.Externals["jquery"]);
    }

    [Fact]
    public void Knows_Where_The_Rig_Writes_Outputs()
    {
        var p = SpfxProject.Load(Fixture.Project);
        var root = Path.GetFullPath(Fixture.Project);

        Assert.Equal(Path.Combine(root, "sharepoint", "solution", "spfx-probe.sppkg"), p.Outputs.Sppkg);
        Assert.Equal(Path.Combine(root, "release", "analysis-logs", "lint.sarif"), p.Outputs.LintSarif);
        Assert.Equal(Path.Combine(root, "jest-output", "coverage", "cobertura", "cobertura-coverage.xml"), p.Outputs.CoberturaCoverage);
        Assert.Equal(Path.Combine(root, "jest-output", "coverage", "coverage-summary.json"), p.Outputs.CoverageSummary);
        Assert.Equal(Path.Combine(root, "jest-output", "JUnit.xml"), p.Outputs.JUnitResults);
        Assert.Equal(Path.Combine(root, "release", "component-dependency-audit"), p.Outputs.ComponentDependencyAudit);
        Assert.Equal(Path.Combine(root, "release", "manifests"), p.Outputs.Manifests);
        Assert.Equal(Path.Combine(root, "release", "assets"), p.Outputs.Assets);
        Assert.Equal(Path.Combine(root, "config", "package-solution.json"), p.PackageSolutionPath);
    }

    [Fact]
    public void Detects_A_Gulp_Project()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("config"));
        File.WriteAllText(t.File("package.json"), """{ "name": "old", "dependencies": { "@microsoft/sp-core-library": "~1.20.0" }, "devDependencies": { "@microsoft/sp-build-web": "1.20.2" } }""");
        File.WriteAllText(t.File(Path.Combine("config", "package-solution.json")), """{ "solution": { "name": "old", "version": "1.0.0.0" } }""");

        var p = SpfxProject.Load(t.Path);

        Assert.Equal(SpfxToolchain.Gulp, p.Toolchain);
        Assert.Equal("1.20.0", p.SpfxVersion);
        Assert.Equal(Path.Combine(t.Path, "sharepoint", "solution", "old.sppkg"), p.Outputs.Sppkg);
        Assert.Empty(p.Externals);
    }

    [Fact]
    public void Unknown_Toolchain_And_Object_Form_Externals()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("config"));
        File.WriteAllText(t.File("package.json"), """{ "name": "x" }""");
        File.WriteAllText(t.File(Path.Combine("config", "package-solution.json")), """{ "solution": { "version": "1.0.0.0" } }""");
        File.WriteAllText(t.File(Path.Combine("config", "config.json")), """
            // comment
            { "externals": { "a": "https://cdn.example.com/a.js", "b": { "path": "https://cdn.example.com/b.js", "globalName": "B" }, "c": { "globalName": "C" } } }
            """);

        var p = SpfxProject.Load(t.Path);

        Assert.Equal(SpfxToolchain.Unknown, p.Toolchain);
        Assert.Null(p.SpfxVersion);
        Assert.Equal(2, p.Externals.Count);
        Assert.Equal("https://cdn.example.com/b.js", p.Externals["b"]);
        Assert.Empty(p.Solution.WebApiPermissionRequests);
        Assert.False(p.Solution.SkipFeatureDeployment);
    }

    [Fact]
    public void Missing_Files_Are_Reported_By_Name()
    {
        using var t = new TempDir();
        var ex = Assert.Throws<FileNotFoundException>(() => SpfxProject.Load(t.Path));
        Assert.EndsWith("package.json", ex.FileName);

        File.WriteAllText(t.File("package.json"), "{}");
        ex = Assert.Throws<FileNotFoundException>(() => SpfxProject.Load(t.Path));
        Assert.EndsWith("package-solution.json", ex.FileName);
    }

    [Fact]
    public void Bad_Json_And_A_Missing_Solution_Object_Are_Reported()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("config"));
        File.WriteAllText(t.File("package.json"), "{ nope");
        File.WriteAllText(t.File(Path.Combine("config", "package-solution.json")), "{}");
        Assert.Throws<InvalidDataException>(() => SpfxProject.Load(t.Path));

        File.WriteAllText(t.File("package.json"), "{}");
        Assert.Throws<InvalidDataException>(() => SpfxProject.Load(t.Path));
    }

    [Fact]
    public void Requires_A_Directory() => Assert.Throws<ArgumentException>(() => SpfxProject.Load(" "));

    [Fact]
    public void Permission_Matching_Ignores_Case()
    {
        Assert.True(new SpfxPermissionRequest("Microsoft Graph", "Sites.Read.All").Matches(new("microsoft graph", "sites.read.all")));
        Assert.False(new SpfxPermissionRequest("Microsoft Graph", "Sites.Read.All").Matches(new("Microsoft Graph", "Sites.ReadWrite.All")));
        Assert.Equal("Microsoft Graph / Sites.Read.All", new SpfxPermissionRequest("Microsoft Graph", "Sites.Read.All").ToString());
    }
}
