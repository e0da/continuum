using System.Globalization;
using System.Text.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: KspContinuum.Pressure [--target-step-seconds SECONDS] markers.json [...]");
    return 1;
}

double targetStep = 0.02;
var paths = new List<string>();
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--target-step-seconds" && ++index < args.Length &&
        double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value) && value > 0)
        targetStep = value;
    else if (args[index].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException("Unknown option or invalid target step: " + args[index]);
    else paths.Add(args[index]);
}
if (paths.Count == 0) throw new ArgumentException("At least one markers.json path is required.");

var results = new List<object>();
foreach (string path in paths)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
    JsonElement root = document.RootElement;
    Require(root.GetProperty("schema").GetString() == "ksp-continuum-markers/v2" &&
        root.GetProperty("status").GetString() == "complete", path + ": unsupported or incomplete marker receipt");
    JsonElement frames = root.GetProperty("frames");
    Require(frames.GetArrayLength() >= 2 && frames.GetArrayLength() == root.GetProperty("completedFrames").GetInt32() &&
        root.GetProperty("contextMisalignedFrames").GetInt32() == 0 &&
        root.GetProperty("cleanupErrors").GetArrayLength() == 0 &&
        root.GetProperty("recorderCleanupStatus").GetString() == "restored-owned-enables",
        path + ": incomplete, misaligned, or unclean capture");
    JsonElement first = frames[0], last = frames[frames.GetArrayLength() - 1];
    string[] stableFields = { "vesselId", "body", "situation", "parts", "rigidbodies", "joints", "colliders",
        "loadedVessels", "packed", "paused", "warpRate", "fixedDeltaSeconds", "timeScale" };
    bool stable = true;
    foreach (JsonElement frame in frames.EnumerateArray())
    {
        Require(frame.GetProperty("contextAligned").GetBoolean(), path + ": misaligned context frame");
        foreach (string field in stableFields)
            if (frame.GetProperty(field).ToString() != first.GetProperty(field).ToString()) stable = false;
    }
    double wallSeconds = Number(last, "boundaryWallSeconds") - Number(first, "boundaryWallSeconds");
    double simulatedSeconds = Number(last, "universalTime") - Number(first, "universalTime");
    double requestedWarp = Number(first, "warpRate");
    double fixedStep = Number(first, "fixedDeltaSeconds");
    if (wallSeconds <= 0 || simulatedSeconds < 0 || requestedWarp <= 0 || fixedStep <= 0)
        throw new InvalidDataException(path + ": clock interval, requested warp and fixed step must be positive");

    JsonElement playerLoop = root.GetProperty("playerLoop");
    Require(playerLoop.GetProperty("schema").GetString() == "ksp-continuum-playerloop/v2" &&
        playerLoop.GetProperty("status").GetString() == "observed" &&
        playerLoop.GetProperty("integrityStatus").GetString() == "verified-at-boundaries" &&
        playerLoop.GetProperty("cleanupStatus").GetString() == "removed-owned-hooks",
        path + ": unqualified PlayerLoop capture");
    JsonElement scopes = playerLoop.GetProperty("scopes");
    (double physics, int physicsSamples) = Mean(scopes, "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate", "fixed");
    (double scripts, int scriptSamples) = Mean(scopes, "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate", "fixed");
    (double update, int updateSamples) = Mean(scopes, "UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate", "frame");
    bool physicsDomain = stable && first.GetProperty("loaded").GetBoolean() &&
        !first.GetProperty("packed").GetBoolean() && !first.GetProperty("paused").GetBoolean() &&
        physicsSamples > 0 && scriptSamples == physicsSamples;
    double achievedWarp = simulatedSeconds / wallSeconds;
    double fixedChildMs = physics + scripts;
    results.Add(new {
        source = Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path),
        observedBoundaryFieldsStable = stable,
        pressureQualified = physicsDomain,
        reason = physicsDomain ? "loaded-unpacked-stable-observed-boundaries" : "pressure estimate requires stable observed loaded unpacked boundaries and matched child counts",
        factors = new {
            logicalProcessors = root.GetProperty("processorCount").GetInt32(),
            body = first.GetProperty("body").GetString(),
            situation = first.GetProperty("situation").GetString(),
            parts = first.GetProperty("parts").GetInt32(),
            rigidbodies = first.GetProperty("rigidbodies").GetInt32(),
            joints = first.GetProperty("joints").GetInt32(),
            colliders = first.GetProperty("colliders").GetInt32(),
            loadedVessels = first.GetProperty("loadedVessels").GetInt32(),
            packed = first.GetProperty("packed").GetBoolean()
        },
        clock = new {
            wallSeconds, simulatedSeconds, requestedWarp, achievedWarp,
            warpFulfillment = achievedWarp / requestedWarp,
            simulatedTimeDebtSecondsPerWallSecond = Math.Max(0, requestedWarp - achievedWarp)
        },
        measured = new {
            physicsChildMeanMilliseconds = physics,
            scriptChildMeanMilliseconds = scripts,
            fixedChildMeanMilliseconds = fixedChildMs,
            fixedChildSamples = physicsSamples,
            updateMeanMillisecondsPerRenderFrame = update,
            updateSamples
        },
        pressure = physicsDomain ? new {
            currentStepSeconds = fixedStep,
            targetStepSeconds = targetStep,
            requestedStepsPerWallSecondAtCurrentStep = requestedWarp / fixedStep,
            requestedStepsPerWallSecondAtTargetStep = requestedWarp / targetStep,
            measuredFixedChildWallBudgetFractionAtCurrentStep = fixedChildMs * requestedWarp / (1000 * fixedStep),
            measuredFixedChildWallBudgetFractionAtTargetStep = fixedChildMs * requestedWarp / (1000 * targetStep)
        } : null
    });
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static double Number(JsonElement element, string name) => element.GetProperty(name).GetDouble();

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static (double mean, int count) Mean(JsonElement scopes, string name, string timeDomain)
{
    JsonElement? found = null;
    foreach (JsonElement scope in scopes.EnumerateArray())
        if (scope.GetProperty("name").GetString() == name)
        { Require(found == null, "Duplicate timing scope: " + name); found = scope; }
    JsonElement row = found ?? throw new InvalidDataException("Missing timing scope: " + name);
    JsonElement milliseconds = row.GetProperty("milliseconds");
    int count = milliseconds.GetProperty("count").GetInt32();
    double mean = Number(milliseconds, "mean");
    Require(row.GetProperty("status").GetString() == "observed" &&
        row.GetProperty("timeDomain").GetString() == timeDomain &&
        row.GetProperty("sequenceErrors").GetInt32() == 0 &&
        row.GetProperty("droppedSamples").GetInt32() == 0 &&
        count == row.GetProperty("samples").GetArrayLength() && count > 0 &&
        double.IsFinite(mean) && mean >= 0,
        "Unqualified timing scope: " + name);
    return (mean, count);
}
