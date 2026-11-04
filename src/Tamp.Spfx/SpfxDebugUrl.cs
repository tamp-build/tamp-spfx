using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tamp.Spfx;

/// <summary>
/// Builds the SharePoint debug-page URL for a <c>serve.json</c> serve configuration: the page to open, with the
/// query string that makes SharePoint load the components from the local dev server. Matches the SPFx Heft rig's
/// own <c>SPFxDebugPageUrl</c> (<c>@microsoft/spfx-heft-plugins</c> 1.24.1) character for character, so a test
/// can open exactly the page <c>heft start</c> would.
/// </summary>
/// <remarks>
/// <para>
/// The query is <c>debugManifestsFile</c>, <c>debug=true</c> and <c>noredir=true</c>; for extensions also
/// <c>loadSPFX=true</c> plus <c>customActions</c> / <c>fieldCustomizers</c> as JSON, or the Form Customizer's
/// fields (<c>componentId</c>, <c>PageType</c>, <c>RootFolder</c>, <c>ID</c>, <c>ContentTypeID</c>, ...) with
/// <c>properties</c> as JSON. <c>debug=true</c> is dropped when any extension is loaded, as the rig does. Like the
/// rig, the page URL's own query string is replaced (its fragment is kept).
/// </para>
/// <para>
/// Two deliberate differences, both where the rig produces something unusable: a configuration name that isn't in
/// <c>serve.json</c> throws instead of silently falling back to <c>default</c>, and a <c>{tenantDomain}</c>
/// placeholder with no tenant domain throws instead of yielding <c>https://{tenantdomain}/...</c>.
/// </para>
/// <para>
/// The URL carries only what <c>serve.json</c> already holds; it never includes credentials. Don't write it into
/// the dev server's state file.
/// </para>
/// </remarks>
public static class SpfxDebugUrl
{
    private const string Placeholder = "{tenantdomain}";
    private static readonly Regex PlaceholderPattern = new(Regex.Escape(Placeholder), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonStringify = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The debug-page URL for <paramref name="configuration"/> (default: the <c>default</c> configuration, else
    /// <c>initialPage</c>).
    /// </summary>
    /// <param name="serve">The project's serve configuration.</param>
    /// <param name="configuration">A key of <c>serveConfigurations</c>; null for <c>default</c>.</param>
    /// <param name="tenantDomain">
    /// Replaces <c>{tenantDomain}</c> (e.g. <c>contoso.sharepoint.com</c>). Default: the
    /// <c>SPFX_SERVE_TENANT_DOMAIN</c> environment variable, as for the rig.
    /// </param>
    /// <param name="manifestsUrl">
    /// The manifests file to load. Default: computed from <paramref name="serve"/> as the rig does. Pass a running
    /// dev server's <c>ManifestsUrl</c> to point at the server actually started.
    /// </param>
    /// <exception cref="KeyNotFoundException"><paramref name="configuration"/> isn't in <c>serve.json</c>.</exception>
    /// <exception cref="InvalidOperationException">There's no page URL, or a <c>{tenantDomain}</c> placeholder and no tenant domain.</exception>
    public static string Build(SpfxServeConfig serve, string? configuration = null, string? tenantDomain = null, string? manifestsUrl = null)
    {
        if (serve is null) throw new ArgumentNullException(nameof(serve));
        // A blank tenant domain counts as none, so it gets the clear "set SPFX_SERVE_TENANT_DOMAIN" error below.
        tenantDomain = NonEmpty(tenantDomain?.Trim()) ?? NonEmpty(Environment.GetEnvironmentVariable(SpfxServeConfig.TenantDomainVariable)?.Trim());

        SpfxServeConfiguration? selected = null;
        if (configuration is not null)
        {
            selected = serve.Find(configuration) ?? throw new KeyNotFoundException(
                $"serve.json has no serve configuration '{configuration}'. It has: " +
                (serve.Configurations.Count == 0 ? "none" : string.Join(", ", serve.Configurations.Select(c => c.Name))) + ".");
        }
        else if (serve.Configurations.Count > 0)
        {
            selected = serve.Find("default");
        }

        var page = selected?.PageUrl ?? serve.Find("default")?.PageUrl ?? serve.InitialPage
            ?? throw new InvalidOperationException(
                "No page to debug on: the serve configuration has no pageUrl and serve.json has no initialPage.");
        if (PlaceholderPattern.IsMatch(page) && tenantDomain is null)
            throw new InvalidOperationException(
                $"The page URL '{page}' contains {{tenantDomain}}. Pass the tenant domain (e.g. contoso.sharepoint.com) " +
                $"or set {SpfxServeConfig.TenantDomainVariable}.");
        page = ReplaceTenantDomain(page, tenantDomain);
        if (!Uri.TryCreate(page, UriKind.Absolute, out var pageUri) || (pageUri.Scheme != Uri.UriSchemeHttps && pageUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"The page URL '{page}' is not an absolute http(s) URL.");

        var query = new QueryParameters();
        query.Set("debugManifestsFile", manifestsUrl ?? serve.ManifestsUrl(tenantDomain));
        query.Set("debug", "true");
        query.Set("noredir", "true");

        var customActions = Truthy(selected?.CustomActions);
        var fieldCustomizers = Truthy(selected?.FieldCustomizers);
        var formCustomizer = Truthy(selected?.FormCustomizer);
        if (customActions is not null || fieldCustomizers is not null || formCustomizer is not null) query.Set("loadSPFX", "true");
        if (customActions is { } ca)
        {
            query.Set("customActions", Stringify(ca));
            query.Delete("debug"); // the lists app fails to load debug scripts when both debug and loadSPFX are set
        }
        if (fieldCustomizers is { } fc)
        {
            query.Set("fieldCustomizers", Stringify(fc));
            query.Delete("debug");
        }
        if (formCustomizer is { } form)
        {
            if (form.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in form.EnumerateObject())
                {
                    if (!IsTruthy(field.Value)) continue;
                    query.Set(field.Name, field.Name switch
                    {
                        "properties" => Stringify(field.Value),
                        "RootFolder" => Uri.UnescapeDataString(ToJsString(field.Value)),
                        _ => ToJsString(field.Value),
                    });
                }
            }
            query.Delete("debug");
        }

        var left = pageUri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return left + "?" + query + pageUri.GetComponents(UriComponents.Fragment | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
    }

    /// <summary>Replace the first <c>{tenantDomain}</c> (any case) in <paramref name="value"/>, as the rig does.</summary>
    internal static string ReplaceTenantDomain(string value, string? tenantDomain) =>
        string.IsNullOrEmpty(tenantDomain) ? value : PlaceholderPattern.Replace(value, tenantDomain.Replace("$", "$$", StringComparison.Ordinal), 1);

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static JsonElement? Truthy(JsonElement? e) => e is { } v && IsTruthy(v) ? v : null;

    // JavaScript truthiness for a JSON value.
    private static bool IsTruthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => e.GetString()!.Length > 0,
        JsonValueKind.Number => e.GetDouble() != 0,
        _ => true,
    };

    // JSON.stringify, compact, keys in file order.
    internal static string Stringify(JsonElement e)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonStringify.Encoder }))
            WriteJs(writer, e);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteJs(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject()) { w.WritePropertyName(p.Name); WriteJs(w, p.Value); }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) WriteJs(w, item);
                w.WriteEndArray();
                break;
            case JsonValueKind.Number:
                w.WriteRawValue(JsNumber(e), skipInputValidation: true);
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    // String(value) in JavaScript, for the form customizer's plain fields.
    private static string ToJsString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Number => JsNumber(e),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object => "[object Object]",
        JsonValueKind.Array => string.Join(",", e.EnumerateArray().Select(i => i.ValueKind is JsonValueKind.Null ? "" : ToJsString(i))),
        _ => "null",
    };

    // Number.prototype.toString: integers without a decimal point, shortest round-trip otherwise.
    private static string JsNumber(JsonElement e)
    {
        if (e.TryGetInt64(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        var d = e.GetDouble();
        if (d == Math.Floor(d) && Math.Abs(d) < 1e21) return d.ToString("F0", CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
    }

    /// <summary>An ordered query string with URLSearchParams' set/delete/serialize semantics.</summary>
    private sealed class QueryParameters
    {
        private readonly List<KeyValuePair<string, string>> _items = new();

        public void Set(string name, string value)
        {
            var i = _items.FindIndex(kv => kv.Key == name);
            if (i < 0) { _items.Add(new(name, value)); return; }
            // The rig refuses to overwrite a parameter with a different value (e.g. a form field named "debug").
            if (_items[i].Value != value) throw new InvalidOperationException($"The {name} parameter is already set with a different value.");
        }

        public void Delete(string name) => _items.RemoveAll(kv => kv.Key == name);

        public override string ToString() => string.Join("&", _items.Select(kv => FormEncode(kv.Key) + "=" + FormEncode(kv.Value)));

        // application/x-www-form-urlencoded as the WHATWG URL spec serializes it: ASCII alphanumerics and *-._ stay,
        // space becomes +, everything else is percent-encoded UTF-8.
        private static string FormEncode(string s)
        {
            var sb = new StringBuilder(s.Length * 3);
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                    or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
                    sb.Append((char)b);
                else if (b == (byte)' ')
                    sb.Append('+');
                else
                    sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
