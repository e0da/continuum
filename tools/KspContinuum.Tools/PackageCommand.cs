using System.IO.Compression;
using System.Text.Json.Nodes;

namespace KspContinuum.Tools;

internal static class PackageCommand
{
    private static readonly string[] Docs = ["experiment.md", "replacement.md", "validation.md", "compatibility.md", "input-timeline.md", "integration-map.md", "minmus-mission.md", "space-program.md", "naming.md", "wiki-templates.md", "chronicle.md", "simulation-worker.md", "profiling.md", "input-comparison.md", "worker-benchmark.md", "structural-benchmark.md", "orbital-fixture.md", "layout-benchmark.md", "telemetry-playback.md", "shadow-worker.md", "field-gravity.md", "field-trajectory.md", "modal-reduction.md", "interaction-regimes.md", "encounter-scheduler.md", "worldline-tubes.md", "force-observation.md", "qualification-report.md", "learned-compute.md", "aerodynamics.md", "aero-behavior.md"];

    public static int Run(string[] args)
    {
        var parsed = new Arguments(args, "--mission");
        var root = Tooling.Root();
        var mission = parsed.Has("--mission");
        var plugin = parsed.Optional("--plugin") ?? Path.Combine(root, "src/KspContinuum.Plugin/bin/Release/net472/KspContinuum.dll");
        Tooling.Require(File.Exists(plugin), "Build the Release plugin first.");
        var addon = parsed.Optional("--mission-addon") ?? Path.Combine(root, "src/KspContinuum.Mission/bin/Release/net48/KspContinuum.Mission.dll");
        Tooling.Require(!mission || File.Exists(addon), "Build the Release mission addon first.");
        var output = Path.GetFullPath(parsed.Optional("--output") ?? Path.Combine(root, "artifacts", mission ? "ksp-continuum-0.1.0-mission.zip" : "ksp-continuum-0.1.0-experiment.zip"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Delete(output);
        using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            Add(archive, plugin, "GameData/KspContinuum/Plugins/KspContinuum.dll");
            if (mission) Add(archive, addon, "GameData/KspContinuum/Plugins/KspContinuum.Mission.dll");
            Add(archive, Path.Combine(root, "README.md"), "GameData/KspContinuum/README.md");
            foreach (var name in Docs) Add(archive, Path.Combine(root, "docs", name), "GameData/KspContinuum/docs/" + name);
            Add(archive, Path.Combine(root, "examples/neutral-inputs.csv"), "GameData/KspContinuum/examples/neutral-inputs.csv");
        }
        VerifyArchive(output);
        var metadata = Metadata(output, mission);
        var metadataPath = Path.ChangeExtension(output, ".ckan");
        File.Delete(metadataPath);
        Tooling.WriteJsonNew(metadataPath, metadata);
        Console.WriteLine(Path.GetFileName(output));
        Console.WriteLine(Path.GetFileName(metadataPath));
        return 0;
    }

    private static void Add(ZipArchive archive, string source, string destination)
    {
        Tooling.Require(File.Exists(source), "missing package input: " + source);
        archive.CreateEntryFromFile(source, destination, CompressionLevel.Optimal);
    }

    private static void VerifyArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        Tooling.Require(!archive.Entries.Any(entry => string.Equals(Path.GetFileName(entry.FullName), "0Harmony.dll", StringComparison.OrdinalIgnoreCase)), "package must use shared Harmony2 and cannot bundle 0Harmony.dll");
    }

    private static JsonObject Metadata(string archive, bool mission)
    {
        var sha256 = Tooling.Sha256(archive);
        var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(archive))).ToLowerInvariant();
        var flavor = mission ? "mission" : "aero";
        return new JsonObject
        {
            ["spec_version"] = "v1.34",
            ["identifier"] = "KspContinuum",
            ["name"] = "KSP Continuum local experiment",
            ["abstract"] = "Local qualification build for deterministic, scalable KSP simulation experiments.",
            ["author"] = new JsonArray("e0da"),
            ["version"] = $"0.1.0-{flavor}.{sha256[..12]}",
            ["ksp_version"] = "1.12.5",
            ["license"] = "restricted",
            ["release_status"] = "testing",
            ["depends"] = new JsonArray(new JsonObject { ["name"] = "Harmony2" }),
            ["install"] = new JsonArray(new JsonObject { ["find"] = "KspContinuum", ["install_to"] = "GameData" }),
            ["download"] = new Uri(archive).AbsoluteUri,
            ["download_size"] = new FileInfo(archive).Length,
            ["download_hash"] = new JsonObject { ["sha1"] = sha1, ["sha256"] = sha256 },
        };
    }
}
