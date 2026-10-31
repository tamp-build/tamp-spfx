using System.Globalization;
using System.Text.RegularExpressions;

namespace Tamp.Spfx;

/// <summary>
/// A SharePoint solution version: exactly four non-negative integers, <c>major.minor.build.revision</c>.
/// This is what <c>config/package-solution.json</c> and the <c>.sppkg</c>'s <c>AppManifest.xml</c> carry, and
/// SharePoint compares it numerically when deciding whether an upload is an upgrade.
/// </summary>
public readonly record struct SpfxSolutionVersion(int Major, int Minor, int Build, int Revision)
    : IComparable<SpfxSolutionVersion>
{
    private static readonly Regex SemVer = new(
        @"^v?(?<maj>0|[1-9]\d*)\.(?<min>0|[1-9]\d*)\.(?<pat>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Parse <c>a.b.c.d</c>. Anything else (three parts, signs, whitespace, overflow) is rejected.</summary>
    public static SpfxSolutionVersion Parse(string value) =>
        TryParse(value, out var v) ? v : throw new FormatException($"'{value}' is not a SharePoint solution version (expected four integers, e.g. 1.4.0.12).");

    /// <summary>Try to parse <c>a.b.c.d</c>.</summary>
    public static bool TryParse(string? value, out SpfxSolutionVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split('.');
        if (parts.Length != 4) return false;
        var n = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var p = parts[i];
            if (p.Length == 0 || p.Length > 10 || !p.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        }
        version = new SpfxSolutionVersion(n[0], n[1], n[2], n[3]);
        return true;
    }

    /// <summary>
    /// Map a semantic version (the build's version, e.g. from GitVersion or a tag) onto a solution version:
    /// <c>1.4.2</c> becomes <c>1.4.2.&lt;revision&gt;</c>. Pre-release and build metadata are dropped, because
    /// SharePoint has nowhere to put them; pass a monotonically increasing <paramref name="revision"/> (a CI
    /// run number, a commit count) so two pre-releases of the same core version still order correctly.
    /// </summary>
    public static SpfxSolutionVersion FromSemVer(string semver, int revision = 0)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision), "revision must be non-negative.");
        var m = SemVer.Match(semver ?? string.Empty);
        if (!m.Success) throw new FormatException($"'{semver}' is not a semantic version (expected major.minor.patch[-pre][+meta]).");
        int Part(string g) => int.TryParse(m.Groups[g].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var x)
            ? x
            : throw new FormatException($"'{semver}': the {g} component is too large for a solution version.");
        return new SpfxSolutionVersion(Part("maj"), Part("min"), Part("pat"), revision);
    }

    /// <inheritdoc />
    public int CompareTo(SpfxSolutionVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Build.CompareTo(other.Build);
        return c != 0 ? c : Revision.CompareTo(other.Revision);
    }

    /// <summary>True when <paramref name="a"/> sorts after <paramref name="b"/>.</summary>
    public static bool operator >(SpfxSolutionVersion a, SpfxSolutionVersion b) => a.CompareTo(b) > 0;
    /// <summary>True when <paramref name="a"/> sorts before <paramref name="b"/>.</summary>
    public static bool operator <(SpfxSolutionVersion a, SpfxSolutionVersion b) => a.CompareTo(b) < 0;
    /// <summary>True when <paramref name="a"/> sorts at or after <paramref name="b"/>.</summary>
    public static bool operator >=(SpfxSolutionVersion a, SpfxSolutionVersion b) => a.CompareTo(b) >= 0;
    /// <summary>True when <paramref name="a"/> sorts at or before <paramref name="b"/>.</summary>
    public static bool operator <=(SpfxSolutionVersion a, SpfxSolutionVersion b) => a.CompareTo(b) <= 0;

    /// <summary><c>major.minor.build.revision</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Build}.{Revision}");
}
