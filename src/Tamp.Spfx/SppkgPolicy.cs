namespace Tamp.Spfx;

/// <summary>How serious a policy finding is. Any <see cref="Error"/> fails the gate.</summary>
public enum SppkgSeverity
{
    /// <summary>Recorded as evidence; never fails the gate.</summary>
    Note,
    /// <summary>Worth a human look; doesn't fail the gate.</summary>
    Warning,
    /// <summary>Fails the gate.</summary>
    Error,
}

/// <summary>One policy finding.</summary>
/// <param name="RuleId">Stable rule id, e.g. <c>SPFX001</c>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What was found, in a sentence.</param>
/// <param name="ComponentId">The component it concerns, when it concerns one.</param>
/// <param name="Subject">
/// What the finding is about, stable across releases (<c>permission:Microsoft Graph/Sites.Read.All</c>,
/// <c>script:https://…</c>): the identity a dashboard uses to track one finding over time.
/// </param>
public sealed record SppkgFinding(string RuleId, SppkgSeverity Severity, string Message, string? ComponentId = null, string? Subject = null);

/// <summary>The outcome of evaluating a package against a policy.</summary>
public sealed record SppkgPolicyResult(SppkgInspection Inspection, IReadOnlyList<SppkgFinding> Findings)
{
    /// <summary>True when there are no <see cref="SppkgSeverity.Error"/> findings.</summary>
    public bool Passed => Findings.All(f => f.Severity != SppkgSeverity.Error);

    /// <summary>The findings that fail the gate.</summary>
    public IEnumerable<SppkgFinding> Errors => Findings.Where(f => f.Severity == SppkgSeverity.Error);

    /// <summary>Throw <see cref="SppkgPolicyException"/> unless <see cref="Passed"/>.</summary>
    public SppkgPolicyResult ThrowIfFailed() => Passed ? this : throw new SppkgPolicyException(this);
}

/// <summary>Thrown by <see cref="SppkgPolicyResult.ThrowIfFailed"/>; the message lists every failing finding.</summary>
public sealed class SppkgPolicyException(SppkgPolicyResult result) : Exception(
    $"{result.Inspection.FileName} failed the deploy policy:" + Environment.NewLine +
    string.Join(Environment.NewLine, result.Errors.Select(e => $"  {e.RuleId} {e.Message}")))
{
    /// <summary>The evaluation that failed.</summary>
    public SppkgPolicyResult Result { get; } = result;
}

/// <summary>
/// What a package may do before a machine is allowed to deploy it. The defaults are deliberately strict about
/// the things a reviewer would otherwise have to notice by hand, and permissive about the SPFx template defaults:
/// <list type="bullet">
///   <item>No API permission requests unless allow-listed (<see cref="AllowedPermissions"/>).</item>
///   <item>No scripts from outside the package unless their host is allow-listed (<see cref="AllowedScriptHosts"/>).</item>
///   <item>No component that needs custom script enabled.</item>
///   <item>Tenant-wide deployment is allowed (the template default), but tenant-wide extensions are flagged.</item>
/// </list>
/// </summary>
public sealed record SppkgPolicy
{
    /// <summary>Permission requests the package may make. Anything else is an error. Empty = none allowed.</summary>
    public IReadOnlyList<SpfxPermissionRequest> AllowedPermissions { get; init; } = Array.Empty<SpfxPermissionRequest>();

    /// <summary>Skip the permission allow-list entirely (any request is fine). Prefer listing them.</summary>
    public bool AllowAnyPermission { get; init; }

    /// <summary>
    /// Hosts components may load scripts from (<c>code.jquery.com</c>, <c>cdn.contoso.com</c>). A leading
    /// <c>*.</c> matches subdomains. Anything else is an error.
    /// </summary>
    public IReadOnlyList<string> AllowedScriptHosts { get; init; } = Array.Empty<string>();

    /// <summary>Allow plain <c>http://</c> script URLs. Off: they're an error even when the host is allowed.</summary>
    public bool AllowInsecureScriptUrls { get; init; }

    /// <summary>Allow <c>skipFeatureDeployment</c> (tenant-wide availability). On by default, as in the SPFx templates.</summary>
    public bool AllowTenantWideDeployment { get; init; } = true;

    /// <summary>
    /// Severity for extensions registered with <c>ClientSideComponentInstance</c> in a tenant-wide package:
    /// those run on every site in the tenant. A warning by default.
    /// </summary>
    public SppkgSeverity TenantWideExtensionSeverity { get; init; } = SppkgSeverity.Warning;

    /// <summary>Allow components that require custom script. Off by default.</summary>
    public bool AllowCustomScript { get; init; }

    /// <summary>The version the package must carry (what the build stamped). Null = don't check.</summary>
    public SpfxSolutionVersion? ExpectedVersion { get; init; }

    /// <summary>The solution id the package must carry, so a wrong package can't be deployed under a right name.</summary>
    public string? ExpectedSolutionId { get; init; }

    /// <summary>Largest acceptable package, in bytes. Null = no limit.</summary>
    public long? MaxPackageBytes { get; init; }

    /// <summary>
    /// Severity for permission requests that weren't in the previous release (when one is supplied). A warning
    /// by default; set <see cref="SppkgSeverity.Error"/> to make any new request a human decision even when
    /// it's allow-listed.
    /// </summary>
    public SppkgSeverity NewPermissionSeverity { get; init; } = SppkgSeverity.Warning;

    /// <summary>Evaluate <paramref name="current"/>, optionally against the last deployed release.</summary>
    public SppkgPolicyResult Evaluate(SppkgInspection current, SppkgInspection? previous = null)
    {
        if (current is null) throw new ArgumentNullException(nameof(current));
        var f = new List<SppkgFinding>();

        // SPFX001: the package could not be fully read, so nothing else here is reliable.
        foreach (var p in current.ReadProblems)
            f.Add(new SppkgFinding(SppkgRules.Unreadable, SppkgSeverity.Error, $"part of the package could not be read: {p}", Subject: "read:" + p));

        // SPFX002: entry names that would escape the extraction directory.
        foreach (var e in current.UnsafeEntries)
            f.Add(new SppkgFinding(SppkgRules.UnsafeEntry, SppkgSeverity.Error, $"archive entry '{e}' has an unsafe path.", Subject: "entry:" + e));

        // SPFX003: the version and identity the build meant to produce.
        var parsed = SpfxSolutionVersion.TryParse(current.App.Version, out var version);
        if (!parsed)
            f.Add(new SppkgFinding(SppkgRules.Version, SppkgSeverity.Error, $"AppManifest version '{current.App.Version}' is not a four-part solution version.", Subject: "version"));
        else if (ExpectedVersion is { } expected && version != expected)
            f.Add(new SppkgFinding(SppkgRules.Version, SppkgSeverity.Error, $"package version {version} is not the expected {expected}; it may be a stale build.", Subject: "version"));

        if (ExpectedSolutionId is { } id && !string.Equals(Guidish(id), Guidish(current.App.ProductId), StringComparison.OrdinalIgnoreCase))
            f.Add(new SppkgFinding(SppkgRules.Identity, SppkgSeverity.Error, $"solution id {current.App.ProductId} is not the expected {id}.", Subject: "identity"));

        if (MaxPackageBytes is { } max && current.SizeBytes > max)
            f.Add(new SppkgFinding(SppkgRules.Size, SppkgSeverity.Error, $"package is {current.SizeBytes} bytes, over the {max}-byte limit.", Subject: "size"));

        // SPFX010: API permission requests.
        foreach (var p in current.App.WebApiPermissionRequests)
        {
            var request = p.ToRequest();
            if (AllowAnyPermission)
                f.Add(new SppkgFinding(SppkgRules.Permission, SppkgSeverity.Note, $"requests {request} (any permission allowed by policy).", Subject: "permission:" + request));
            else if (!AllowedPermissions.Any(a => a.Matches(request)))
                f.Add(new SppkgFinding(SppkgRules.Permission, SppkgSeverity.Error, $"requests {request}, which the policy does not allow.", Subject: "permission:" + request));
            else
                f.Add(new SppkgFinding(SppkgRules.Permission, SppkgSeverity.Note, $"requests {request} (allowed).", Subject: "permission:" + request));
        }

        // SPFX011: tenant-wide availability, and extensions that would then run on every site.
        if (current.App.SkipFeatureDeployment && !AllowTenantWideDeployment)
            f.Add(new SppkgFinding(SppkgRules.TenantWide, SppkgSeverity.Error, "solution can be deployed tenant-wide (skipFeatureDeployment), which the policy does not allow.", Subject: "tenant-wide"));
        if (current.App.SkipFeatureDeployment)
            foreach (var r in current.ExtensionRegistrations.Where(r => r.Kind == SppkgRegistrationKind.ClientSideComponentInstance))
                f.Add(new SppkgFinding(SppkgRules.TenantWideExtension, TenantWideExtensionSeverity,
                    $"extension '{r.Title ?? r.ComponentId}' ({r.Location}) is registered to run on every site when deployed tenant-wide.", r.ComponentId, "tenant-wide-extension:" + r.ComponentId));

        // SPFX012: custom script.
        foreach (var c in current.Components.Where(c => c.RequiresCustomScript))
            f.Add(new SppkgFinding(SppkgRules.CustomScript, AllowCustomScript ? SppkgSeverity.Note : SppkgSeverity.Error,
                $"component '{c.Alias ?? c.Name ?? c.Id}' requires custom script to be enabled on the site.", c.Id, "custom-script:" + c.Id));

        // SPFX013: scripts from outside the package.
        foreach (var c in current.Components)
            foreach (var s in c.ExternalScripts)
            {
                var problem = ScriptProblem(s.Url);
                f.Add(problem is null
                    ? new SppkgFinding(SppkgRules.ExternalScript, SppkgSeverity.Note, $"component '{c.Alias ?? c.Id}' loads {s.Module} from {s.Url} (allowed host).", c.Id, "script:" + s.Url)
                    : new SppkgFinding(SppkgRules.ExternalScript, SppkgSeverity.Error, $"component '{c.Alias ?? c.Id}' loads {s.Module} from {s.Url}: {problem}.", c.Id, "script:" + s.Url));
            }

        if (previous is not null) Compare(current, previous, parsed ? version : null, f);

        return new SppkgPolicyResult(current, f);
    }

    // SPFX020-022: what changed since the last deployed release.
    private void Compare(SppkgInspection current, SppkgInspection previous, SpfxSolutionVersion? version, List<SppkgFinding> f)
    {
        if (version is { } v && SpfxSolutionVersion.TryParse(previous.App.Version, out var before) && v <= before)
            f.Add(new SppkgFinding(SppkgRules.VersionNotIncreased, SppkgSeverity.Error,
                $"version {v} is not greater than the deployed {before}; SharePoint would not treat it as an upgrade.", Subject: "version"));

        var oldPermissions = previous.App.WebApiPermissionRequests.Select(p => p.ToRequest()).ToList();
        foreach (var p in current.App.WebApiPermissionRequests.Select(p => p.ToRequest()).Where(p => !oldPermissions.Any(o => o.Matches(p))))
            f.Add(new SppkgFinding(SppkgRules.NewPermission, NewPermissionSeverity, $"requests {p}, which the deployed release did not.", Subject: "permission:" + p));

        if (!previous.App.SkipFeatureDeployment && current.App.SkipFeatureDeployment)
            f.Add(new SppkgFinding(SppkgRules.NewTenantWide, SppkgSeverity.Warning, "solution became tenant-wide deployable since the deployed release.", Subject: "tenant-wide"));

        var oldScripts = new HashSet<string>(previous.ExternalScriptUrls, StringComparer.OrdinalIgnoreCase);
        foreach (var url in current.ExternalScriptUrls.Where(u => !oldScripts.Contains(u)))
            f.Add(new SppkgFinding(SppkgRules.NewExternalScript, SppkgSeverity.Warning, $"loads {url}, which the deployed release did not.", Subject: "script:" + url));

        var oldComponents = new HashSet<string>(previous.Components.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var newComponents = new HashSet<string>(current.Components.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in previous.Components.Where(c => !newComponents.Contains(c.Id)))
            f.Add(new SppkgFinding(SppkgRules.ComponentRemoved, SppkgSeverity.Warning,
                $"component '{gone.Alias ?? gone.Id}' is in the deployed release but not this one; pages using it will break.", gone.Id, "component:" + gone.Id));
        foreach (var added in current.Components.Where(c => !oldComponents.Contains(c.Id)))
            f.Add(new SppkgFinding(SppkgRules.ComponentAdded, SppkgSeverity.Note, $"adds component '{added.Alias ?? added.Id}'.", added.Id, "component:" + added.Id));
    }

    private string? ScriptProblem(string url)
    {
        var normalized = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return "not a valid URL";
        if (uri.Scheme == Uri.UriSchemeHttp && !AllowInsecureScriptUrls) return "plain http is not allowed";
        return HostAllowed(uri.Host) ? null : $"host {uri.Host} is not allow-listed";
    }

    private bool HostAllowed(string host) => AllowedScriptHosts.Any(allowed =>
        allowed.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase));

    private static string Guidish(string? id) => (id ?? "").Trim().Trim('{', '}');
}

/// <summary>The rule ids <see cref="SppkgPolicy"/> reports.</summary>
public static class SppkgRules
{
    /// <summary>Part of the package couldn't be read.</summary>
    public const string Unreadable = "SPFX001";
    /// <summary>An archive entry has an unsafe path.</summary>
    public const string UnsafeEntry = "SPFX002";
    /// <summary>The version is malformed or not the expected one.</summary>
    public const string Version = "SPFX003";
    /// <summary>The solution id is not the expected one.</summary>
    public const string Identity = "SPFX004";
    /// <summary>The package is over the size limit.</summary>
    public const string Size = "SPFX005";
    /// <summary>An API permission request.</summary>
    public const string Permission = "SPFX010";
    /// <summary>Tenant-wide deployment.</summary>
    public const string TenantWide = "SPFX011";
    /// <summary>An extension that runs on every site when deployed tenant-wide.</summary>
    public const string TenantWideExtension = "SPFX014";
    /// <summary>A component that requires custom script.</summary>
    public const string CustomScript = "SPFX012";
    /// <summary>A script loaded from outside the package.</summary>
    public const string ExternalScript = "SPFX013";
    /// <summary>The version didn't increase over the deployed release.</summary>
    public const string VersionNotIncreased = "SPFX020";
    /// <summary>A permission request the deployed release didn't make.</summary>
    public const string NewPermission = "SPFX021";
    /// <summary>The solution became tenant-wide deployable.</summary>
    public const string NewTenantWide = "SPFX022";
    /// <summary>A script origin the deployed release didn't use.</summary>
    public const string NewExternalScript = "SPFX023";
    /// <summary>A component the deployed release had was removed.</summary>
    public const string ComponentRemoved = "SPFX024";
    /// <summary>A component was added.</summary>
    public const string ComponentAdded = "SPFX025";
}
