using Tamp.Heft.V1;
using Xunit;

namespace Tamp.Spfx.Tests;

public sealed class SpfxTests
{
    private static readonly HeftBinaryResolution Node = new()
    {
        Executable = "/usr/bin/node",
        PrefixArguments = new[] { "/w/node_modules/@rushstack/heft/bin/heft" },
        Source = HeftResolutionSource.Explicit,
    };

    private static string[] Args(CommandPlan p) => p.Arguments.Skip(1).ToArray();

    [Fact]
    public void Verbs_Default_To_Production()
    {
        Assert.Equal(new[] { "build", "--production" }, Args(Spfx.Build(s => s.SetBinary(Node))));
        Assert.Equal(new[] { "test", "--production" }, Args(Spfx.Test(s => s.SetBinary(Node))));
        Assert.Equal(new[] { "package-solution", "--production" }, Args(Spfx.PackageSolution(s => s.SetBinary(Node))));
        Assert.Equal(new[] { "clean" }, Args(Spfx.Clean(s => s.SetBinary(Node))));
    }

    [Fact]
    public void Production_Can_Be_Turned_Off_For_A_Debug_Package()
    {
        Assert.Equal(new[] { "package-solution" }, Args(Spfx.PackageSolution(s => s.SetBinary(Node).SetProduction(false))));
    }

    [Fact]
    public void Package_Solution_Flags_Use_Heft_Spelling()
    {
        var p = Spfx.PackageSolution(s => s.SetBinary(Node).SetPrettyPrintXml().SetNoSppkg().SetClean());
        Assert.Equal(new[] { "package-solution", "--production", "--clean", "--pretty-print-xml", "--no-spppkg" }, Args(p));
    }

    [Fact]
    public void The_Ci_Sequence_Mirrors_The_Template_Build_Script()
    {
        // npm run build == heft test --clean --production && heft package-solution --production
        Assert.Equal(new[] { "test", "--production", "--clean" }, Args(Spfx.Test(s => s.SetBinary(Node).SetClean())));
        Assert.Equal(new[] { "package-solution", "--production" }, Args(Spfx.PackageSolution(s => s.SetBinary(Node))));
    }

    [Fact]
    public void A_Gulp_Project_Is_Refused_With_An_Explanation()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("config"));
        File.WriteAllText(t.File("package.json"), """{ "dependencies": { "@microsoft/sp-core-library": "1.20.0" }, "devDependencies": { "@microsoft/sp-build-web": "1.20.2" } }""");
        File.WriteAllText(t.File(Path.Combine("config", "package-solution.json")), """{ "solution": { "version": "1.0.0.0" } }""");

        var ex = Assert.Throws<InvalidOperationException>(() => Spfx.Build(s => s.SetBinary(Node).SetWorkingDirectory(t.Path)));
        Assert.Contains("gulp", ex.Message);
        Assert.Contains("1.20.0", ex.Message);
    }

    [Fact]
    public void A_Heft_Project_Or_A_Directory_That_Is_Not_Spfx_Goes_Through()
    {
        using var t = Fixture.CopyProject();
        Assert.Equal("/usr/bin/node", Spfx.Build(s => s.SetBinary(Node).SetWorkingDirectory(t.Path)).Executable);

        using var empty = new TempDir();
        Assert.Equal("/usr/bin/node", Spfx.Test(s => s.SetBinary(Node).SetWorkingDirectory(empty.Path)).Executable);

        File.WriteAllText(Path.Combine(t.Path, "package.json"), "{ broken");
        Assert.Equal("/usr/bin/node", Spfx.Build(s => s.SetBinary(Node).SetWorkingDirectory(t.Path)).Executable);
    }

    [Fact]
    public void Null_Configure_Throws() => Assert.Throws<ArgumentNullException>(() => Spfx.Build(null!));
}
