using Tamp.Heft.V1;

namespace Tamp.Spfx;

/// <summary>
/// Settings for <c>heft package-solution</c>, the SPFx rig's packaging phase. Writes the <c>.sppkg</c> from the
/// outputs of the last build: it does <em>not</em> rebuild, so run it only after a successful
/// <see cref="Spfx.Test(Action{HeftTestSettings})"/> (or <see cref="Spfx.Build(Action{HeftBuildSettings})"/>) in the
/// same mode, or it will package stale bundles.
/// </summary>
public sealed class SpfxPackageSolutionSettings : HeftActionSettings
{
    /// <summary>Settings for <c>heft package-solution</c>, production mode by default.</summary>
    public SpfxPackageSolutionSettings() : base("package-solution") { Production = true; }

    /// <summary><c>--pretty-print-xml</c>: indent the XML inside the package (useful when diffing packages).</summary>
    public bool PrettyPrintXml { get; set; }

    /// <summary><c>--no-spppkg</c> (Heft's spelling): lay out the package folder but don't zip the <c>.sppkg</c>.</summary>
    public bool NoSppkg { get; set; }

    /// <inheritdoc />
    protected override void AppendArguments(List<string> args)
    {
        base.AppendArguments(args);
        if (PrettyPrintXml) args.Add("--pretty-print-xml");
        if (NoSppkg) args.Add("--no-spppkg");
    }
}

/// <summary>
/// Build verbs for a SharePoint Framework project on the Heft toolchain (SPFx 1.22+). Each is the corresponding
/// Heft command with <c>--production</c> on by default, which is what a build machine wants: minified bundles
/// and a package that references them. Pass <c>s.SetProduction(false)</c> for a debug build.
/// </summary>
/// <remarks>
/// <para>
/// The usual order is <see cref="Test(Action{HeftTestSettings})"/> (which builds) and then
/// <see cref="PackageSolution(Action{SpfxPackageSolutionSettings})"/>, mirroring the SPFx template's
/// <c>npm run build</c> (<c>heft test --clean --production &amp;&amp; heft package-solution --production</c>).
/// Then inspect the package with <see cref="SppkgInspector"/> and gate it with <see cref="SppkgPolicy"/>.
/// </para>
/// <para>
/// A legacy gulp-based project (SPFx 1.21 and earlier) is refused with an explanation rather than handed to
/// Heft, when the working directory is set and recognisable.
/// </para>
/// </remarks>
public static class Spfx
{
    /// <summary><c>heft build --production</c>: compile, lint and bundle.</summary>
    public static CommandPlan Build(Action<HeftBuildSettings> configure) => Run(new HeftBuildSettings { Production = true }, configure);

    /// <summary><c>heft test --production</c>: build, then Jest (JUnit and Cobertura land under <c>jest-output/</c>).</summary>
    public static CommandPlan Test(Action<HeftTestSettings> configure) => Run(new HeftTestSettings { Production = true }, configure);

    /// <summary><c>heft package-solution --production</c>: write the <c>.sppkg</c>.</summary>
    public static CommandPlan PackageSolution(Action<SpfxPackageSolutionSettings> configure) => Run(new SpfxPackageSolutionSettings(), configure);

    /// <summary><c>heft clean</c>.</summary>
    public static CommandPlan Clean(Action<HeftCleanSettings> configure) => Run(new HeftCleanSettings(), configure);

    private static CommandPlan Run<T>(T settings, Action<T> configure) where T : HeftSettingsBase
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        configure(settings);
        RefuseGulp(settings.WorkingDirectory);
        return settings.ToCommandPlan();
    }

    private static void RefuseGulp(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, "package.json"))
            || !File.Exists(Path.Combine(directory, "config", "package-solution.json")))
            return;
        SpfxProject project;
        try { project = SpfxProject.Load(directory); }
        catch (InvalidDataException) { return; }   // let Heft report its own config errors
        if (project.Toolchain == SpfxToolchain.Gulp)
            throw new InvalidOperationException(
                $"{directory} is a gulp-based SPFx project (SPFx {project.SpfxVersion ?? "?"}). Tamp.Spfx drives the Heft " +
                "toolchain (SPFx 1.22+); upgrade the project, or run gulp yourself.");
    }
}

/// <summary>Fluent setters for <see cref="SpfxPackageSolutionSettings"/>.</summary>
public static class SpfxPackageSolutionSettingsExtensions
{
    /// <summary><c>--pretty-print-xml</c>.</summary>
    public static SpfxPackageSolutionSettings SetPrettyPrintXml(this SpfxPackageSolutionSettings s, bool v = true) { s.PrettyPrintXml = v; return s; }
    /// <summary><c>--no-spppkg</c>.</summary>
    public static SpfxPackageSolutionSettings SetNoSppkg(this SpfxPackageSolutionSettings s, bool v = true) { s.NoSppkg = v; return s; }
}
