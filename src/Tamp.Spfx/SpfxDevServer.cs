using Tamp.Heft.V1;

namespace Tamp.Spfx;

/// <summary>
/// A running SPFx dev server: a <see cref="HeftDevServer"/> (<c>heft start</c>, ready once
/// <c>temp/build/manifests.js</c> is served) plus the project's <c>serve.json</c>, so a target can hand browser
/// automation the exact SharePoint page to open, e.g. a Form Customizer's edit form.
/// </summary>
/// <example>
/// <code>
/// Target FormEvidence => _ => _.Executes(async () =&gt;
/// {
///     await using var server = await Spfx.StartDevServerAsync(s =&gt; s.SetWorkingDirectory(Web));
///     var editForm = server.DebugUrl("myForm_EditForm", tenantDomain: "contoso.sharepoint.com");
///     await RunPlaywrightAsync(editForm);
/// });
/// </code>
/// </example>
public sealed class SpfxDevServer : IAsyncDisposable, IDisposable
{
    internal SpfxDevServer(HeftDevServer server, SpfxServeConfig serve)
    {
        Server = server;
        Serve = serve;
    }

    /// <summary>The underlying Heft dev server (URL, port, manifests URL, PID, state file).</summary>
    public HeftDevServer Server { get; }

    /// <summary>The project's <c>serve.json</c>, as read when the server started.</summary>
    public SpfxServeConfig Serve { get; }

    /// <summary>The <c>serveConfigurations</c> names, in file order.</summary>
    public IReadOnlyList<string> ConfigurationNames => Serve.Configurations.Select(c => c.Name).ToList();

    /// <summary>
    /// The SharePoint debug-page URL for <paramref name="configuration"/> (default: <c>default</c>), loading the
    /// components from this server. See <see cref="SpfxDebugUrl.Build"/>.
    /// </summary>
    /// <param name="configuration">A key of <c>serveConfigurations</c>; null for <c>default</c>.</param>
    /// <param name="tenantDomain">Replaces <c>{tenantDomain}</c>; default: <c>SPFX_SERVE_TENANT_DOMAIN</c>.</param>
    public string DebugUrl(string? configuration = null, string? tenantDomain = null) =>
        SpfxDebugUrl.Build(Serve, configuration, tenantDomain, Server.ManifestsUrl.ToString());

    /// <summary>Stop the server (kills its process tree and removes its state file).</summary>
    public void Dispose() => Server.Dispose();

    /// <inheritdoc cref="Dispose" />
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
