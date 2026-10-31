namespace Tamp.Spfx;

/// <summary>
/// Stamps the solution version into <c>config/package-solution.json</c> without reformatting it: only the
/// version string literals change, so comments, key order, indentation and line endings survive and the diff
/// is exactly the version.
/// </summary>
public static class PackageSolutionEditor
{
    private const string SolutionVersion = "$.solution.version";
    private const string FeatureVersion = "$.solution.features[*].version";

    /// <summary>
    /// Set <c>solution.version</c> (and, by default, every <c>solution.features[].version</c>) to
    /// <paramref name="version"/>. Idempotent: returns false and leaves the file untouched when nothing changes.
    /// </summary>
    /// <param name="packageSolutionPath">Path of <c>config/package-solution.json</c>.</param>
    /// <param name="version">The new version.</param>
    /// <param name="includeFeatures">Also stamp the feature versions (the SPFx convention is that they track the solution).</param>
    /// <returns>True when the file was rewritten.</returns>
    public static bool SetVersion(string packageSolutionPath, SpfxSolutionVersion version, bool includeFeatures = true)
    {
        if (string.IsNullOrWhiteSpace(packageSolutionPath)) throw new ArgumentException("path is required.", nameof(packageSolutionPath));
        var original = File.ReadAllBytes(packageSolutionPath);
        var updated = SetVersion(original, version, includeFeatures, packageSolutionPath);
        if (updated is null) return false;
        File.WriteAllBytes(packageSolutionPath, updated);
        return true;
    }

    /// <summary>Stamp the version of the SPFx project's own <c>package-solution.json</c>.</summary>
    public static bool SetVersion(SpfxProject project, SpfxSolutionVersion version, bool includeFeatures = true) =>
        SetVersion((project ?? throw new ArgumentNullException(nameof(project))).PackageSolutionPath, version, includeFeatures);

    /// <summary>The edit itself, on bytes. Returns null when nothing would change.</summary>
    internal static byte[]? SetVersion(byte[] original, SpfxSolutionVersion version, bool includeFeatures, string nameForErrors = "package-solution.json")
    {
        var json = Jsonc.StripBom(original, out var hadBom);

        // Parse fully first, so a malformed file is reported rather than half-edited.
        try { using var _ = System.Text.Json.JsonDocument.Parse(json, Jsonc.DocumentOptions); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidDataException($"{nameForErrors} is not valid JSON: {ex.Message}", ex); }

        var targets = Jsonc.FindStringValues(json, p => p == SolutionVersion || (includeFeatures && p == FeatureVersion));
        if (!targets.Exists(t => t.Path == SolutionVersion))
            throw new InvalidDataException($"{nameForErrors} has no solution.version string to stamp.");

        var text = version.ToString();
        var changed = targets.Where(t => t.Value != text).Select(t => (t.Start, t.Length)).ToList();
        if (changed.Count == 0) return null;

        var body = Jsonc.Splice(json, changed, text);
        return hadBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }
}
