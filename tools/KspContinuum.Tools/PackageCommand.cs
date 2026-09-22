using System.IO.Compression;

namespace KspContinuum.Tools;

internal static class PackageCommand
{
    private static readonly string[] Docs = ["experiment.md", "replacement.md", "validation.md", "compatibility.md", "input-timeline.md", "integration-map.md", "minmus-mission.md", "space-program.md", "naming.md", "wiki-templates.md", "chronicle.md", "simulation-worker.md", "profiling.md", "input-comparison.md", "worker-benchmark.md", "structural-benchmark.md", "orbital-fixture.md", "layout-benchmark.md", "telemetry-playback.md", "shadow-worker.md", "field-gravity.md", "field-trajectory.md", "modal-reduction.md", "interaction-regimes.md", "encounter-scheduler.md", "worldline-tubes.md", "force-observation.md", "qualification-report.md", "learned-compute.md", "aerodynamics.md", "aero-behavior.md"];

    public static int Run(string[] args)
    {
        var parsed = new Arguments(args, "--mission");
        var root = Tooling.Root();
        var mission = parsed.Has("--mission");
        var plugin = Path.Combine(root, "src/KspContinuum.Plugin/bin/Release/net472/KspContinuum.dll");
        Tooling.Require(File.Exists(plugin), "Build the Release plugin first.");
        var addon = Path.Combine(root, "src/KspContinuum.Mission/bin/Release/net48/KspContinuum.Mission.dll");
        Tooling.Require(!mission || File.Exists(addon), "Build the Release mission addon first.");
        var output = Path.Combine(root, "artifacts", mission ? "ksp-continuum-0.1.0-mission.zip" : "ksp-continuum-0.1.0-experiment.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
        Add(archive, plugin, "GameData/KspContinuum/Plugins/KspContinuum.dll");
        if (mission) Add(archive, addon, "GameData/KspContinuum/Plugins/KspContinuum.Mission.dll");
        Add(archive, Path.Combine(root, "README.md"), "GameData/KspContinuum/README.md");
        foreach (var name in Docs) Add(archive, Path.Combine(root, "docs", name), "GameData/KspContinuum/docs/" + name);
        Add(archive, Path.Combine(root, "examples/neutral-inputs.csv"), "GameData/KspContinuum/examples/neutral-inputs.csv");
        Console.WriteLine(Path.GetFileName(output));
        return 0;
    }

    private static void Add(ZipArchive archive, string source, string destination)
    {
        Tooling.Require(File.Exists(source), "missing package input: " + source);
        archive.CreateEntryFromFile(source, destination, CompressionLevel.Optimal);
    }
}
