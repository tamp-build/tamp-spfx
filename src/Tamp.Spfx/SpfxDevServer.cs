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
/// <remarks>
/// The process lifecycle is <see cref="HeftDevServer"/>'s (built on Tamp.Core's <c>ManagedProcess</c>); this type adds
/// nothing to it. Inside a <c>tamp</c> run, Ctrl+C / SIGTERM / SIGHUP cancel the build's token, which the start honours
/// as well as the one passed to <see cref="Spfx.StartDevServerAsync"/>; a server still running when the build ends is
/// stopped by Tamp.Core's backstop, so one started in a target can serve later targets but never outlives the build.
/// Every stop emits <c>service.stopped</c> with its <see cref="StopReason"/>.
/// </remarks>
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

    /// <summary>True once the Heft process has exited (stopped, or on its own).</summary>
    public bool HasExited => Server.HasExited;

    /// <summary>True once a stop (dispose, an interrupt or the end-of-build backstop) has begun.</summary>
    public bool IsStopping => Server.IsStopping;

    /// <summary>
    /// Heft's exit code, once it has exited. Non-zero after a deliberate stop too (the tree is killed), so read
    /// <see cref="StopReason"/> to tell a stop from a failure.
    /// </summary>
    public int? ExitCode => Server.ExitCode;

    /// <summary>
    /// Why the server stopped, once it has: <c>requested</c> (the server was disposed),
    /// <c>signal</c> (Ctrl+C, SIGTERM, SIGHUP), <c>exited</c> (Heft exited on its own) or <c>timeout</c>; see
    /// <see cref="Tamp.ServiceStopReason"/>. Null while it is running.
    /// </summary>
    public string? StopReason => Server.StopReason;

    /// <summary>Completes when the Heft process has exited and its stop has been recorded.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Server.WaitForExitAsync(cancellationToken);

    /// <summary>
    /// Stop the server: kills its process tree and removes its state file (stop reason <c>requested</c>). Safe to
    /// call more than once, and after the server has already been stopped.
    /// </summary>
    public void Dispose() => Server.Dispose();

    /// <inheritdoc cref="Dispose" />
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}
