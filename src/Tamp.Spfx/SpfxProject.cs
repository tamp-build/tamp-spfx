using System.Text.Json;

namespace Tamp.Spfx;

/// <summary>Which build toolchain an SPFx project uses.</summary>
public enum SpfxToolchain
{
    /// <summary>Couldn't tell (no recognisable toolchain dependency).</summary>
    Unknown,
    /// <summary>SPFx 1.22+: Heft with <c>@microsoft/spfx-web-build-rig</c>. What this package drives.</summary>
    Heft,
    /// <summary>The legacy gulp toolchain (<c>gulpfile.js</c> / <c>@microsoft/sp-build-web</c>). Not driven by this package.</summary>
    Gulp,
}

/// <summary>An API permission the solution asks the tenant admin to grant (<c>webApiPermissionRequests</c>).</summary>
/// <param name="Resource">The API, by display name or app id (<c>Microsoft Graph</c>, <c>00000003-0000-0000-c000-000000000000</c>, …).</param>
/// <param name="Scope">The delegated scope (<c>Sites.Read.All</c>, <c>user_impersonation</c>, …).</param>
public sealed record SpfxPermissionRequest(string Resource, string Scope)
{
    /// <summary><c>Resource / Scope</c>.</summary>
    public override string ToString() => $"{Resource} / {Scope}";

    /// <summary>Case-insensitive match on both parts (SharePoint treats them case-insensitively).</summary>
    public bool Matches(SpfxPermissionRequest other) =>
        string.Equals(Resource, other.Resource, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Scope, other.Scope, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A feature declared in <c>package-solution.json</c>.</summary>
public sealed record SpfxFeature(string? Id, string? Title, string? Version);

/// <summary>The <c>solution</c> section of <c>config/package-solution.json</c>.</summary>
public sealed record SpfxSolutionConfig
{
    /// <summary><c>solution.name</c>.</summary>
    public string? Name { get; init; }
    /// <summary><c>solution.id</c> (the app's product id).</summary>
    public string? Id { get; init; }
    /// <summary><c>solution.version</c>, as written.</summary>
    public string? Version { get; init; }
    /// <summary><c>solution.skipFeatureDeployment</c>: the admin may make the solution available tenant-wide on deploy.</summary>
    public bool SkipFeatureDeployment { get; init; }
    /// <summary>
    /// <c>solution.isDomainIsolated</c>, as written. The SPFx 1.24 packager ignores it (isolated web parts are
    /// retired) and it never reaches the <c>.sppkg</c>, so nothing here treats it as a security property.
    /// </summary>
    public bool IsDomainIsolated { get; init; }
    /// <summary><c>solution.includeClientSideAssets</c>: bundle the JS into the package (false = served from a CDN).</summary>
    public bool IncludeClientSideAssets { get; init; } = true;
    /// <summary><c>solution.webApiPermissionRequests</c>.</summary>
    public IReadOnlyList<SpfxPermissionRequest> WebApiPermissionRequests { get; init; } = Array.Empty<SpfxPermissionRequest>();
    /// <summary><c>solution.features</c>.</summary>
    public IReadOnlyList<SpfxFeature> Features { get; init; } = Array.Empty<SpfxFeature>();
    /// <summary><c>paths.zippedPackage</c>, relative to <c>sharepoint/</c>.</summary>
    public string? ZippedPackage { get; init; }
}

/// <summary>
/// Where the SPFx Heft rig writes its outputs. These are the <c>@microsoft/spfx-web-build-rig</c> defaults
/// (verified against 1.24.0); override with <c>with</c> if a project's rig config moves them.
/// </summary>
public sealed record SpfxOutputs
{
    /// <summary>The project root every other path is under.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The <c>.sppkg</c> that <c>heft package-solution</c> writes.</summary>
    public required string Sppkg { get; init; }

    /// <summary>ESLint SARIF from the rig's lint task: <c>release/analysis-logs/lint.sarif</c>.</summary>
    public string LintSarif => Path.Combine(ProjectDirectory, "release", "analysis-logs", "lint.sarif");

    /// <summary>Jest coverage, Cobertura: <c>jest-output/coverage/cobertura/cobertura-coverage.xml</c>.</summary>
    public string CoberturaCoverage => Path.Combine(ProjectDirectory, "jest-output", "coverage", "cobertura", "cobertura-coverage.xml");

    /// <summary>Jest coverage summary: <c>jest-output/coverage/coverage-summary.json</c>.</summary>
    public string CoverageSummary => Path.Combine(ProjectDirectory, "jest-output", "coverage", "coverage-summary.json");

    /// <summary>Jest results, JUnit XML: <c>jest-output/JUnit.xml</c>.</summary>
    public string JUnitResults => Path.Combine(ProjectDirectory, "jest-output", "JUnit.xml");

    /// <summary>The component dependency audit the rig writes per bundle: <c>release/component-dependency-audit/</c>.</summary>
    public string ComponentDependencyAudit => Path.Combine(ProjectDirectory, "release", "component-dependency-audit");

    /// <summary>The component manifests: <c>release/manifests/</c>.</summary>
    public string Manifests => Path.Combine(ProjectDirectory, "release", "manifests");

    /// <summary>The bundled client-side assets: <c>release/assets/</c>.</summary>
    public string Assets => Path.Combine(ProjectDirectory, "release", "assets");
}

/// <summary>
/// A SharePoint Framework project on disk: its <c>package.json</c>, <c>config/package-solution.json</c> and
/// <c>config/config.json</c>, read without running anything.
/// </summary>
public sealed record SpfxProject
{
    /// <summary>The project root (the directory holding <c>package.json</c>).</summary>
    public required string Directory { get; init; }
    /// <summary>The npm package name.</summary>
    public string? PackageName { get; init; }
    /// <summary>The SPFx version, from the <c>@microsoft/sp-core-library</c> (or build rig) dependency, without range operators.</summary>
    public string? SpfxVersion { get; init; }
    /// <summary>The toolchain the project builds with.</summary>
    public SpfxToolchain Toolchain { get; init; }
    /// <summary>The solution definition.</summary>
    public required SpfxSolutionConfig Solution { get; init; }
    /// <summary><c>config/config.json</c> externals: module name to script URL or path.</summary>
    public IReadOnlyDictionary<string, string> Externals { get; init; } = new Dictionary<string, string>();
    /// <summary>The path of <c>config/package-solution.json</c>.</summary>
    public string PackageSolutionPath => Path.Combine(Directory, "config", "package-solution.json");
    /// <summary>Where the build writes its outputs.</summary>
    public required SpfxOutputs Outputs { get; init; }

    /// <summary>Read the SPFx project at <paramref name="directory"/>.</summary>
    /// <exception cref="FileNotFoundException">No <c>package.json</c> or <c>config/package-solution.json</c>.</exception>
    /// <exception cref="InvalidDataException">A config file isn't valid JSON(C).</exception>
    public static SpfxProject Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory is required.", nameof(directory));
        var root = Path.GetFullPath(directory);
        var packageJson = Path.Combine(root, "package.json");
        var solutionJson = Path.Combine(root, "config", "package-solution.json");
        if (!File.Exists(packageJson)) throw new FileNotFoundException("Not an SPFx project: package.json is missing.", packageJson);
        if (!File.Exists(solutionJson)) throw new FileNotFoundException("Not an SPFx project: config/package-solution.json is missing.", solutionJson);

        string? name, spfxVersion;
        SpfxToolchain toolchain;
        using (var pkg = Jsonc.ParseFile(packageJson))
        {
            var p = pkg.RootElement;
            name = p.GetString("name");
            var deps = Dependencies(p);
            spfxVersion = Bare(deps.GetValueOrDefault("@microsoft/sp-core-library") ?? deps.GetValueOrDefault("@microsoft/spfx-web-build-rig"));
            toolchain = deps.ContainsKey("@microsoft/spfx-web-build-rig") || deps.ContainsKey("@rushstack/heft")
                ? SpfxToolchain.Heft
                : deps.ContainsKey("@microsoft/sp-build-web") || File.Exists(Path.Combine(root, "gulpfile.js"))
                    ? SpfxToolchain.Gulp
                    : SpfxToolchain.Unknown;
        }

        SpfxSolutionConfig solution;
        using (var doc = Jsonc.ParseFile(solutionJson))
            solution = ReadSolution(doc.RootElement);

        var externals = new Dictionary<string, string>(StringComparer.Ordinal);
        var configJson = Path.Combine(root, "config", "config.json");
        if (File.Exists(configJson))
        {
            using var cfg = Jsonc.ParseFile(configJson);
            if (cfg.RootElement.GetObject("externals") is { } ext)
                foreach (var e in ext.EnumerateObject())
                {
                    var path = e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() : e.Value.GetString("path");
                    if (!string.IsNullOrEmpty(path)) externals[e.Name] = path!;
                }
        }

        var zipped = solution.ZippedPackage ?? $"solution/{name ?? "solution"}.sppkg";
        return new SpfxProject
        {
            Directory = root,
            PackageName = name,
            SpfxVersion = spfxVersion,
            Toolchain = toolchain,
            Solution = solution,
            Externals = externals,
            Outputs = new SpfxOutputs
            {
                ProjectDirectory = root,
                Sppkg = Path.GetFullPath(Path.Combine(root, "sharepoint", zipped.Replace('/', Path.DirectorySeparatorChar))),
            },
        };
    }

    internal static SpfxSolutionConfig ReadSolution(JsonElement root)
    {
        var s = root.GetObject("solution") ?? throw new InvalidDataException("package-solution.json has no \"solution\" object.");
        return new SpfxSolutionConfig
        {
            Name = s.GetString("name"),
            Id = s.GetString("id"),
            Version = s.GetString("version"),
            SkipFeatureDeployment = s.GetBool("skipFeatureDeployment") ?? false,
            IsDomainIsolated = s.GetBool("isDomainIsolated") ?? false,
            IncludeClientSideAssets = s.GetBool("includeClientSideAssets") ?? true,
            WebApiPermissionRequests = s.GetArray("webApiPermissionRequests")
                .Select(r => new SpfxPermissionRequest(r.GetString("resource") ?? "", r.GetString("scope") ?? ""))
                .ToList(),
            Features = s.GetArray("features")
                .Select(f => new SpfxFeature(f.GetString("id"), f.GetString("title"), f.GetString("version")))
                .ToList(),
            ZippedPackage = root.GetObject("paths")?.GetString("zippedPackage"),
        };
    }

    private static Dictionary<string, string> Dependencies(JsonElement package)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in new[] { "devDependencies", "dependencies" })
            if (package.GetObject(section) is { } deps)
                foreach (var d in deps.EnumerateObject())
                    if (d.Value.ValueKind == JsonValueKind.String) all[d.Name] = d.Value.GetString()!;
        return all;
    }

    private static string? Bare(string? range) => range?.TrimStart('^', '~', '=', 'v', ' ').Trim();
}
