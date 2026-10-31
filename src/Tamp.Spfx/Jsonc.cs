using System.Text;
using System.Text.Json;

namespace Tamp.Spfx;

/// <summary>
/// SPFx config files are JSON with comments (the generators write <c>//</c> comments into several of them).
/// These helpers read them tolerantly and edit them without disturbing anything but the edited value.
/// </summary>
internal static class Jsonc
{
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    internal static JsonDocument ParseFile(string path)
    {
        try
        {
            return JsonDocument.Parse(StripBom(File.ReadAllBytes(path), out _), DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid JSON: {ex.Message}", ex);
        }
    }

    internal static ReadOnlyMemory<byte> StripBom(byte[] bytes, out bool hadBom)
    {
        hadBom = bytes.AsSpan().StartsWith(Bom);
        return hadBom ? bytes.AsMemory(Bom.Length) : bytes;
    }

    /// <summary>
    /// The byte span of every string value whose JSON path matches <paramref name="isTarget"/>. Paths are
    /// <c>$.a.b[0].c</c>-style with array indices replaced by <c>[*]</c>, e.g. <c>$.solution.features[*].version</c>.
    /// </summary>
    internal static List<(int Start, int Length, string Value, string Path)> FindStringValues(ReadOnlyMemory<byte> json, Func<string, bool> isTarget)
    {
        var found = new List<(int, int, string, string)>();
        var reader = new Utf8JsonReader(json.Span, ReaderOptions);
        var path = new Stack<string>();
        string? pendingProperty = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingProperty = reader.GetString();
                    break;

                case JsonTokenType.StartObject:
                    path.Push(Segment(path, ref pendingProperty));
                    break;

                case JsonTokenType.StartArray:
                    path.Push(Segment(path, ref pendingProperty) + "[*]");
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    path.Pop();
                    break;

                case JsonTokenType.String:
                    {
                        var at = Segment(path, ref pendingProperty);
                        if (isTarget(at))
                        {
                            // TokenStartIndex is the opening quote; ValueSpan excludes the quotes and is the raw
                            // (still escaped) text, so the replaced region is exactly the quoted literal.
                            var start = (int)reader.TokenStartIndex;
                            var length = reader.ValueSpan.Length + 2;
                            found.Add((start, length, reader.GetString()!, at));
                        }
                        break;
                    }

                default:
                    pendingProperty = null;
                    break;
            }
        }
        return found;
    }

    private static string Segment(Stack<string> path, ref string? property)
    {
        var parent = path.Count == 0 ? "$" : path.Peek();
        var segment = property is null ? parent : parent + "." + property;
        property = null;
        return segment;
    }

    /// <summary>Replace the given spans with JSON string literals, back to front so earlier offsets stay valid.</summary>
    internal static byte[] Splice(ReadOnlyMemory<byte> json, IEnumerable<(int Start, int Length)> spans, string newValue)
    {
        var literal = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newValue));
        var buffer = json.ToArray().ToList();
        foreach (var (start, length) in spans.OrderByDescending(s => s.Start))
        {
            buffer.RemoveRange(start, length);
            buffer.InsertRange(start, literal);
        }
        return buffer.ToArray();
    }

    internal static string? GetString(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static bool? GetBool(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    internal static JsonElement? GetObject(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    internal static IEnumerable<JsonElement> GetArray(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
}
