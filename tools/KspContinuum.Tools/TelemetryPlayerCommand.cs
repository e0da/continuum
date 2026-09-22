using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KspContinuum.Tools;

internal static class TelemetryPlayerCommand
{
    private static readonly string[] Header = ["wall_s", "ut_s", "phase", "body", "situation", "altitude_m", "surface_speed_mps", "throttle", "stage", "parts", "packed", "autopilot"];
    public static int Run(string[] args)
    {
        var a = new Arguments(args); var source = a.Positional(0, "source"); var title = a.Required("--title"); var output = a.Required("--output"); var data = Capture(source); Tooling.WriteNew(output, Render(data, title, false)); Console.WriteLine($"Wrote {data["rows"]!.AsArray().Count} recorded observations to {output}"); return 0;
    }
    public static JsonObject Capture(string path)
    {
        var info = new FileInfo(path); Tooling.Require(info.Exists && info.LinkTarget is null, "Source may not be a symbolic link"); var raw = File.ReadAllBytes(path); Tooling.Require(raw.Length <= 32 * 1024 * 1024, "Source exceeds byte limit"); var lines = new UTF8Encoding(false, true).GetString(raw).Replace("\r\n", "\n").TrimEnd('\n').Split('\n'); Tooling.Require(lines.Length > 1 && lines[0].Split(',').SequenceEqual(Header), "Unsupported mission telemetry header"); var rows = new JsonArray(); double wall = -1, ut = -1;
        foreach (var line in lines.Skip(1)) { var f = line.Split(','); Tooling.Require(f.Length == Header.Length && rows.Count < 250000, "Malformed or oversized telemetry"); double? N(int i) { if (f[i] == "") return null; Tooling.Require(double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && Math.Abs(n) <= 1e15, "Invalid telemetry number"); return n; } var nw = N(0); var nu = N(1); Tooling.Require(nw >= 0 && nu >= 0 && nw >= wall && nu >= ut && !string.IsNullOrEmpty(f[2]), "Missing or backward clock"); wall = nw.GetValueOrDefault(); ut = nu.GetValueOrDefault(); var throttle = N(7); Tooling.Require(throttle is null or >= 0 and <= 1, "Throttle is outside 0..1"); var stage = N(8); var parts = N(9); Tooling.Require((stage is null || stage >= -1 && stage == Math.Truncate(stage.Value)) && (parts is null || parts >= 0 && parts == Math.Truncate(parts.Value)), "Invalid stage or parts"); Tooling.Require(f[10] is "" or "True" or "False", "Invalid packed state"); rows.Add(new JsonObject { { "phase", f[2] }, { "body", f[3] }, { "situation", f[4] }, { "autopilot", f[11] }, { "wall", nw }, { "ut", nu }, { "altitude", N(5) }, { "speed", N(6) }, { "throttle", throttle }, { "stage", stage }, { "parts", parts }, { "packed", f[10] == "" ? null : f[10] == "True" } }); }
        return new JsonObject { { "schema", "ksp-continuum-telemetry-playback/v1" }, { "sourceSha256", Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() }, { "rows", rows }, { "scope", "recorded observations only; no state reconstruction or resimulation" } };
    }
    public static string Render(JsonObject data, string title, bool back) { Tooling.Require(!string.IsNullOrWhiteSpace(title) && title.Length <= (back ? 227 : 160), "Title is invalid"); var template = Tooling.ReadBounded(Path.Combine(Tooling.Root(), "templates/telemetry-player.html"), 128 * 1024); Tooling.Require(template.Contains("<!--DATA-->") && template.Contains("<!--TITLE-->"), "Telemetry player template placeholders are invalid"); var encoded = data.ToJsonString().Replace("&", "\\u0026").Replace("<", "\\u003c").Replace(">", "\\u003e"); var page = template.Replace("<!--TITLE-->", Tooling.Html(title)).Replace("<!--DATA-->", encoded); if (back) page = page.Replace("<body>", "<body><nav aria-label=\"Mission navigation\"><a href=\"index.html\">Back to mission report</a></nav>"); return page; }
}
