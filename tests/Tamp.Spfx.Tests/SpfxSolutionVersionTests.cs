using Xunit;

namespace Tamp.Spfx.Tests;

public sealed class SpfxSolutionVersionTests
{
    [Theory]
    [InlineData("1.0.0.0", 1, 0, 0, 0)]
    [InlineData("1.24.3.4567", 1, 24, 3, 4567)]
    [InlineData("0.0.0.2147483647", 0, 0, 0, int.MaxValue)]
    public void Parses_Four_Part_Versions(string text, int a, int b, int c, int d)
    {
        var v = SpfxSolutionVersion.Parse(text);
        Assert.Equal(new SpfxSolutionVersion(a, b, c, d), v);
        Assert.Equal(text, v.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0.0.0")]
    [InlineData("1.0.0.x")]
    [InlineData("1.0.-1.0")]
    [InlineData(" 1.0.0.0")]
    [InlineData("1..0.0")]
    [InlineData("1.0.0.2147483648")]
    [InlineData("1.0.0.99999999999")]
    public void Rejects_Anything_Else(string? text)
    {
        Assert.False(SpfxSolutionVersion.TryParse(text, out _));
        if (text is not null) Assert.Throws<FormatException>(() => SpfxSolutionVersion.Parse(text));
    }

    [Theory]
    [InlineData("1.4.2", 0, "1.4.2.0")]
    [InlineData("v1.4.2", 17, "1.4.2.17")]
    [InlineData("1.4.2-beta.3", 512, "1.4.2.512")]
    [InlineData("2.0.0+sha.abc123", 3, "2.0.0.3")]
    [InlineData("1.4.2-rc.1+build.9", 9, "1.4.2.9")]
    public void Maps_SemVer_Dropping_Prerelease_And_Metadata(string semver, int revision, string expected) =>
        Assert.Equal(expected, SpfxSolutionVersion.FromSemVer(semver, revision).ToString());

    [Theory]
    [InlineData("1.4")]
    [InlineData("1.4.2.0")]
    [InlineData("01.4.2")]
    [InlineData("latest")]
    [InlineData("99999999999.0.0")]
    public void Rejects_Non_SemVer(string semver) =>
        Assert.Throws<FormatException>(() => SpfxSolutionVersion.FromSemVer(semver));

    [Fact]
    public void Rejects_A_Negative_Revision() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SpfxSolutionVersion.FromSemVer("1.0.0", -1));

    [Fact]
    public void Orders_Numerically_Not_Lexically()
    {
        var a = SpfxSolutionVersion.Parse("1.9.0.0");
        var b = SpfxSolutionVersion.Parse("1.10.0.0");
        Assert.True(b > a);
        Assert.True(a < b);
        Assert.True(a <= SpfxSolutionVersion.Parse("1.9.0.0"));
        Assert.True(b >= a);
        Assert.True(SpfxSolutionVersion.Parse("1.0.0.10") > SpfxSolutionVersion.Parse("1.0.0.9"));
        Assert.True(SpfxSolutionVersion.Parse("1.0.1.0") > SpfxSolutionVersion.Parse("1.0.0.99"));
        Assert.True(SpfxSolutionVersion.Parse("2.0.0.0") > SpfxSolutionVersion.Parse("1.99.99.99"));
        Assert.Equal(0, a.CompareTo(SpfxSolutionVersion.Parse("1.9.0.0")));
    }
}
