using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Tamp.Spfx;

/// <summary>Whether the running Node major is one the project's SPFx version supports.</summary>
public enum SpfxNodeVerdict
{
    /// <summary>
    /// No judgement could be made — the SPFx version could not be read, or it is not in the
    /// compatibility data. <b>Not the same as supported.</b> A newer SPFx release than this package's
    /// data file knows about lands here, which is why the data is overridable.
    /// </summary>
    Unknown,

    /// <summary>The running Node major is listed for this SPFx version.</summary>
    Supported,

    /// <summary>
    /// The running Node major is not listed for this SPFx version. Building anyway does not produce a
    /// clear error: it produces a failure inside Heft, a native-module build error, or a silently
    /// different bundle.
    /// </summary>
    Unsupported,
}

/// <summary>The outcome of a Node compatibility check, with enough detail to act on.</summary>
public sealed record SpfxNodeCheck
{
    /// <summary>The verdict.</summary>
    public required SpfxNodeVerdict Verdict { get; init; }

    /// <summary>The SPFx version the check was made against, as read from the project.</summary>
    public string? SpfxVersion { get; init; }

    /// <summary>The key in the compatibility data that <see cref="SpfxVersion"/> resolved to.</summary>
    public string? MatchedVersion { get; init; }

    /// <summary>The running Node version, as reported.</summary>
    public string? NodeVersion { get; init; }

    /// <summary>The running Node major.</summary>
    public int? NodeMajor { get; init; }

    /// <summary>Node majors this SPFx version supports.</summary>
    public IReadOnlyList<int> SupportedMajors { get; init; } = [];

    /// <summary>The Node major Microsoft recommends for a new project on this SPFx version, if any.</summary>
    public int? RecommendedMajor { get; init; }

    /// <summary>
    /// What the project's own <c>engines.node</c> says, verbatim, when it declares one. Authoritative
    /// per project in a way a table shipped in a NuGet package cannot be — see
    /// <see cref="EnginesDisagrees"/>.
    /// </summary>
    public string? EnginesNode { get; init; }

    /// <summary>
    /// The project's <c>engines.node</c> mentions no major matching the running Node, while the
    /// compatibility data says it is fine — or the reverse.
    /// </summary>
    /// <remarks>
    /// A conservative textual read, deliberately <b>not</b> a semver range evaluation: it reports that
    /// the two sources disagree and leaves the judgement to a human, rather than implementing range
    /// satisfaction and being quietly wrong about it. Worth surfacing because the rig declares its own
    /// supported range and that is newer than any table shipped in a package.
    /// </remarks>
    public bool EnginesDisagrees { get; init; }

    /// <summary>Why the verdict is <see cref="SpfxNodeVerdict.Unknown"/>, when it is.</summary>
    public string? Detail { get; init; }

    /// <summary>The date the bundled compatibility data was transcribed.</summary>
    public string? DataAsOf { get; init; }

    /// <summary>A line for a build log, naming the cause rather than leaving it to be inferred.</summary>
    public override string ToString() => Verdict switch
    {
        SpfxNodeVerdict.Supported =>
            $"SPFx {SpfxVersion} supports Node {string.Join(", ", SupportedMajors)}; running Node {NodeVersion}. OK."
            + (EnginesDisagrees ? $" NOTE: the project's engines.node ({EnginesNode}) disagrees with the bundled data — trust engines." : ""),
        SpfxNodeVerdict.Unsupported =>
            $"SPFx {SpfxVersion} supports Node {string.Join(", ", SupportedMajors)}, but Node {NodeVersion} is running. "
            + (RecommendedMajor is { } r ? $"Microsoft recommends Node {r} for this version. " : "")
            + "Building on an unsupported Node does not fail clearly: expect an error inside Heft, a native-module "
            + "build failure, or a silently different bundle.",
        _ => $"Could not judge Node compatibility: {Detail ?? "no detail"}. "
             + $"This is not a pass — it means nothing was checked (SPFx {SpfxVersion ?? "unknown"}, Node {NodeVersion ?? "unknown"}).",
    };
}

/// <summary>
/// Compares a project's SPFx version against the running Node major, so an unsupported pair is named
/// before the build rather than discovered inside it.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft publishes a supported Node major per SPFx version and the window is narrow — one or two
/// majors, moving every few releases. Building on an unsupported Node does not produce a clear error;
/// it produces a failure inside Heft, a native-module build error, or a silently different bundle. The
/// value of this check is replacing a confusing downstream failure with a named one, so it is only
/// worth anything if it runs <b>first</b>.
/// </para>
/// <para>
/// <b>The table is data, not code</b> (<c>spfx-node-compatibility.json</c>, embedded). A newer SPFx
/// release can be taught to an existing build with <see cref="LoadFrom"/> rather than waiting for a
/// Tamp.Spfx release — otherwise this package would need one every time Microsoft ships an SPFx
/// version, and the check would go stale exactly when it matters.
/// </para>
/// <para>
/// <b>Three outcomes, not two.</b> An SPFx version the data does not know about is
/// <see cref="SpfxNodeVerdict.Unknown"/>, never <see cref="SpfxNodeVerdict.Supported"/>: reporting an
/// unchecked pair as a good pair is how a check becomes worse than no check.
/// </para>
/// </remarks>
public sealed class SpfxNodeSupport
{
    private readonly Dictionary<string, (int[] Majors, Dictionary<int, int> MinimumMinor)> _versions;
    private readonly Dictionary<string, int> _recommended;

    /// <summary>The date the loaded compatibility data was transcribed from Microsoft's table.</summary>
    public string? AsOf { get; }

    /// <summary>Where the loaded data came from.</summary>
    public string? Source { get; }

    private SpfxNodeSupport(
        Dictionary<string, (int[], Dictionary<int, int>)> versions,
        Dictionary<string, int> recommended,
        string? asOf,
        string? source)
    {
        _versions = versions;
        _recommended = recommended;
        AsOf = asOf;
        Source = source;
    }

    /// <summary>The compatibility data shipped with this package.</summary>
    public static SpfxNodeSupport Bundled { get; } = Parse(ReadBundled());

    /// <summary>
    /// Load compatibility data from <paramref name="path"/>, for a project that needs to know about an
    /// SPFx version newer than this package. Same shape as the bundled
    /// <c>spfx-node-compatibility.json</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not valid compatibility data.</exception>
    public static SpfxNodeSupport LoadFrom(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required.", nameof(path));
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Check <paramref name="project"/>'s SPFx version against a Node version.
    /// </summary>
    /// <param name="project">The project, for its SPFx version and its <c>engines.node</c>.</param>
    /// <param name="nodeVersion">
    /// The Node version, as <c>node --version</c> reports it (<c>v22.14.0</c>) or bare (<c>22.14.0</c>).
    /// Null or unparseable yields <see cref="SpfxNodeVerdict.Unknown"/> rather than a guess.
    /// </param>
    public SpfxNodeCheck Check(SpfxProject project, string? nodeVersion)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        return Check(project.SpfxVersion, nodeVersion, project.EnginesNode);
    }

    /// <summary>
    /// <see cref="Check(SpfxProject, string?)"/> against loose values, for a caller that has the
    /// versions but not a loaded project.
    /// </summary>
    public SpfxNodeCheck Check(string? spfxVersion, string? nodeVersion, string? enginesNode = null)
    {
        var nodeMajor = MajorOf(nodeVersion);
        var nodeMinor = MinorOf(nodeVersion);

        if (string.IsNullOrWhiteSpace(spfxVersion))
            return new SpfxNodeCheck
            {
                Verdict = SpfxNodeVerdict.Unknown,
                NodeVersion = nodeVersion,
                NodeMajor = nodeMajor,
                EnginesNode = enginesNode,
                DataAsOf = AsOf,
                Detail = "the project declares no @microsoft/sp-core-library or @microsoft/spfx-web-build-rig "
                       + "dependency, so its SPFx version could not be read",
            };

        if (nodeMajor is null)
            return new SpfxNodeCheck
            {
                Verdict = SpfxNodeVerdict.Unknown,
                SpfxVersion = spfxVersion,
                NodeVersion = nodeVersion,
                EnginesNode = enginesNode,
                DataAsOf = AsOf,
                Detail = nodeVersion is null
                    ? "no Node version was supplied — resolve `node --version` and pass it"
                    : $"'{nodeVersion}' is not a Node version this can read",
            };

        if (Resolve(spfxVersion) is not { } entry)
            return new SpfxNodeCheck
            {
                Verdict = SpfxNodeVerdict.Unknown,
                SpfxVersion = spfxVersion,
                NodeVersion = nodeVersion,
                NodeMajor = nodeMajor,
                EnginesNode = enginesNode,
                DataAsOf = AsOf,
                Detail = $"SPFx {spfxVersion} is not in the compatibility data (transcribed {AsOf}). It is probably "
                       + "newer than this package: pass your own table to SpfxNodeSupport.LoadFrom rather than "
                       + "treating this as a pass",
            };

        var (key, majors, minimumMinor) = entry;
        var supported = majors.Contains(nodeMajor.Value);

        // "v16.13+" in Microsoft's table: the major is supported, but only from a minimum minor.
        if (supported && minimumMinor.TryGetValue(nodeMajor.Value, out var floor) && nodeMinor is { } minor && minor < floor)
            supported = false;

        return new SpfxNodeCheck
        {
            Verdict = supported ? SpfxNodeVerdict.Supported : SpfxNodeVerdict.Unsupported,
            SpfxVersion = spfxVersion,
            MatchedVersion = key,
            NodeVersion = nodeVersion,
            NodeMajor = nodeMajor,
            SupportedMajors = majors,
            RecommendedMajor = _recommended.TryGetValue(key, out var recommended) ? recommended : null,
            EnginesNode = enginesNode,
            EnginesDisagrees = EnginesMentionsMajor(enginesNode, nodeMajor.Value) is { } mentioned && mentioned != supported,
            DataAsOf = AsOf,
        };
    }

    /// <summary>
    /// Resolve a project version against the data: exact, then <c>major.minor</c>, then <c>major</c>.
    /// So <c>1.24.2</c> finds <c>1.24</c> and a new patch does not read as unknown.
    /// </summary>
    private (string Key, int[] Majors, Dictionary<int, int> MinimumMinor)? Resolve(string spfxVersion)
    {
        var bare = spfxVersion.TrimStart('^', '~', '=', 'v', ' ').Trim();
        var parts = bare.Split('.');

        foreach (var candidate in Candidates(parts, bare))
            if (_versions.TryGetValue(candidate, out var found))
                return (candidate, found.Majors, found.MinimumMinor);

        return null;
    }

    private static IEnumerable<string> Candidates(string[] parts, string bare)
    {
        yield return bare;
        if (parts.Length >= 2) yield return $"{parts[0]}.{parts[1]}";
        if (parts.Length >= 1) yield return parts[0];
    }

    /// <summary>
    /// Whether <c>engines.node</c> names <paramref name="major"/> among the majors it allows, or
    /// <see langword="null"/> when the range cannot be read this way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A textual read of the majors a range enumerates — deliberately <b>not</b> semver satisfaction.
    /// It exists to spot a disagreement between the project's own declaration and the bundled table,
    /// not to replace either.
    /// </para>
    /// <para>
    /// <b>An open-ended lower bound returns no opinion.</b> <c>&gt;=22.14.0</c> allows Node 24 and 26
    /// as well, so reading the majors it mentions and concluding "24 is not allowed" would be wrong —
    /// and wrong on the most common form a rig writes, so it would fire on almost every project.
    /// Ranges with an upper bound (<c>&gt;=22.14.0 &lt;23</c>) and plain enumerations
    /// (<c>22 || 24</c>) are readable; an unbounded <c>&gt;=</c> or <c>^</c> is not, and says so by
    /// returning null.
    /// </para>
    /// <para>
    /// Being approximately right about a narrow case and silent about the rest beats being precisely
    /// wrong about everything.
    /// </para>
    /// </remarks>
    internal static bool? EnginesMentionsMajor(string? enginesNode, int major)
    {
        if (string.IsNullOrWhiteSpace(enginesNode)) return null;

        var range = enginesNode!;
        var hasLowerBound = range.Contains('>') || range.Contains('^') || range.Contains('~');
        var hasUpperBound = range.Contains('<');
        if (hasLowerBound && !hasUpperBound) return null;   // unbounded above: not readable textually

        var mentioned = MajorsMentionedIn(range);
        return mentioned.Count == 0 ? null : mentioned.Contains(major);
    }

    /// <summary>
    /// The major version numbers a range names. A number directly after a <c>.</c> is a minor or a
    /// patch, not a major: the <c>14</c> of <c>&gt;=22.14.0</c> must not be read as Node 14.
    /// </summary>
    internal static HashSet<int> MajorsMentionedIn(string range)
    {
        var mentioned = new HashSet<int>();
        var index = 0;

        while (index < range.Length)
        {
            if (!char.IsDigit(range[index])) { index++; continue; }

            // The character before this run of digits decides whether it is a major.
            var precededByDot = index > 0 && range[index - 1] == '.';

            var start = index;
            while (index < range.Length && char.IsDigit(range[index])) index++;

            if (!precededByDot
                && int.TryParse(range.AsSpan(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                mentioned.Add(value);

            // Skip the rest of a dotted version so 22.14.0 contributes only 22.
            while (index < range.Length && (range[index] == '.' || char.IsDigit(range[index]))) index++;
        }

        return mentioned;
    }

    internal static int? MajorOf(string? version) => Component(version, 0);

    internal static int? MinorOf(string? version) => Component(version, 1);

    private static int? Component(string? version, int index)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version!.TrimStart('v', 'V', '^', '~', '=', ' ').Trim().Split('.');
        if (index >= parts.Length) return null;
        return int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string ReadBundled()
    {
        var assembly = typeof(SpfxNodeSupport).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("spfx-node-compatibility.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "spfx-node-compatibility.json is missing from Tamp.Spfx. It is an embedded resource; "
                + "the package is built wrong if this throws.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SpfxNodeSupport Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new InvalidDataException($"Not valid compatibility data: {ex.Message}", ex); }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Compatibility data needs a \"versions\" object keyed by SPFx version.");

            var parsed = new Dictionary<string, (int[], Dictionary<int, int>)>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in versions.EnumerateObject())
            {
                var majors = version.Value.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Array
                    ? node.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToArray()
                    : [];
                if (majors.Length == 0) continue;

                var floors = new Dictionary<int, int>();
                if (version.Value.TryGetProperty("minimumMinor", out var minimums) && minimums.ValueKind == JsonValueKind.Object)
                    foreach (var minimum in minimums.EnumerateObject())
                        if (int.TryParse(minimum.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                            && minimum.Value.TryGetInt32(out var floor))
                            floors[major] = floor;

                parsed[version.Name] = (majors, floors);
            }

            if (parsed.Count == 0) throw new InvalidDataException("Compatibility data lists no usable versions.");

            var recommended = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("recommended", out var recommendations) && recommendations.ValueKind == JsonValueKind.Object)
                foreach (var recommendation in recommendations.EnumerateObject())
                    if (recommendation.Value.TryGetInt32(out var major)) recommended[recommendation.Name] = major;

            return new SpfxNodeSupport(
                parsed,
                recommended,
                root.TryGetProperty("asOf", out var asOf) ? asOf.GetString() : null,
                root.TryGetProperty("source", out var source) ? source.GetString() : null);
        }
    }
}
