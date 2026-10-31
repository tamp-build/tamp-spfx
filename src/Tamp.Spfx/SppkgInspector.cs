using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Tamp.Spfx;

/// <summary>
/// Reads a <c>.sppkg</c> (an Open Packaging Conventions zip) into an <see cref="SppkgInspection"/>. Nothing is
/// extracted to disk and nothing in the package is executed.
/// </summary>
/// <remarks>
/// The package is untrusted input as far as this code is concerned, so: entry names are never used as paths,
/// XML is parsed with DTDs prohibited and no resolver, and no entry is decompressed past
/// <see cref="SppkgInspectorOptions.MaxEntryBytes"/> (a zip bomb fails the inspection instead of the machine).
/// </remarks>
public static class SppkgInspector
{
    private static readonly XNamespace Sp = "http://schemas.microsoft.com/sharepoint/";
    private static readonly XNamespace AppNs = "http://schemas.microsoft.com/sharepoint/2012/app/manifest";
    private const string SpAssetLibrary = "HTTPS://SPCLIENTSIDEASSETLIBRARY/";

    /// <summary>Inspect the package at <paramref name="sppkgPath"/>.</summary>
    /// <exception cref="InvalidDataException">The file isn't a readable zip, or has no <c>AppManifest.xml</c>.</exception>
    public static SppkgInspection Inspect(string sppkgPath, SppkgInspectorOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(sppkgPath)) throw new ArgumentException("path is required.", nameof(sppkgPath));
        using var stream = File.OpenRead(sppkgPath);
        return Inspect(stream, Path.GetFileName(sppkgPath), options);
    }

    /// <summary>Inspect a package from a seekable stream.</summary>
    public static SppkgInspection Inspect(Stream sppkg, string fileName, SppkgInspectorOptions? options = null)
    {
        if (sppkg is null) throw new ArgumentNullException(nameof(sppkg));
        if (!sppkg.CanSeek) throw new ArgumentException("The stream must be seekable.", nameof(sppkg));
        options ??= new SppkgInspectorOptions();

        var size = sppkg.Length;
        if (size > options.MaxPackageBytes)
            throw new InvalidDataException($"{fileName} is {size} bytes, over the {options.MaxPackageBytes}-byte inspection limit.");

        sppkg.Position = 0;
        var sha = Convert.ToHexString(SHA256.HashData(sppkg)).ToLowerInvariant();
        sppkg.Position = 0;

        ZipArchive zip;
        try { zip = new ZipArchive(sppkg, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{fileName} is not a zip archive: {ex.Message}", ex); }

        using (zip)
        {
            if (zip.Entries.Count > options.MaxEntries)
                throw new InvalidDataException($"{fileName} has {zip.Entries.Count} entries, over the {options.MaxEntries}-entry limit.");

            var problems = new List<string>();
            var unsafeEntries = zip.Entries.Select(e => e.FullName).Where(IsUnsafeName).OrderBy(n => n, StringComparer.Ordinal).ToList();

            var manifestEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "AppManifest.xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{fileName} has no AppManifest.xml; it is not a SharePoint app package.");
            var app = ReadApp(LoadXml(manifestEntry, options) ?? throw new InvalidDataException($"{fileName}: AppManifest.xml could not be read."));

            var features = new List<SppkgFeature>();
            var components = new Dictionary<string, SppkgComponent>(StringComparer.OrdinalIgnoreCase);
            var registrations = new List<SppkgExtensionRegistration>();
            var assets = new List<SppkgAsset>();

            foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                var name = entry.FullName;
                if (name.EndsWith('/')) continue;

                if (name.StartsWith("ClientSideAssets/", StringComparison.OrdinalIgnoreCase))
                {
                    var digest = Digest(entry, options, out var bytes, out var problem);
                    if (problem is not null) problems.Add($"{name}: {problem}");
                    assets.Add(new SppkgAsset(name, bytes, digest ?? ""));
                    continue;
                }

                if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".config.xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "AppManifest.xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "[Content_Types].xml", StringComparison.Ordinal)) continue;

                XDocument? doc;
                try { doc = LoadXml(entry, options); }
                catch (Exception ex) when (ex is XmlException or InvalidDataException)
                {
                    problems.Add($"{name}: {ex.Message}");
                    continue;
                }
                if (doc?.Root is null) continue;

                if (doc.Root.Name == Sp + "Feature")
                {
                    var f = doc.Root;
                    features.Add(new SppkgFeature(Attr(f, "Id"), Attr(f, "Title"), Attr(f, "Version"), Attr(f, "Scope")));
                    continue;
                }

                foreach (var c in doc.Descendants(Sp + "ClientSideComponent"))
                {
                    var component = ReadComponent(c, name, problems);
                    if (component is not null) components.TryAdd(component.Id, component);   // repeated once per feature
                }

                foreach (var ca in doc.Descendants(Sp + "CustomAction"))
                    registrations.Add(new SppkgExtensionRegistration
                    {
                        Kind = SppkgRegistrationKind.CustomAction,
                        ComponentId = Attr(ca, "ClientSideComponentId"),
                        Location = Attr(ca, "Location"),
                        Title = Attr(ca, "Title"),
                        Properties = Attr(ca, "ClientSideComponentProperties"),
                        ListScope = Attr(ca, "RegistrationId"),
                        Entry = name,
                    });

                foreach (var ci in doc.Descendants(Sp + "ClientSideComponentInstance"))
                    registrations.Add(new SppkgExtensionRegistration
                    {
                        Kind = SppkgRegistrationKind.ClientSideComponentInstance,
                        ComponentId = Attr(ci, "ComponentId"),
                        Location = Attr(ci, "Location"),
                        Title = Attr(ci, "Title"),
                        Properties = Attr(ci, "Properties"),
                        ListScope = Attr(ci, "ListTemplateId"),
                        Entry = name,
                    });
            }

            var componentList = components.Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
            return new SppkgInspection
            {
                FileName = fileName,
                Sha256 = sha,
                SizeBytes = size,
                App = app,
                Features = features.OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                Components = componentList,
                ExtensionRegistrations = registrations,
                Assets = assets,
                ExternalScriptUrls = componentList.SelectMany(c => c.ExternalScripts).Select(s => s.Url)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList(),
                UnsafeEntries = unsafeEntries,
                ReadProblems = problems,
            };
        }
    }

    private static SppkgApp ReadApp(XDocument doc)
    {
        var app = doc.Root!;
        if (app.Name.LocalName != "App") throw new InvalidDataException("AppManifest.xml has no App root element.");
        var ns = app.Name.Namespace == XNamespace.None ? XNamespace.None : AppNs;
        var permissions = app.Descendants(ns + "WebApiPermissionRequest")
            .Select(p => new SppkgPermissionRequest(Attr(p, "ResourceId") ?? "", Attr(p, "Scope") ?? "", Attr(p, "AppId"), Attr(p, "ReplyUrl")))
            .ToList();
        return new SppkgApp
        {
            Name = Attr(app, "Name"),
            ProductId = Attr(app, "ProductID"),
            Version = Attr(app, "Version"),
            SharePointMinVersion = Attr(app, "SharePointMinVersion"),
            IsClientSideSolution = Bool(Attr(app, "IsClientSideSolution")),
            SkipFeatureDeployment = Bool(Attr(app, "SkipFeatureDeployment")),
            WebApiPermissionRequests = permissions,
        };
    }

    private static SppkgComponent? ReadComponent(XElement c, string entry, List<string> problems)
    {
        var id = Attr(c, "Id");
        if (string.IsNullOrEmpty(id)) { problems.Add($"{entry}: a ClientSideComponent has no Id."); return null; }

        string? type = Attr(c, "Type"), extensionType = null, alias = null, version = null;
        var hosts = new List<string>();
        var external = new List<SppkgExternalScript>();
        var deps = new List<SppkgComponentDependency>();

        var manifestJson = Attr(c, "ComponentManifest");
        if (!string.IsNullOrEmpty(manifestJson))
        {
            try
            {
                using var m = JsonDocument.Parse(manifestJson);
                var r = m.RootElement;
                type = r.GetString("componentType") ?? type;
                extensionType = r.GetString("extensionType");
                alias = r.GetString("alias");
                version = r.GetString("version");
                hosts.AddRange(r.GetArray("supportedHosts").Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()!));

                if (r.GetObject("loaderConfig") is { } loader)
                {
                    foreach (var baseUrl in loader.GetArray("internalModuleBaseUrls").Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!))
                        if (!string.Equals(baseUrl, SpAssetLibrary, StringComparison.OrdinalIgnoreCase) && IsRemote(baseUrl))
                            external.Add(new SppkgExternalScript("(module base URL)", baseUrl));

                    if (loader.GetObject("scriptResources") is { } resources)
                        foreach (var res in resources.EnumerateObject())
                        {
                            var kind = res.Value.GetString("type");
                            if (kind == "component")
                            {
                                deps.Add(new SppkgComponentDependency(res.Name, res.Value.GetString("id"), res.Value.GetString("version")));
                                continue;
                            }
                            foreach (var url in ResourceUrls(res.Value))
                                if (IsRemote(url)) external.Add(new SppkgExternalScript(res.Name, url));
                        }
                }
            }
            catch (JsonException ex)
            {
                problems.Add($"{entry}: component {id} has an unreadable ComponentManifest ({ex.Message}).");
            }
        }

        return new SppkgComponent
        {
            Id = id!,
            Name = Attr(c, "Name"),
            ComponentType = type,
            ExtensionType = extensionType,
            Alias = alias,
            Version = version,
            RequiresCustomScript = string.Equals(Attr(c, "ReturnIfCustomScriptDisabled"), "false", StringComparison.OrdinalIgnoreCase),
            SupportedHosts = hosts,
            ExternalScripts = external,
            Dependencies = deps,
        };
    }

    // "path" resources carry `path`; "localizedPath" carry `defaultPath` plus a `paths` map of locale -> path.
    private static IEnumerable<string> ResourceUrls(JsonElement resource)
    {
        if (resource.GetString("path") is { } p) yield return p;
        if (resource.GetString("defaultPath") is { } d) yield return d;
        if (resource.GetObject("paths") is { } paths)
            foreach (var l in paths.EnumerateObject())
                if (l.Value.ValueKind == JsonValueKind.String) yield return l.Value.GetString()!;
    }

    internal static bool IsRemote(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("//", StringComparison.Ordinal);

    internal static bool IsUnsafeName(string name) =>
        name.StartsWith('/') || name.StartsWith('\\') ||
        (name.Length > 1 && name[1] == ':') ||
        name.Replace('\\', '/').Split('/').Any(s => s == "..");

    private static XDocument? LoadXml(ZipArchiveEntry entry, SppkgInspectorOptions options)
    {
        var bytes = ReadBounded(entry, options.MaxEntryBytes);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = options.MaxEntryBytes,
        };
        using var ms = new MemoryStream(bytes);
        using var reader = XmlReader.Create(ms, settings);
        return XDocument.Load(reader);
    }

    private static string? Digest(ZipArchiveEntry entry, SppkgInspectorOptions options, out long bytes, out string? problem)
    {
        problem = null;
        try
        {
            using var s = entry.Open();
            using var bounded = new BoundedStream(s, options.MaxEntryBytes);
            var hash = SHA256.HashData(bounded);
            bytes = bounded.BytesRead;
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (InvalidDataException ex)
        {
            bytes = entry.Length;
            problem = ex.Message;
            return null;
        }
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, long max)
    {
        using var s = entry.Open();
        using var bounded = new BoundedStream(s, max);
        using var ms = new MemoryStream();
        bounded.CopyTo(ms);
        return ms.ToArray();
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static bool Bool(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads at most <c>max</c> bytes, then throws, whatever the entry header claims its size is.</summary>
    private sealed class BoundedStream(Stream inner, long max) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            if (BytesRead > max) throw new InvalidDataException($"entry decompresses past the {max}-byte limit.");
            return n;
        }
    }
}

/// <summary>Limits applied while reading a package.</summary>
public sealed record SppkgInspectorOptions
{
    /// <summary>Largest package accepted (default 256 MiB).</summary>
    public long MaxPackageBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>Most entries accepted (default 20,000).</summary>
    public int MaxEntries { get; init; } = 20_000;
    /// <summary>Largest decompressed size of any single entry (default 64 MiB).</summary>
    public long MaxEntryBytes { get; init; } = 64L * 1024 * 1024;
}
