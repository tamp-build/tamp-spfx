using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp.Spfx;

/// <summary>
/// Everything a deploy decision needs to know about one <c>.sppkg</c>, read from the package itself rather
/// than from the source tree, so it describes the exact bytes that would be uploaded.
/// </summary>
public sealed record SppkgInspection
{
    /// <summary>The file name of the package.</summary>
    public required string FileName { get; init; }
    /// <summary>SHA-256 of the package, lowercase hex. Bind evidence and attestations to this.</summary>
    public required string Sha256 { get; init; }
    /// <summary>Size of the package in bytes.</summary>
    public required long SizeBytes { get; init; }
    /// <summary>The solution as <c>AppManifest.xml</c> declares it.</summary>
    public required SppkgApp App { get; init; }
    /// <summary>Features, from the root-level feature XML files.</summary>
    public IReadOnlyList<SppkgFeature> Features { get; init; } = Array.Empty<SppkgFeature>();
    /// <summary>Client-side components (web parts, extensions, ACEs, libraries), one per component id.</summary>
    public IReadOnlyList<SppkgComponent> Components { get; init; } = Array.Empty<SppkgComponent>();
    /// <summary>
    /// Extension registrations: <c>CustomAction</c> elements (activated per site by the feature) and
    /// <c>ClientSideComponentInstance</c> elements (applied tenant-wide when the admin deploys tenant-wide).
    /// </summary>
    public IReadOnlyList<SppkgExtensionRegistration> ExtensionRegistrations { get; init; } = Array.Empty<SppkgExtensionRegistration>();
    /// <summary>Files under <c>ClientSideAssets/</c>, with their digests.</summary>
    public IReadOnlyList<SppkgAsset> Assets { get; init; } = Array.Empty<SppkgAsset>();
    /// <summary>
    /// Every script the components load from outside the package, from the components' loader configuration:
    /// CDN externals and non-SharePoint module base URLs. Distinct and sorted.
    /// </summary>
    public IReadOnlyList<string> ExternalScriptUrls { get; init; } = Array.Empty<string>();
    /// <summary>Archive entries whose names are unsafe to extract (absolute, drive-qualified, or containing <c>..</c>).</summary>
    public IReadOnlyList<string> UnsafeEntries { get; init; } = Array.Empty<string>();
    /// <summary>Parts that couldn't be read, with the reason. A non-empty list means the inspection is incomplete.</summary>
    public IReadOnlyList<string> ReadProblems { get; init; } = Array.Empty<string>();

    /// <summary>True when the solution can be deployed tenant-wide and registers extensions that would then apply everywhere.</summary>
    [JsonIgnore]
    public bool HasTenantWideExtensions => App.SkipFeatureDeployment && ExtensionRegistrations.Any(r => r.Kind == SppkgRegistrationKind.ClientSideComponentInstance);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The inspection as indented camelCase JSON, for evidence and for diffing against the next release.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Write <see cref="ToJson"/> to <paramref name="path"/>, creating its directory.</summary>
    public void WriteJson(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Read an inspection written by <see cref="WriteJson"/> (typically the last deployed release's).</summary>
    public static SppkgInspection ReadJson(string path) =>
        JsonSerializer.Deserialize<SppkgInspection>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is not an sppkg inspection.");
}

/// <summary>The <c>App</c> element of <c>AppManifest.xml</c>.</summary>
public sealed record SppkgApp
{
    /// <summary><c>Name</c>.</summary>
    public string? Name { get; init; }
    /// <summary><c>ProductID</c>: the solution id.</summary>
    public string? ProductId { get; init; }
    /// <summary><c>Version</c>, as written.</summary>
    public string? Version { get; init; }
    /// <summary><c>SharePointMinVersion</c>.</summary>
    public string? SharePointMinVersion { get; init; }
    /// <summary><c>IsClientSideSolution</c>.</summary>
    public bool IsClientSideSolution { get; init; }
    /// <summary><c>SkipFeatureDeployment</c>: the admin can make it available to every site on deploy.</summary>
    public bool SkipFeatureDeployment { get; init; }
    /// <summary><c>WebApiPermissionRequests</c>: what the tenant admin is asked to grant.</summary>
    public IReadOnlyList<SppkgPermissionRequest> WebApiPermissionRequests { get; init; } = Array.Empty<SppkgPermissionRequest>();
}

/// <summary>One <c>WebApiPermissionRequest</c>.</summary>
public sealed record SppkgPermissionRequest(string ResourceId, string Scope, string? AppId = null, string? ReplyUrl = null)
{
    /// <summary>As a <see cref="SpfxPermissionRequest"/>, for matching against policy.</summary>
    public SpfxPermissionRequest ToRequest() => new(ResourceId, Scope);
}

/// <summary>A <c>Feature</c> element.</summary>
public sealed record SppkgFeature(string? Id, string? Title, string? Version, string? Scope);

/// <summary>A <c>ClientSideComponent</c>, with the interesting parts of its component manifest.</summary>
public sealed record SppkgComponent
{
    /// <summary>The component id.</summary>
    public required string Id { get; init; }
    /// <summary>The display name.</summary>
    public string? Name { get; init; }
    /// <summary><c>componentType</c>: <c>WebPart</c>, <c>Extension</c>, <c>AdaptiveCardExtension</c>, <c>Library</c>, …</summary>
    public string? ComponentType { get; init; }
    /// <summary><c>extensionType</c> for extensions (<c>ApplicationCustomizer</c>, <c>ListViewCommandSet</c>, …).</summary>
    public string? ExtensionType { get; init; }
    /// <summary><c>alias</c>.</summary>
    public string? Alias { get; init; }
    /// <summary>The component manifest's <c>version</c>.</summary>
    public string? Version { get; init; }
    /// <summary>
    /// True when the component requires custom script to be allowed on the site (the packager writes
    /// <c>ReturnIfCustomScriptDisabled="false"</c>). Custom script is off by default in SharePoint Online.
    /// </summary>
    public bool RequiresCustomScript { get; init; }
    /// <summary><c>supportedHosts</c>.</summary>
    public IReadOnlyList<string> SupportedHosts { get; init; } = Array.Empty<string>();
    /// <summary>Scripts this component loads from outside the package.</summary>
    public IReadOnlyList<SppkgExternalScript> ExternalScripts { get; init; } = Array.Empty<SppkgExternalScript>();
    /// <summary>Other SPFx components this one loads (framework libraries, React, …), by id and version.</summary>
    public IReadOnlyList<SppkgComponentDependency> Dependencies { get; init; } = Array.Empty<SppkgComponentDependency>();
}

/// <summary>A script loaded from a URL outside the package.</summary>
/// <param name="Module">The module name in the loader config, or <c>(module base URL)</c> for a CDN-hosted bundle.</param>
/// <param name="Url">The URL.</param>
public sealed record SppkgExternalScript(string Module, string Url);

/// <summary>An SPFx component dependency from the loader config.</summary>
public sealed record SppkgComponentDependency(string Module, string? Id, string? Version);

/// <summary>Which element registered an extension.</summary>
public enum SppkgRegistrationKind
{
    /// <summary>A <c>CustomAction</c>: active on a site once the feature is activated there.</summary>
    CustomAction,
    /// <summary>A <c>ClientSideComponentInstance</c>: applied to every site when the package is deployed tenant-wide.</summary>
    ClientSideComponentInstance,
}

/// <summary>An extension registration from an element manifest.</summary>
public sealed record SppkgExtensionRegistration
{
    /// <summary>The element it came from.</summary>
    public required SppkgRegistrationKind Kind { get; init; }
    /// <summary>The component it activates.</summary>
    public string? ComponentId { get; init; }
    /// <summary><c>Location</c>, e.g. <c>ClientSideExtension.ApplicationCustomizer</c>.</summary>
    public string? Location { get; init; }
    /// <summary><c>Title</c>.</summary>
    public string? Title { get; init; }
    /// <summary>The JSON properties passed to the component.</summary>
    public string? Properties { get; init; }
    /// <summary><c>RegistrationId</c> / <c>ListTemplateId</c>, when scoped to a list type.</summary>
    public string? ListScope { get; init; }
    /// <summary>The archive entry it was read from.</summary>
    public required string Entry { get; init; }
}

/// <summary>A client-side asset in the package.</summary>
public sealed record SppkgAsset(string Path, long SizeBytes, string Sha256);
