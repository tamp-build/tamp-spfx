using System.Net;
using System.Net.Sockets;
using Tamp.Heft.V1;
using Xunit;

namespace Tamp.Spfx.Tests;

/// <summary>
/// <see cref="Spfx.StartDevServerAsync"/> end to end against a fake <c>@rushstack/heft</c> (a small Node server
/// that serves <c>temp/build/manifests.js</c>), and <see cref="Spfx.TrustDevCertificate"/>'s command line. Needs
/// <c>node</c> on PATH, as any SPFx project does; returns early without it.
/// </summary>
public sealed class SpfxDevServerTests
{
    private const string FakeHeft = """
        #!/usr/bin/env node
        const http = require('http');
        console.log('fake heft ' + process.argv.slice(2).join(' '));
        http.createServer((q, r) => { r.writeHead(q.url === '/temp/build/manifests.js' ? 200 : 404); r.end(); })
          .listen(+process.env.FAKE_PORT);
        """;

    private static bool HaveNode => FindOnPath("node") is not null;

    [Fact]
    public async Task Starts_Heft_And_Builds_Debug_Urls_From_The_Running_Server()
    {
        if (!HaveNode) return;
        using var t = new TempDir();
        var port = FreePort();
        var heft = Path.Combine(t.Path, "node_modules", "@rushstack", "heft");
        Directory.CreateDirectory(Path.Combine(heft, "bin"));
        File.WriteAllText(Path.Combine(heft, "package.json"), """{ "name": "@rushstack/heft", "version": "1.3.2", "bin": { "heft": "./bin/heft" } }""");
        File.WriteAllText(Path.Combine(heft, "bin", "heft"), FakeHeft.Replace("\r\n", "\n", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(t.Path, "config"));
        File.WriteAllText(Path.Combine(t.Path, "config", "serve.json"), $$"""
            { "port": {{port}}, "https": false,
              "serveConfigurations": {
                "default": { "pageUrl": "https://{tenantDomain}/_layouts/workbench.aspx" },
                "myForm_EditForm": { "pageUrl": "https://{tenantDomain}/_layouts/15/SPListForm.aspx",
                  "formCustomizer": { "componentId": "2875713c-2c49-4baf-9725-fcc44d531e58", "PageType": 6, "RootFolder": "/sites/s/Lists/L", "ID": 1 } } } }
            """);
        var output = TextWriter.Synchronized(new StringWriter());

        await using (var server = await Spfx.StartDevServerAsync(s => s
            .SetWorkingDirectory(t.Path)
            .SetStatePath(Path.Combine(t.Path, ".tamp", "state.json"))
            .SetOutput(output)
            .SetPollInterval(TimeSpan.FromMilliseconds(100))
            .SetReadyTimeout(TimeSpan.FromSeconds(30))
            .SetEnvironmentVariable("FAKE_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture))))
        {
            Assert.Equal(new[] { "default", "myForm_EditForm" }, server.ConfigurationNames);
            Assert.Equal(port, server.Server.Port);

            var manifests = Uri.EscapeDataString($"http://localhost:{port}/temp/build/manifests.js");
            Assert.Equal($"https://contoso.sharepoint.com/_layouts/workbench.aspx?debugManifestsFile={manifests}&debug=true&noredir=true",
                server.DebugUrl(tenantDomain: "contoso.sharepoint.com"));
            Assert.Equal($"https://contoso.sharepoint.com/_layouts/15/SPListForm.aspx?debugManifestsFile={manifests}&noredir=true&loadSPFX=true" +
                         "&componentId=2875713c-2c49-4baf-9725-fcc44d531e58&PageType=6&RootFolder=%2Fsites%2Fs%2FLists%2FL&ID=1",
                server.DebugUrl("myForm_EditForm", "contoso.sharepoint.com"));

            // The debug URL is computed on demand; it never goes into the state file.
            var state = File.ReadAllText(server.Server.StatePath);
            Assert.DoesNotContain("debugManifestsFile", state, StringComparison.Ordinal);
            Assert.DoesNotContain("contoso", state, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(t.Path, ".tamp", "state.json")));
    }

    [Fact]
    public void Trust_Dev_Certificate_Runs_The_Rig_Phase()
    {
        var plan = Spfx.TrustDevCertificate(s => s.SetBinary(new HeftBinaryResolution
        {
            Executable = "/usr/bin/node",
            PrefixArguments = new[] { "/w/node_modules/@rushstack/heft/bin/heft" },
            Source = HeftResolutionSource.Explicit,
        }));
        Assert.Equal(new[] { "trust-dev-cert" }, plan.Arguments.Skip(1));
    }

    [Fact]
    public async Task Gulp_Projects_Are_Refused_Before_Anything_Starts()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("config"));
        File.WriteAllText(t.File("package.json"), """{ "dependencies": { "@microsoft/sp-core-library": "1.20.0" }, "devDependencies": { "@microsoft/sp-build-web": "1.20.2" } }""");
        File.WriteAllText(t.File(Path.Combine("config", "package-solution.json")), """{ "solution": { "version": "1.0.0.0" } }""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Spfx.StartDevServerAsync(s => s.SetWorkingDirectory(t.Path)));
        Assert.Contains("gulp", ex.Message, StringComparison.Ordinal);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static string? FindOnPath(string name)
    {
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : new[] { name };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir.Trim('"'), n))) return Path.Combine(dir.Trim('"'), n);
        return null;
    }
}
