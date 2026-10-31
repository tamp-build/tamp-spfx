using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace Tamp.Spfx.Tests;

public sealed class SppkgInspectorTests
{
    private static readonly SppkgInspection Probe = SppkgInspector.Inspect(Fixture.Sppkg);

    [Fact]
    public void Digest_And_Size_Are_Of_The_File()
    {
        var bytes = File.ReadAllBytes(Fixture.Sppkg);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Probe.Sha256);
        Assert.Equal(bytes.LongLength, Probe.SizeBytes);
        Assert.Equal("spfx-probe.sppkg", Probe.FileName);
    }

    [Fact]
    public void Reads_The_App_Manifest()
    {
        var app = Probe.App;
        Assert.Equal("spfx-probe-client-side-solution", app.Name);
        Assert.Equal(Fixture.SolutionId, app.ProductId);
        Assert.Equal("1.2.3.4", app.Version);
        Assert.Equal("16.0.0.0", app.SharePointMinVersion);
        Assert.True(app.IsClientSideSolution);
        Assert.True(app.SkipFeatureDeployment);
        Assert.Equal(new[] { ("Microsoft Graph", "Sites.Read.All"), ("Contoso API", "user_impersonation") },
            app.WebApiPermissionRequests.Select(p => (p.ResourceId, p.Scope)));
    }

    [Fact]
    public void Components_Are_Listed_Once_Each_Despite_Repeating_Per_Feature()
    {
        Assert.Equal(new[] { Fixture.WebPartId, Fixture.ExtensionId }.OrderBy(x => x), Probe.Components.Select(c => c.Id));
    }

    [Fact]
    public void Reads_The_Web_Part()
    {
        var wp = Probe.Components.Single(c => c.Id == Fixture.WebPartId);
        Assert.Equal("WebPart", wp.ComponentType);
        Assert.Equal("Hello Probe Web Part", wp.Name);
        Assert.Equal("Hello ProbeWebPart", wp.Alias);
        Assert.Equal("0.0.1", wp.Version);
        Assert.True(wp.RequiresCustomScript);
        Assert.Contains("SharePointWebPart", wp.SupportedHosts);
        Assert.Equal(new[] { new SppkgExternalScript("jquery", "https://code.jquery.com/jquery-3.7.1.min.js") }, wp.ExternalScripts);
        Assert.Contains(wp.Dependencies, d => d.Module == "react" && d.Version == "17.0.1");
        Assert.Contains(wp.Dependencies, d => d.Module == "@microsoft/sp-core-library" && d.Version == "1.24.0");
    }

    [Fact]
    public void Reads_The_Extension_And_Its_Registrations()
    {
        var ext = Probe.Components.Single(c => c.Id == Fixture.ExtensionId);
        Assert.Equal("Extension", ext.ComponentType);
        Assert.Equal("ApplicationCustomizer", ext.ExtensionType);
        Assert.False(ext.RequiresCustomScript);
        Assert.Empty(ext.ExternalScripts);

        var ca = Probe.ExtensionRegistrations.Single(r => r.Kind == SppkgRegistrationKind.CustomAction);
        Assert.Equal(Fixture.ExtensionId, ca.ComponentId);
        Assert.Equal("ClientSideExtension.ApplicationCustomizer", ca.Location);
        Assert.Equal("""{"testMessage":"Test message"}""", ca.Properties);
        Assert.EndsWith("elements.xml", ca.Entry);

        var ci = Probe.ExtensionRegistrations.Single(r => r.Kind == SppkgRegistrationKind.ClientSideComponentInstance);
        Assert.Equal(Fixture.ExtensionId, ci.ComponentId);
        Assert.EndsWith("ClientSideInstance.xml", ci.Entry);
        Assert.True(Probe.HasTenantWideExtensions);
    }

    [Fact]
    public void Reads_Features_And_Assets()
    {
        Assert.Equal(3, Probe.Features.Count);   // two solution features + the client-side assets feature
        Assert.Contains(Probe.Features, f => f.Title == "Client Side Assets");
        Assert.All(Probe.Features, f => Assert.Equal("Web", f.Scope));

        Assert.NotEmpty(Probe.Assets);
        Assert.All(Probe.Assets, a =>
        {
            Assert.StartsWith("ClientSideAssets/", a.Path);
            Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
            Assert.True(a.SizeBytes > 0);
        });
        Assert.Contains(Probe.Assets, a => a.Path.EndsWith(".js", StringComparison.Ordinal) && a.Path.Contains("hello-probe-web-part"));
    }

    [Fact]
    public void Aggregates_External_Scripts_And_Has_No_Problems()
    {
        Assert.Equal(new[] { "https://code.jquery.com/jquery-3.7.1.min.js" }, Probe.ExternalScriptUrls);
        Assert.Empty(Probe.UnsafeEntries);
        Assert.Empty(Probe.ReadProblems);
    }

    [Fact]
    public void Json_Round_Trips()
    {
        using var t = new TempDir();
        var path = t.File(Path.Combine("evidence", "sppkg.json"));
        Probe.WriteJson(path);

        var text = File.ReadAllText(path);
        Assert.Contains("\"sha256\":", text);
        Assert.Contains("\"kind\": \"clientSideComponentInstance\"", text);
        Assert.DoesNotContain("hasTenantWideExtensions", text);

        var back = SppkgInspection.ReadJson(path);
        Assert.Equal(Probe.Sha256, back.Sha256);
        Assert.Equal(Probe.App.WebApiPermissionRequests, back.App.WebApiPermissionRequests);
        Assert.Equal(Probe.Components.Select(c => c.Id), back.Components.Select(c => c.Id));
        Assert.Equal(Probe.ExtensionRegistrations.Select(r => r.Kind), back.ExtensionRegistrations.Select(r => r.Kind));
        Assert.Equal(Probe.ExternalScriptUrls, back.ExternalScriptUrls);
    }

    [Fact]
    public void ReadJson_Rejects_A_Null_Document()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("x.json"), "null");
        Assert.Throws<InvalidDataException>(() => SppkgInspection.ReadJson(t.File("x.json")));
    }

    // ---- hostile and unusual packages ----

    [Fact]
    public void Not_A_Zip_Is_A_Clear_Error()
    {
        using var ms = new MemoryStream("this is not a zip"u8.ToArray());
        var ex = Assert.Throws<InvalidDataException>(() => SppkgInspector.Inspect(ms, "bad.sppkg"));
        Assert.Contains("not a zip", ex.Message);
    }

    [Fact]
    public void A_Zip_Without_An_App_Manifest_Is_Not_A_Package()
    {
        using var zip = Fixture.Zip(("readme.txt", "hi"));
        var ex = Assert.Throws<InvalidDataException>(() => SppkgInspector.Inspect(zip, "x.sppkg"));
        Assert.Contains("AppManifest.xml", ex.Message);
    }

    [Fact]
    public void An_App_Manifest_With_The_Wrong_Root_Is_Refused()
    {
        using var zip = Fixture.Zip(("AppManifest.xml", "<NotApp/>"));
        Assert.Throws<InvalidDataException>(() => SppkgInspector.Inspect(zip, "x.sppkg"));
    }

    [Fact]
    public void Unsafe_Entry_Names_Are_Reported_Not_Followed()
    {
        using var zip = Fixture.Zip(
            ("AppManifest.xml", Fixture.AppManifest()),
            ("../../evil.js", "x"),
            ("ok/../../evil2.js", "x"),
            ("/abs.js", "x"),
            ("C:/drive.js", "x"),
            ("fine/name.js", "x"));

        var r = SppkgInspector.Inspect(zip, "x.sppkg");

        Assert.Equal(new[] { "../../evil.js", "/abs.js", "C:/drive.js", "ok/../../evil2.js" }.OrderBy(x => x, StringComparer.Ordinal), r.UnsafeEntries);
    }

    [Fact]
    public void A_Dtd_Is_Refused_So_Entity_Tricks_Cannot_Run()
    {
        const string xxe = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><Elements xmlns="http://schemas.microsoft.com/sharepoint/">&e;</Elements>""";
        using var zip = Fixture.Zip(("AppManifest.xml", Fixture.AppManifest()), ("f/elements.xml", xxe));

        var r = SppkgInspector.Inspect(zip, "x.sppkg");

        var problem = Assert.Single(r.ReadProblems);
        Assert.StartsWith("f/elements.xml:", problem);
    }

    [Fact]
    public void An_Entry_That_Decompresses_Past_The_Limit_Is_Stopped()
    {
        var big = new string('a', 200_000);
        using var zip = Fixture.Zip(("AppManifest.xml", Fixture.AppManifest()), ("ClientSideAssets/big.js", big), ("f/x.xml", "<a>" + big + "</a>"));
        var opts = new SppkgInspectorOptions { MaxEntryBytes = 10_000 };

        var r = SppkgInspector.Inspect(zip, "x.sppkg", opts);

        Assert.Equal(2, r.ReadProblems.Count);
        Assert.All(r.ReadProblems, p => Assert.Contains("limit", p));
    }

    [Fact]
    public void Package_And_Entry_Count_Limits_Apply()
    {
        using (var zip = Fixture.Zip(("AppManifest.xml", Fixture.AppManifest())))
            Assert.Throws<InvalidDataException>(() => SppkgInspector.Inspect(zip, "x", new SppkgInspectorOptions { MaxPackageBytes = 10 }));
        using (var zip = Fixture.Zip(("AppManifest.xml", Fixture.AppManifest()), ("a", ""), ("b", "")))
            Assert.Throws<InvalidDataException>(() => SppkgInspector.Inspect(zip, "x", new SppkgInspectorOptions { MaxEntries = 2 }));
    }

    [Fact]
    public void A_Cdn_Hosted_Bundle_And_Localized_Paths_Count_As_External()
    {
        const string manifest = """
            { "id": "c1", "alias": "Cdn", "componentType": "WebPart", "version": "1.0.0",
              "loaderConfig": {
                "internalModuleBaseUrls": [ "https://cdn.contoso.com/spfx/" ],
                "scriptResources": {
                  "strings": { "type": "localizedPath", "defaultPath": "https://cdn.contoso.com/s/en.js", "paths": { "de-de": "//cdn.contoso.com/s/de.js", "fr-fr": "s/fr.js" } },
                  "local": { "type": "path", "path": "bundle.js" },
                  "insecure": { "type": "path", "path": "http://old.example.com/x.js" }
                } } }
            """;
        using var zip = Fixture.Zip(("AppManifest.xml", Fixture.AppManifest()), ("f/WebPart_c1.xml", Fixture.Component("c1", manifest)));

        var r = SppkgInspector.Inspect(zip, "x.sppkg");

        var c = Assert.Single(r.Components);
        Assert.Equal(new[]
        {
            "(module base URL)|https://cdn.contoso.com/spfx/",
            "strings|https://cdn.contoso.com/s/en.js",
            "strings|//cdn.contoso.com/s/de.js",
            "insecure|http://old.example.com/x.js",
        }, c.ExternalScripts.Select(s => s.Module + "|" + s.Url));
        Assert.Equal(4, r.ExternalScriptUrls.Count);
    }

    [Fact]
    public void A_Broken_Component_Manifest_Or_Missing_Id_Is_A_Problem_Not_A_Crash()
    {
        using var zip = Fixture.Zip(
            ("AppManifest.xml", Fixture.AppManifest()),
            ("f/a.xml", Fixture.Component("c1", "{ not json")),
            ("f/b.xml", """<Elements xmlns="http://schemas.microsoft.com/sharepoint/"><ClientSideComponent Name="no id"/></Elements>"""));

        var r = SppkgInspector.Inspect(zip, "x.sppkg");

        Assert.Equal(2, r.ReadProblems.Count);
        Assert.Equal("c1", Assert.Single(r.Components).Id);
    }

    [Fact]
    public void Skips_Package_Plumbing_Parts()
    {
        using var zip = Fixture.Zip(
            ("AppManifest.xml", Fixture.AppManifest()),
            ("[Content_Types].xml", "<broken"),
            ("_rels/.rels", "<broken"),
            ("_rels/AppManifest.xml.rels", "<broken"),
            ("feature_x.xml.config.xml", "<broken"),
            ("notes.txt", "<broken"),
            ("dir/", ""));

        var r = SppkgInspector.Inspect(zip, "x.sppkg");

        Assert.Empty(r.ReadProblems);
        Assert.Empty(r.Components);
        Assert.False(r.HasTenantWideExtensions);
    }

    [Fact]
    public void Argument_Validation()
    {
        Assert.Throws<ArgumentException>(() => SppkgInspector.Inspect(" "));
        Assert.Throws<ArgumentNullException>(() => SppkgInspector.Inspect(null!, "x"));
        using var gz = new GZipStream(new MemoryStream(), CompressionMode.Compress);
        Assert.Throws<ArgumentException>(() => SppkgInspector.Inspect(gz, "x"));
    }

    [Theory]
    [InlineData("https://a", true)]
    [InlineData("HTTP://a", true)]
    [InlineData("//a/b.js", true)]
    [InlineData("bundle.js", false)]
    [InlineData("HTTPS://SPCLIENTSIDEASSETLIBRARY/", true)]
    public void Remote_Url_Detection(string url, bool remote) => Assert.Equal(remote, SppkgInspector.IsRemote(url));
}
