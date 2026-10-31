using System.Text;
using Xunit;

namespace Tamp.Spfx.Tests;

public sealed class PackageSolutionEditorTests
{
    private const string Jsonc = """
        {
          "$schema": "https://developer.microsoft.com/json-schemas/spfx-build/package-solution.schema.json",
          // Stamped by the build; don't edit by hand.
          "solution": {
            "name": "contoso-client-side-solution",
            "id": "67271b26-cf99-4d77-a709-4faee6ca69f9",
            "version": "1.0.0.0", /* the solution version */
            "developer": { "name": "", "version": "not-me" },
            "features": [
              { "title": "A", "id": "x", "version": "1.0.0.0" },
              { "title": "B", "id": "y", "version": "0.9.0.0", },
            ],
          },
          "paths": { "zippedPackage": "solution/contoso.sppkg", "version": "also-not-me" }
        }
        """;

    private static readonly SpfxSolutionVersion V = SpfxSolutionVersion.Parse("2.3.4.5");

    private static string Edit(string json, bool features = true) =>
        Encoding.UTF8.GetString(PackageSolutionEditor.SetVersion(Encoding.UTF8.GetBytes(json), V, features)!);

    [Fact]
    public void Changes_Only_The_Version_Literals()
    {
        var expected = Jsonc
            .Replace("\"version\": \"1.0.0.0\", /*", "\"version\": \"2.3.4.5\", /*")
            .Replace("\"id\": \"x\", \"version\": \"1.0.0.0\"", "\"id\": \"x\", \"version\": \"2.3.4.5\"")
            .Replace("\"version\": \"0.9.0.0\"", "\"version\": \"2.3.4.5\"");
        Assert.Equal(expected, Edit(Jsonc));
    }

    [Fact]
    public void Comments_Trailing_Commas_And_Unrelated_Versions_Survive()
    {
        var edited = Edit(Jsonc);
        Assert.Contains("// Stamped by the build; don't edit by hand.", edited);
        Assert.Contains("/* the solution version */", edited);
        Assert.Contains("\"version\": \"not-me\"", edited);
        Assert.Contains("\"version\": \"also-not-me\"", edited);
        Assert.Contains("\"version\": \"2.3.4.5\", },", edited);
    }

    [Fact]
    public void Feature_Versions_Can_Be_Left_Alone()
    {
        var edited = Edit(Jsonc, features: false);
        Assert.Contains("\"version\": \"2.3.4.5\", /*", edited);
        Assert.Contains("\"id\": \"x\", \"version\": \"1.0.0.0\"", edited);
        Assert.Contains("\"version\": \"0.9.0.0\"", edited);
    }

    [Fact]
    public void Is_Idempotent()
    {
        var once = Encoding.UTF8.GetBytes(Edit(Jsonc));
        Assert.Null(PackageSolutionEditor.SetVersion(once, V, includeFeatures: true));
    }

    [Fact]
    public void Preserves_A_Byte_Order_Mark_And_CRLF()
    {
        var crlf = Jsonc.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(crlf)).ToArray();
        var edited = PackageSolutionEditor.SetVersion(bytes, V, true)!;
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, edited.Take(3));
        var text = Encoding.UTF8.GetString(edited, 3, edited.Length - 3);
        Assert.Contains("\"version\": \"2.3.4.5\"", text);
        Assert.Equal(crlf.Split("\r\n").Length, text.Split("\r\n").Length);
    }

    [Fact]
    public void Handles_Escaped_Strings_Before_The_Target()
    {
        const string json = """{ "solution": { "name": "a \"quoted\" é name", "version": "1.0.0.0" } }""";
        Assert.Equal("""{ "solution": { "name": "a \"quoted\" é name", "version": "2.3.4.5" } }""", Edit(json));
    }

    [Fact]
    public void A_File_Without_A_Solution_Version_Is_Refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Edit("""{ "solution": { "name": "x" } }"""));
        Assert.Contains("solution.version", ex.Message);
        Assert.Throws<InvalidDataException>(() => Edit("""{ "version": "1.0.0.0" }"""));
        Assert.Throws<InvalidDataException>(() => Edit("""{ "solution": { "version": 1 } }"""));
    }

    [Fact]
    public void Malformed_Json_Is_Refused_Not_Half_Edited() =>
        Assert.Throws<InvalidDataException>(() => Edit("""{ "solution": { "version": "1.0.0.0" """));

    [Fact]
    public void Rewrites_The_File_On_Disk_And_Reports_Whether_It_Changed()
    {
        using var t = Fixture.CopyProject();
        var project = SpfxProject.Load(t.Path);

        Assert.True(PackageSolutionEditor.SetVersion(project, V));
        Assert.Equal("2.3.4.5", SpfxProject.Load(t.Path).Solution.Version);
        Assert.All(SpfxProject.Load(t.Path).Solution.Features, f => Assert.Equal("2.3.4.5", f.Version));
        Assert.False(PackageSolutionEditor.SetVersion(project.PackageSolutionPath, V));
    }

    [Fact]
    public void Argument_Validation()
    {
        Assert.Throws<ArgumentException>(() => PackageSolutionEditor.SetVersion("", V));
        Assert.Throws<ArgumentNullException>(() => PackageSolutionEditor.SetVersion((SpfxProject)null!, V));
    }
}
