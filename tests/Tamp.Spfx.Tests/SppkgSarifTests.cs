using Tamp.Sarif;
using Xunit;

namespace Tamp.Spfx.Tests;

public sealed class SppkgSarifTests
{
    private static readonly SppkgPolicyResult Result = new SppkgPolicy().Evaluate(SppkgInspector.Inspect(Fixture.Sppkg));

    [Fact]
    public void One_Result_Per_Finding_With_Mapped_Levels()
    {
        var run = Assert.Single(SppkgSarif.ToSarif(Result).Runs);

        Assert.Equal("Tamp.Spfx", run.Tool.Driver.Name);
        Assert.Equal(Result.Findings.Count, run.Results!.Count);
        Assert.Equal(Result.Errors.Count(), run.Results.Count(r => r.Level == SarifLevel.Error));
        Assert.Contains(run.Results, r => r.Level == SarifLevel.Warning && r.RuleId == SppkgRules.TenantWideExtension);
    }

    [Fact]
    public void Rules_Are_Described_Once_Each()
    {
        var run = SppkgSarif.ToSarif(Result).Runs[0];
        var ids = run.Tool.Driver.Rules!.Select(r => r.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.Equal(Result.Findings.Select(f => f.RuleId).Distinct().OrderBy(x => x, StringComparer.Ordinal), ids);
        Assert.All(run.Tool.Driver.Rules!, r => Assert.False(string.IsNullOrEmpty(r.ShortDescription?.Text)));
    }

    [Fact]
    public void Findings_Have_Logical_Locations_Not_Invented_Files()
    {
        var results = SppkgSarif.ToSarif(Result).Runs[0].Results!;
        Assert.All(results, r =>
        {
            var loc = Assert.Single(r.Locations!);
            Assert.Null(loc.PhysicalLocation);
            var logical = Assert.Single(loc.LogicalLocations!);
            Assert.StartsWith($"sppkg://{Fixture.SolutionId}/", logical.FullyQualifiedName);
        });
        Assert.Contains(results, r => r.Locations![0].LogicalLocations![0].FullyQualifiedName ==
            $"sppkg://{Fixture.SolutionId}/permission:Microsoft Graph / Sites.Read.All");
    }

    [Fact]
    public void Dedup_Keeps_Distinct_Findings_Of_The_Same_Rule()
    {
        // Two permission requests share a rule id; their logical locations keep them apart through SarifDedup.
        var log = SppkgSarif.ToSarif(Result);
        Assert.Equal(log.Runs[0].Results!.Count, SarifDedup.Distinct(log).Runs[0].Results!.Count);
    }

    [Fact]
    public void Writes_A_File_That_Reads_Back()
    {
        using var t = new TempDir();
        var path = t.File(Path.Combine("security", "sppkg.sarif"));
        SppkgSarif.WriteSarif(Result, path);

        var back = SarifReader.Parse(File.ReadAllText(path));
        Assert.Equal(Result.Findings.Count, back.Runs[0].Results!.Count);
    }

    [Fact]
    public void An_Unknown_Rule_Still_Gets_A_Rule_Entry()
    {
        var r = new SppkgPolicyResult(Result.Inspection, new[] { new SppkgFinding("SPFX999", SppkgSeverity.Note, "custom") });
        var rule = Assert.Single(SppkgSarif.ToSarif(r).Runs[0].Tool.Driver.Rules!);
        Assert.Equal("SPFX999", rule.Id);
        Assert.Equal("solution", SppkgSarif.ToSarif(r).Runs[0].Results![0].Locations![0].LogicalLocations![0].Name);
    }

    [Fact]
    public void Null_Result_Throws() => Assert.Throws<ArgumentNullException>(() => SppkgSarif.ToSarif(null!));
}
