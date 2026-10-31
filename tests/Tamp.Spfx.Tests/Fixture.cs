using System.IO.Compression;
using System.Text;

namespace Tamp.Spfx.Tests;

internal static class Fixture
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static string Sppkg => Path.Combine(Dir, "spfx-probe.sppkg");
    public static string Project => Path.Combine(Dir, "probe");

    public const string SolutionId = "67271b26-cf99-4d77-a709-4faee6ca69f9";
    public const string WebPartId = "cc9029b5-8b72-4a7d-b731-aa1fa590f781";
    public const string ExtensionId = "d31829d6-c180-4c7d-84ff-7fe079a66f94";

    /// <summary>Build a small package in memory from (entry name, content) pairs.</summary>
    public static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    public static string AppManifest(string version = "1.0.0.0", bool skipFeatureDeployment = false, string permissions = "") =>
        $"""<?xml version="1.0" encoding="utf-8"?><App xmlns="http://schemas.microsoft.com/sharepoint/2012/app/manifest" Name="t" ProductID="{SolutionId}" SharePointMinVersion="16.0.0.0" IsClientSideSolution="true" Version="{version}"{(skipFeatureDeployment ? " SkipFeatureDeployment=\"true\"" : "")}><Properties><Title>t</Title></Properties>{permissions}</App>""";

    public static string Component(string id, string manifestJson, string extraAttributes = "") =>
        $"""<?xml version="1.0" encoding="utf-8"?><Elements xmlns="http://schemas.microsoft.com/sharepoint/"><ClientSideComponent Name="c" Id="{id}" ComponentManifest="{System.Security.SecurityElement.Escape(manifestJson)}" Type="WebPart"{extraAttributes}/></Elements>""";

    /// <summary>A throwaway copy of the probe project, so tests can edit it.</summary>
    public static TempDir CopyProject()
    {
        var t = new TempDir();
        foreach (var file in Directory.GetFiles(Project, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(t.Path, Path.GetRelativePath(Project, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return t;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tamp-spfx-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string relative) => System.IO.Path.Combine(Path, relative);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
