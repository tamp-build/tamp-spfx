using System.Globalization;
using System.Text.Json;

namespace Tamp.Spfx;

/// <summary>
/// One entry of <c>serveConfigurations</c> in <c>config/serve.json</c>: the SharePoint page to debug on and the
/// extensions to load there. The customizer sections are kept as JSON, exactly as written, because the debug URL
/// carries them verbatim.
/// </summary>
public sealed record SpfxServeConfiguration
{
    /// <summary>The configuration's key, e.g. <c>default</c> or <c>myForm_EditForm</c>.</summary>
    public required string Name { get; init; }

    /// <summary><c>pageUrl</c>: the page to open, often with a <c>{tenantDomain}</c> placeholder.</summary>
    public string? PageUrl { get; init; }

    /// <summary><c>customActions</c> (application customizers, command sets), when present.</summary>
    public JsonElement? CustomActions { get; init; }

    /// <summary><c>fieldCustomizers</c>, when present.</summary>
    public JsonElement? FieldCustomizers { get; init; }

    /// <summary><c>formCustomizer</c>, when present.</summary>
    public JsonElement? FormCustomizer { get; init; }
}

/// <summary>
/// The parts of an SPFx project's <c>config/serve.json</c> that decide where <c>heft start</c> serves and which
/// debug page it opens, read the way the SPFx Heft rig reads them (verified against
/// <c>@microsoft/spfx-heft-plugins</c> 1.24.1): <c>hostname</c> (else <c>ipAddress</c>, else <c>localhost</c>),
/// <c>port</c> (default 4321), <c>https</c>, <c>initialPage</c> and <c>serveConfigurations</c>.
/// </summary>
public sealed record SpfxServeConfig
{
    /// <summary>The rig's default port.</summary>
    public const int DefaultPort = 4321;

    /// <summary>The environment variable the rig reads to replace <c>{tenantDomain}</c>.</summary>
    public const string TenantDomainVariable = "SPFX_SERVE_TENANT_DOMAIN";

    /// <summary>The host the dev server listens on, as written (it may contain <c>{tenantDomain}</c>).</summary>
    public string Hostname { get; init; } = "localhost";

    /// <summary>The dev server's port.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Whether the dev server uses HTTPS.</summary>
    public bool Https { get; init; }

    /// <summary><c>initialPage</c>: the page to open when no serve configuration names one.</summary>
    public string? InitialPage { get; init; }

    /// <summary><c>serveConfigurations</c>, in file order.</summary>
    public IReadOnlyList<SpfxServeConfiguration> Configurations { get; init; } = Array.Empty<SpfxServeConfiguration>();

    /// <summary>The <c>serve.json</c> this came from, or null for defaults.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The configuration called <paramref name="name"/>, or null.</summary>
    public SpfxServeConfiguration? Find(string name) => Configurations.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// The manifests file the debug loader is pointed at, as the rig computes it:
    /// <c>{https|http}://{hostname}:{port}/temp/build/manifests.js</c>, with <c>{tenantDomain}</c> in the hostname
    /// replaced by <paramref name="tenantDomain"/>.
    /// </summary>
    public string ManifestsUrl(string? tenantDomain = null)
    {
        var host = SpfxDebugUrl.ReplaceTenantDomain(Hostname, tenantDomain);
        return string.Create(CultureInfo.InvariantCulture, $"{(Https ? "https" : "http")}://{host}:{Port}/temp/build/manifests.js");
    }

    /// <summary>
    /// Read <c>config/serve.json</c> under <paramref name="projectDirectory"/>. A missing file gives the rig's
    /// defaults; a file that isn't valid JSON throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static SpfxServeConfig Load(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory)) throw new ArgumentException("projectDirectory is required.", nameof(projectDirectory));
        var path = Path.Combine(projectDirectory, "config", "serve.json");
        if (!File.Exists(path)) return new SpfxServeConfig();
        using var doc = Jsonc.ParseFile(path);
        return From(doc.RootElement, Path.GetFullPath(path));
    }

    /// <summary>Parse <c>serve.json</c> text (comments and trailing commas allowed).</summary>
    public static SpfxServeConfig Parse(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        try
        {
            using var doc = JsonDocument.Parse(json, Jsonc.DocumentOptions);
            return From(doc.RootElement, sourcePath: null);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"serve.json is not valid JSON: {ex.Message}", ex);
        }
    }

    private static SpfxServeConfig From(JsonElement root, string? sourcePath)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{sourcePath ?? "serve.json"} is not a JSON object.");

        var port = DefaultPort;
        if (root.TryGetProperty("port", out var p) && (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out port) || port is < 1 or > 65535))
            throw new InvalidDataException($"{sourcePath ?? "serve.json"}: 'port' must be a number from 1 to 65535.");

        var configurations = new List<SpfxServeConfiguration>();
        if (root.GetObject("serveConfigurations") is { } all)
        {
            foreach (var entry in all.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                configurations.Add(new SpfxServeConfiguration
                {
                    Name = entry.Name,
                    PageUrl = entry.Value.GetString("pageUrl"),
                    CustomActions = Section(entry.Value, "customActions"),
                    FieldCustomizers = Section(entry.Value, "fieldCustomizers"),
                    FormCustomizer = Section(entry.Value, "formCustomizer"),
                });
            }
        }

        return new SpfxServeConfig
        {
            // The rig: { hostname: ipAddress ?? 'localhost', port: 4321, ...serve }.
            Hostname = root.GetString("hostname") ?? root.GetString("ipAddress") ?? "localhost",
            Port = port,
            Https = root.GetBool("https") ?? false,
            InitialPage = root.GetString("initialPage"),
            Configurations = configurations,
            SourcePath = sourcePath,
        };
    }

    // Absent and JSON null are "not configured"; anything else is kept (the rig only checks truthiness).
    private static JsonElement? Section(JsonElement configuration, string name) =>
        configuration.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
}
