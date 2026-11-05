using Tamp.Spfx;
using Xunit;

namespace Tamp.Spfx.Tests;

/// <summary>
/// #9 part 2 — nothing validated the SPFx/Node pair, so a project on an unsupported Node failed
/// inside Heft with an error that did not name the cause.
/// </summary>
/// <remarks>
/// The expected values here are transcribed from Microsoft's own compatibility reference, so these
/// tests also guard the bundled data file against a typo'd edit.
/// </remarks>
public class SpfxNodeSupportTests
{
    private static readonly SpfxNodeSupport Data = SpfxNodeSupport.Bundled;

    // ─── the table, against Microsoft's published rows ────────────────────

    [Theory]
    [InlineData("1.24.1", "v26")]
    [InlineData("1.24.1", "v24")]
    [InlineData("1.24.0", "v22.14.0")]
    [InlineData("1.23.2", "v22.11.0")]
    [InlineData("1.22.0", "v22.0.0")]
    [InlineData("1.21.1", "v22.9.0")]
    [InlineData("1.20.0", "v18.19.0")]
    [InlineData("1.18.2", "v16.20.0")]
    [InlineData("1.18.2", "v18.0.0")]
    [InlineData("1.17.4", "v16.13.0")]
    [InlineData("1.15.2", "v14.0.0")]
    public void A_Supported_Pair_Is_Supported(string spfx, string node)
        => Assert.Equal(SpfxNodeVerdict.Supported, Data.Check(spfx, node).Verdict);

    [Theory]
    // The common upgrade trap: a current SPFx on the Node its predecessor wanted.
    [InlineData("1.22.0", "v18.19.0")]
    [InlineData("1.21.0", "v20.11.0")]
    [InlineData("1.20.0", "v22.0.0")]
    [InlineData("1.20.0", "v16.20.0")]
    [InlineData("1.17.4", "v18.0.0")]
    [InlineData("1.23.2", "v24.0.0")]
    public void An_Unsupported_Pair_Is_Unsupported(string spfx, string node)
        => Assert.Equal(SpfxNodeVerdict.Unsupported, Data.Check(spfx, node).Verdict);

    [Fact]
    public void The_Minimum_Minor_Qualifier_Is_Honoured()
    {
        // Microsoft writes "v16.13+" for 1.17, not "v16". Flattening that would call Node 16.9 fine.
        Assert.Equal(SpfxNodeVerdict.Unsupported, Data.Check("1.17.4", "v16.9.0").Verdict);
        Assert.Equal(SpfxNodeVerdict.Supported, Data.Check("1.17.4", "v16.13.0").Verdict);

        // Same shape on 1.24, which supports Node 22 only from 22.14.
        Assert.Equal(SpfxNodeVerdict.Unsupported, Data.Check("1.24.0", "v22.0.0").Verdict);
        Assert.Equal(SpfxNodeVerdict.Supported, Data.Check("1.24.0", "v22.14.0").Verdict);
    }

    [Theory]
    // Patches within one minor differ here, so the data carries both and must not collapse upward:
    // 1.8.0/1.8.1 support Node 8 only, 1.8.2 adds 10.
    [InlineData("1.8.0", "10", SpfxNodeVerdict.Unsupported)]
    [InlineData("1.8.1", "10", SpfxNodeVerdict.Unsupported)]
    [InlineData("1.8.2", "10", SpfxNodeVerdict.Supported)]
    // 1.4.0 supports Node 6 only, 1.4.1 adds 8.
    [InlineData("1.4.0", "8", SpfxNodeVerdict.Unsupported)]
    [InlineData("1.4.1", "8", SpfxNodeVerdict.Supported)]
    public void Patch_Level_Differences_Are_Not_Collapsed_Upward(string spfx, string node, SpfxNodeVerdict expected)
        => Assert.Equal(expected, Data.Check(spfx, node).Verdict);

    [Fact]
    public void An_Unknown_Patch_Falls_Back_To_Its_Minor()
    {
        // A new patch of a known minor must not read as unknown — otherwise every SPFx patch release
        // would silently disable the check.
        var check = Data.Check("1.24.7", "v26.0.0");

        Assert.Equal(SpfxNodeVerdict.Supported, check.Verdict);
        Assert.Equal("1.24", check.MatchedVersion);
    }

    [Theory]
    [InlineData("^1.22.0")]
    [InlineData("~1.22.0")]
    [InlineData("v1.22.0")]
    [InlineData("=1.22.0")]
    public void A_Range_Operator_On_The_Spfx_Version_Is_Tolerated(string spfx)
        => Assert.Equal(SpfxNodeVerdict.Supported, Data.Check(spfx, "v22.9.0").Verdict);

    // ─── unknown is not supported ─────────────────────────────────────────

    [Fact]
    public void An_Spfx_Version_The_Data_Does_Not_Know_Is_Unknown_Not_Supported()
    {
        // The failure mode that would make this check worse than no check: a newer SPFx than the
        // bundled table reported as a pass.
        var check = Data.Check("1.99.0", "v22.14.0");

        Assert.Equal(SpfxNodeVerdict.Unknown, check.Verdict);
        Assert.NotEqual(SpfxNodeVerdict.Supported, check.Verdict);
        Assert.Contains("LoadFrom", check.Detail!, StringComparison.Ordinal);
        Assert.Contains("not a pass", check.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Missing_Spfx_Version_Is_Unknown(string? spfx)
    {
        var check = Data.Check(spfx, "v22.14.0");

        Assert.Equal(SpfxNodeVerdict.Unknown, check.Verdict);
        Assert.Contains("sp-core-library", check.Detail!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void A_Missing_Or_Unreadable_Node_Version_Is_Unknown(string? node)
        => Assert.Equal(SpfxNodeVerdict.Unknown, Data.Check("1.24.0", node).Verdict);

    // ─── the message has to name the cause ────────────────────────────────

    [Fact]
    public void An_Unsupported_Message_Names_The_Downstream_Symptom()
    {
        // The whole value of this check is replacing a confusing failure with a named one, so the
        // message has to say what the confusing failure would have looked like.
        var text = Data.Check("1.22.0", "v18.19.0").ToString();

        Assert.Contains("SPFx 1.22.0", text, StringComparison.Ordinal);
        Assert.Contains("Node 22", text, StringComparison.Ordinal);
        Assert.Contains("v18.19.0", text, StringComparison.Ordinal);
        Assert.Contains("inside Heft", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Recommended_Node_Is_Reported_Where_Microsoft_Names_One()
    {
        Assert.Equal(26, Data.Check("1.24.1", "v18.0.0").RecommendedMajor);
        Assert.Contains("recommends Node 26", Data.Check("1.24.1", "v18.0.0").ToString(), StringComparison.Ordinal);
    }

    // ─── engines.node as a cross-check ────────────────────────────────────

    [Theory]
    // Bounded above, so the majors it names are the majors it allows.
    [InlineData(">=22.14.0 <23.0.0", 22, true)]
    [InlineData(">=22.14.0 <23.0.0", 24, false)]
    // A plain enumeration.
    [InlineData("22", 22, true)]
    [InlineData("22 || 24", 24, true)]
    [InlineData("22 || 24", 26, false)]
    // Unbounded above: >=22.14.0 allows 24 and 26 too, so a textual read has no opinion rather than
    // a wrong one — and this is the most common form a rig writes, so a wrong answer here would fire
    // on almost every project.
    [InlineData(">=22.14.0", 24, null)]
    [InlineData(">=22.14.0", 14, null)]
    [InlineData("^22.14.0", 22, null)]
    [InlineData(null, 22, null)]
    [InlineData("", 22, null)]
    public void Engines_Node_Is_Read_Only_Where_A_Textual_Read_Is_Sound(string? engines, int major, bool? expected)
        => Assert.Equal(expected, SpfxNodeSupport.EnginesMentionsMajor(engines, major));

    [Theory]
    // The number after a dot is a minor or a patch, never a major: the 14 of ">=22.14.0" is not Node 14.
    [InlineData(">=22.14.0 <23", new[] { 22, 23 })]
    [InlineData("22 || 24", new[] { 22, 24 })]
    [InlineData("22.14.0", new[] { 22 })]
    [InlineData("16.13.0 || 18.0.0", new[] { 16, 18 })]
    public void Only_Leading_Numbers_Are_Read_As_Majors(string range, int[] expected)
        => Assert.Equal(expected.OrderBy(x => x), SpfxNodeSupport.MajorsMentionedIn(range).OrderBy(x => x));

    [Fact]
    public void A_Disagreement_Between_Engines_And_The_Table_Is_Surfaced()
    {
        // The project's own engines range is newer than any table shipped in a package, so when the
        // two disagree that is worth saying rather than silently preferring one.
        var check = Data.Check("1.23.2", "v24.0.0", enginesNode: "22 || 24");

        Assert.Equal(SpfxNodeVerdict.Unsupported, check.Verdict);   // the table's answer
        Assert.True(check.EnginesDisagrees);                        // but the project says otherwise
        Assert.Equal("22 || 24", check.EnginesNode);
    }

    [Fact]
    public void Agreement_Is_Not_Reported_As_A_Disagreement()
    {
        var check = Data.Check("1.24.0", "v24.0.0", enginesNode: "22 || 24");

        Assert.Equal(SpfxNodeVerdict.Supported, check.Verdict);
        Assert.False(check.EnginesDisagrees);
    }

    [Fact]
    public void An_Unreadable_Engines_Range_Never_Reports_A_Disagreement()
    {
        // The noise case: ">=22.14.0" is the most common form a rig writes and it does allow 24, so
        // claiming a disagreement here would fire on almost every project.
        var check = Data.Check("1.24.0", "v24.0.0", enginesNode: ">=22.14.0");

        Assert.Equal(SpfxNodeVerdict.Supported, check.Verdict);
        Assert.False(check.EnginesDisagrees);
    }

    // ─── the data is data ─────────────────────────────────────────────────

    [Fact]
    public void The_Bundled_Data_Records_Where_And_When_It_Came_From()
    {
        // A transcription with no provenance cannot be checked against its source.
        Assert.Equal("2026-11-02", Data.AsOf);
        Assert.Contains("learn.microsoft.com", Data.Source!, StringComparison.Ordinal);
        Assert.Equal("2026-11-02", Data.Check("1.24.0", "v26.0.0").DataAsOf);
    }

    [Fact]
    public void An_Adopter_Can_Supply_Newer_Data()
    {
        // The reason the table is a file: otherwise the package needs a release every time Microsoft
        // ships an SPFx version, and the check goes stale exactly when it matters.
        var path = Path.Combine(Path.GetTempPath(), $"spfx-node-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "asOf": "2027-01-01", "source": "local", "versions": { "1.99": { "node": [30] } } }
            """);
        try
        {
            var custom = SpfxNodeSupport.LoadFrom(path);

            Assert.Equal(SpfxNodeVerdict.Supported, custom.Check("1.99.0", "v30.1.0").Verdict);
            Assert.Equal(SpfxNodeVerdict.Unsupported, custom.Check("1.99.0", "v26.0.0").Verdict);
            // ...and the bundled data still says it does not know 1.99.
            Assert.Equal(SpfxNodeVerdict.Unknown, Data.Check("1.99.0", "v30.1.0").Verdict);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ }")]
    [InlineData("""{ "versions": { } }""")]
    [InlineData("""{ "versions": { "1.0": { } } }""")]
    public void Unusable_Data_Is_Refused_Rather_Than_Silently_Empty(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spfx-node-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { Assert.Throws<InvalidDataException>(() => SpfxNodeSupport.LoadFrom(path)); }
        finally { File.Delete(path); }
    }
}
