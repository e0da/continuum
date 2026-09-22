using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KspContinuum.Tools;

internal static partial class CompareInputsCommand
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private sealed record Key(double Time, double Value, string Mode, double Control1, double Control2);
    private sealed record Track(string Name, double Minimum, double Maximum, Key[] Keys)
    {
        public double Evaluate(double time)
        {
            var index = Array.FindLastIndex(Keys, key => key.Time <= time);
            if (index == Keys.Length - 1) return Keys[index].Value;
            var start = Keys[index]; var end = Keys[index + 1];
            if (start.Mode == "step") return start.Value;
            var u = (time - start.Time) / (end.Time - start.Time);
            if (start.Mode == "linear") return Lerp(start.Value, end.Value, u);
            var a = Lerp(start.Value, start.Control1, u); var b = Lerp(start.Control1, start.Control2, u); var c = Lerp(start.Control2, end.Value, u);
            return Lerp(Lerp(a, b, u), Lerp(b, c, u), u);
        }
    }
    private sealed record Timeline(double Duration, Dictionary<string, Track> Tracks, string Hash, string Filename);
    private sealed class Rms { public int Count; public double Sum; public void Add(double x) { Count++; Sum += x * x; } public double Value => Math.Sqrt(Sum / Count); }

    public static int Run(string[] args)
    {
        var a = new Arguments(args);
        var left = Parse(a.Positional(0, "left timeline")); var right = Parse(a.Positional(1, "right timeline"));
        var start = Number(a.Required("--start"), "start"); var end = Number(a.Required("--end"), "end");
        Tooling.Require(int.TryParse(a.Required("--samples"), NumberStyles.None, CultureInfo.InvariantCulture, out var samples), "samples must be an integer");
        var tolerance = Number(a.Required("--tolerance"), "tolerance");
        var report = Compare(left, right, start, end, samples, tolerance);
        var output = a.Optional("--output");
        if (output is null) Console.WriteLine(report.ToJsonString(Tooling.Json));
        else { Tooling.WriteJsonNew(output, report); Console.WriteLine("Wrote sampled input comparison to " + output); }
        return 0;
    }

    private static Timeline Parse(string path)
    {
        var info = new FileInfo(path); Tooling.Require(info.Exists && info.Length <= MaxBytes, "timeline source missing or too large");
        var bytes = File.ReadAllBytes(path); var raw = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        Tooling.Require(!raw.Contains('\r'), "timeline contains an invalid carriage return");
        var lines = raw.TrimEnd('\n').Split('\n'); Tooling.Require(lines.Length <= 100000 && lines.All(x => x.Length is > 0 and <= 1024), "invalid timeline lines");
        var i = 0; void Expect(string value) { Tooling.Require(i < lines.Length && lines[i++] == value, "expected header '" + value + "'"); }
        Expect("schema,ksp-continuum-input-timeline/v1"); var durationRow = Fields(lines[i++], 2); Tooling.Require(durationRow[0] == "duration", "expected duration row"); var duration = Number(durationRow[1], "duration"); Tooling.Require(duration > 0, "duration must be positive");
        Expect("track,name,min,max"); var specs = new Dictionary<string, (double Min, double Max)>();
        while (i < lines.Length && lines[i].StartsWith("track,", StringComparison.Ordinal)) { var f = Fields(lines[i++], 4); Tooling.Require(Id().IsMatch(f[1]) && specs.Count < 64 && !specs.ContainsKey(f[1]), "invalid or duplicate track"); var min = Number(f[2], "track minimum"); var max = Number(f[3], "track maximum"); Tooling.Require(min <= max, "track minimum exceeds maximum"); specs.Add(f[1], (min, max)); }
        Tooling.Require(specs.Count > 0, "timeline requires at least one track"); Expect("key,track,time,value,mode,control1,control2"); var keys = specs.Keys.ToDictionary(x => x, _ => new List<Key>()); var total = 0;
        while (i < lines.Length && lines[i].StartsWith("key,", StringComparison.Ordinal)) { var f = Fields(lines[i++], 7); Tooling.Require(keys.ContainsKey(f[1]), "key references unknown track " + f[1]); Tooling.Require(f[4] is "step" or "linear" or "cubic-bezier", "unknown interpolation mode " + f[4]); Tooling.Require(++total <= 65536 && keys[f[1]].Count < 32768, "timeline has too many keys"); keys[f[1]].Add(new(Number(f[2], "key time"), Number(f[3], "key value"), f[4], Number(f[5], "key control1"), Number(f[6], "key control2"))); }
        Expect("event,time,name,value"); var events = 0;
        while (i < lines.Length) { var f = Fields(lines[i++], 4); Tooling.Require(f[0] == "event" && ++events <= 8192, "invalid event row"); var time = Number(f[1], "event time"); Tooling.Require(time >= 0 && time <= duration && Id().IsMatch(f[2]) && int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _), "invalid event"); }
        var tracks = new Dictionary<string, Track>();
        foreach (var (name, range) in specs) { var list = keys[name]; Tooling.Require(list.Count > 0 && list[0].Time == 0, "track " + name + " must start with a key at time zero"); for (var k = 0; k < list.Count; k++) Tooling.Require(list[k].Time >= 0 && list[k].Time <= duration && (k == 0 || list[k].Time > list[k - 1].Time) && new[] { list[k].Value, list[k].Control1, list[k].Control2 }.All(v => v >= range.Min && v <= range.Max), "invalid key for track " + name); tracks[name] = new(name, range.Min, range.Max, [.. list]); }
        return new(duration, tracks, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Path.GetFileName(path));
    }

    private static JsonObject Compare(Timeline left, Timeline right, double start, double end, int samples, double tolerance)
    {
        Tooling.Require(left.Duration == right.Duration, "timeline durations differ"); Tooling.Require(left.Tracks.Keys.ToHashSet().SetEquals(right.Tracks.Keys), "channel sets differ"); Tooling.Require(start >= 0 && end > start && end <= left.Duration, "aligned domain must be within timeline duration and have positive width"); Tooling.Require(samples is >= 2 and <= 100000 && tolerance >= 0, "invalid sampling parameters");
        foreach (var name in left.Tracks.Keys) Tooling.Require((left.Tracks[name].Minimum, left.Tracks[name].Maximum) == (right.Tracks[name].Minimum, right.Tracks[name].Maximum), "range differs for channel " + name);
        var declared = Enumerable.Range(0, samples).Select(i => i == 0 ? start : i == samples - 1 ? end : start + (end - start) * i / (samples - 1)).ToArray(); var times = declared.ToHashSet();
        foreach (var timeline in new[] { left, right }) foreach (var track in timeline.Tracks.Values) foreach (var key in track.Keys) if (key.Time >= start && key.Time <= end) times.Add(key.Time);
        Tooling.Require(times.Count <= 250000, "declared grid plus breakpoints exceeds sample limit"); var ordered = times.Order().ToArray(); var channels = new JsonArray(); var overall = new Rms(); var overallMax = 0d; (double Time, string Name)? first = null;
        foreach (var name in left.Tracks.Keys.Order()) { var rms = new Rms(); var max = 0d; double? channelFirst = null; var divergent = 0; foreach (var time in ordered) { var d = Math.Abs(left.Tracks[name].Evaluate(time) - right.Tracks[name].Evaluate(time)); Tooling.Require(double.IsFinite(d), "nonfinite deviation"); rms.Add(d); overall.Add(d); max = Math.Max(max, d); overallMax = Math.Max(overallMax, d); if (d > tolerance) { divergent++; channelFirst ??= time; if (first is null || (time, name).CompareTo(first.Value) < 0) first = (time, name); } } channels.Add(new JsonObject { { "name", name }, { "maxAbsDeviation", max }, { "rmsDeviation", rms.Value }, { "firstDivergenceTime", channelFirst }, { "divergentSamples", divergent }, { "withinTolerance", divergent == 0 } }); }
        return new JsonObject { { "schema", "ksp-continuum-input-comparison/v1" }, { "left", new JsonObject { { "filename", left.Filename }, { "sha256", left.Hash } } }, { "right", new JsonObject { { "filename", right.Filename }, { "sha256", right.Hash } } }, { "duration", left.Duration }, { "domain", new JsonObject { { "start", start }, { "end", end } } }, { "tolerance", tolerance }, { "declaredGridSamples", samples }, { "breakpointSamplesAdded", ordered.Length - declared.Distinct().Count() }, { "evaluatedSamples", ordered.Length }, { "sampledNotContinuous", true }, { "eventsCompared", false }, { "channels", channels }, { "overall", new JsonObject { { "maxAbsDeviation", overallMax }, { "rmsDeviation", overall.Value }, { "firstDivergence", first is null ? null : new JsonObject { { "time", first.Value.Time }, { "channel", first.Value.Name } } }, { "channelsWithinTolerance", channels.Count(x => x!["withinTolerance"]!.GetValue<bool>()) }, { "channelsCompared", channels.Count }, { "withinTolerance", first is null } } } };
    }
    private static string[] Fields(string line, int expected) { var f = line.Split(','); Tooling.Require(f.Length == expected, "timeline row has the wrong number of fields"); return f; }
    private static double Number(string value, string label) { Tooling.Require(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x), "invalid " + label); return x; }
    private static double Lerp(double a, double b, double u) => a == b ? a : a + (b - a) * u;
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)] private static partial Regex Id();
}
