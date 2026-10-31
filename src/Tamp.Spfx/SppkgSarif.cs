using Tamp.Sarif;

namespace Tamp.Spfx;

/// <summary>
/// Renders a policy evaluation as SARIF 2.1.0, so package findings travel the same path as every other scanner's
/// (merge, dedup, findings ingest). Findings are about a package, not a source file, so each result carries a
/// <em>logical</em> location (<c>sppkg://&lt;solution id&gt;/&lt;subject&gt;</c>) instead of a synthesised path.
/// </summary>
public static class SppkgSarif
{
    /// <summary>The tool name in the SARIF driver.</summary>
    public const string ToolName = "Tamp.Spfx";

    private static readonly IReadOnlyDictionary<string, (string Name, string Description)> Rules = new Dictionary<string, (string, string)>
    {
        [SppkgRules.Unreadable] = ("PackageUnreadable", "Part of the package could not be read, so the inspection is incomplete."),
        [SppkgRules.UnsafeEntry] = ("UnsafeArchiveEntry", "An archive entry's path would escape the extraction directory."),
        [SppkgRules.Version] = ("UnexpectedVersion", "The package version is malformed or is not the version the build stamped."),
        [SppkgRules.Identity] = ("UnexpectedSolutionId", "The package's solution id is not the expected one."),
        [SppkgRules.Size] = ("PackageTooLarge", "The package is larger than the policy allows."),
        [SppkgRules.Permission] = ("ApiPermissionRequest", "The solution asks the tenant admin to grant an API permission."),
        [SppkgRules.TenantWide] = ("TenantWideDeployment", "The solution can be made available to every site on deploy."),
        [SppkgRules.CustomScript] = ("RequiresCustomScript", "A component requires custom script to be enabled on the site."),
        [SppkgRules.ExternalScript] = ("ExternalScript", "A component loads script from outside the package."),
        [SppkgRules.TenantWideExtension] = ("TenantWideExtension", "An extension is registered to run on every site when deployed tenant-wide."),
        [SppkgRules.VersionNotIncreased] = ("VersionNotIncreased", "The version is not greater than the deployed release's."),
        [SppkgRules.NewPermission] = ("NewApiPermissionRequest", "A permission request the deployed release did not make."),
        [SppkgRules.NewTenantWide] = ("BecameTenantWide", "The solution became tenant-wide deployable."),
        [SppkgRules.NewExternalScript] = ("NewExternalScript", "A script origin the deployed release did not use."),
        [SppkgRules.ComponentRemoved] = ("ComponentRemoved", "A component in the deployed release was removed."),
        [SppkgRules.ComponentAdded] = ("ComponentAdded", "A component was added."),
    };

    /// <summary>Render <paramref name="result"/> as a one-run SARIF log.</summary>
    public static SarifLog ToSarif(SppkgPolicyResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        var app = result.Inspection.App;
        var root = $"sppkg://{(app.ProductId ?? result.Inspection.FileName).Trim('{', '}').ToLowerInvariant()}";

        var used = result.Findings.Select(f => f.RuleId).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal);
        var rules = used.Select(id => Rules.TryGetValue(id, out var r)
            ? new SarifRule { Id = id, Name = r.Name, ShortDescription = new SarifMultiformatMessage { Text = r.Description } }
            : new SarifRule { Id = id }).ToList();

        var results = result.Findings.Select(f =>
        {
            var subject = f.Subject ?? f.ComponentId ?? "solution";
            var fqn = $"{root}/{subject}";
            return new SarifResult
            {
                RuleId = f.RuleId,
                Level = f.Severity switch
                {
                    SppkgSeverity.Error => SarifLevel.Error,
                    SppkgSeverity.Warning => SarifLevel.Warning,
                    _ => SarifLevel.Note,
                },
                Message = new SarifMessage { Text = f.Message },
                Locations = new[]
                {
                    new SarifLocation
                    {
                        LogicalLocations = new[]
                        {
                            new SarifLogicalLocation
                            {
                                Name = subject,
                                FullyQualifiedName = fqn,
                                Kind = f.ComponentId is null ? "spfxSolution" : "spfxComponent",
                            },
                        },
                    },
                },
                PartialFingerprints = new Dictionary<string, string> { ["sppkgSubject/v1"] = $"{f.RuleId}|{fqn}" },
            };
        }).ToList();

        return new SarifLog
        {
            Runs = new[]
            {
                new SarifRun
                {
                    Tool = new SarifTool
                    {
                        Driver = new SarifToolComponent
                        {
                            Name = ToolName,
                            Version = typeof(SppkgSarif).Assembly.GetName().Version?.ToString(3),
                            InformationUri = "https://github.com/tamp-build/tamp-spfx",
                            Rules = rules,
                        },
                    },
                    Results = results,
                },
            },
        };
    }

    /// <summary>Write <see cref="ToSarif"/> to <paramref name="path"/>.</summary>
    public static void WriteSarif(SppkgPolicyResult result, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, SarifWriter.Serialize(ToSarif(result)));
    }
}
