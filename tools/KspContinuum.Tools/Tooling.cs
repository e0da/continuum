using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace KspContinuum.Tools;

internal sealed class ToolException(string message) : Exception(message);

internal sealed class Arguments
{
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);
    private readonly List<string> positionals = [];

    public Arguments(string[] args, params string[] flags)
    {
        var flagSet = flags.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var item = args[i];
            if (!item.StartsWith("--", StringComparison.Ordinal)) { positionals.Add(item); continue; }
            if (flagSet.Contains(item)) { values[item] = null; continue; }
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ToolException($"{item} requires a value");
            values[item] = args[i];
        }
    }

    public bool Has(string key) => values.ContainsKey(key);
    public string Required(string key) => values.TryGetValue(key, out var value) && value is not null ? value : throw new ToolException($"missing required option {key}");
    public string? Optional(string key) => values.TryGetValue(key, out var value) ? value : null;
    public string Positional(int index, string label) => positionals.Count > index ? positionals[index] : throw new ToolException($"missing {label}");
}

internal static class Tooling
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    public static string ReadBounded(string path, long maximum = 8 * 1024 * 1024)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ToolException($"missing file: {path}");
        if (info.Length > maximum) throw new ToolException($"file exceeds {maximum} bytes: {path}");
        return File.ReadAllText(path, new UTF8Encoding(false, true));
    }
    public static JsonObject ReadObject(string path, long maximum = 8 * 1024 * 1024)
    {
        try { return JsonNode.Parse(ReadBounded(path, maximum)) as JsonObject ?? throw new ToolException($"expected JSON object: {path}"); }
        catch (JsonException error) { throw new ToolException($"invalid JSON in {path}: {error.Message}"); }
    }
    public static void WriteNew(string path, string content)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
    public static void WriteJsonNew(string path, JsonNode value) => WriteNew(path, value.ToJsonString(Json) + "\n");
    public static double Finite(JsonNode? node, string label)
    {
        if (node is null || !double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new ToolException($"{label} must be finite");
        return value;
    }
    public static string Text(JsonNode? node, string label, int maximum = 1000)
    {
        var value = node?.GetValue<string>() ?? throw new ToolException($"{label} must be text");
        if (value.Length == 0 || value.Length > maximum || value.Any(char.IsControl)) throw new ToolException($"{label} is invalid");
        return value;
    }
    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static string Html(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
    public static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "README.md"))) current = current.Parent;
        return current?.FullName ?? Directory.GetCurrentDirectory();
    }
    public static void Require(bool condition, string message) { if (!condition) throw new ToolException(message); }
    public static bool SafeRelative(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Split('/', '\\').Any(part => part is "" or "." or "..");
}
