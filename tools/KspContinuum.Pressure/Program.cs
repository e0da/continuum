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
    JsonElement frames = root.GetProperty("frames");
    if (frames.GetArrayLength() < 2) throw new InvalidDataException(path + ": requires two or more context frames");
    JsonElement first = frames[0], last = frames[frames.GetArrayLength() - 1];
    string[] stableFields = { "vesselId", "body", "situation", "parts", "rigidbodies", "joints", "colliders",
        "loadedVessels", "packed", "paused", "warpRate", "fixedDeltaSeconds", "timeScale" };
    bool stable = true;
    foreach (JsonElement frame in frames.EnumerateArray())
        foreach (string field in stableFields)
            if (frame.GetProperty(field).ToString() != first.GetProperty(field).ToString()) stable = false;
    double wallSeconds = Number(last, "boundaryWallSeconds") - Number(first, "boundaryWallSeconds");
    double simulatedSeconds = Number(last, "universalTime") - Number(first, "universalTime");
    double requestedWarp = Number(first, "warpRate");
    double fixedStep = Number(first, "fixedDeltaSeconds");
    if (wallSeconds <= 0 || simulatedSeconds < 0 || requestedWarp <= 0 || fixedStep <= 0)
        throw new InvalidDataException(path + ": clock interval, requested warp and fixed step must be positive");

    JsonElement scopes = root.GetProperty("playerLoop").GetProperty("scopes");
    (double physics, int physicsSamples) = Mean(scopes, "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate");
    (double scripts, int scriptSamples) = Mean(scopes, "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate");
    (double update, int updateSamples) = Mean(scopes, "UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate");
    bool physicsDomain = stable && first.GetProperty("loaded").GetBoolean() &&
        !first.GetProperty("packed").GetBoolean() && !first.GetProperty("paused").GetBoolean() &&
        physicsSamples > 0 && scriptSamples == physicsSamples;
    double achievedWarp = simulatedSeconds / wallSeconds;
    double fixedChildMs = physics + scripts;
    results.Add(new {
        source = Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path),
        stableContext = stable,
        pressureQualified = physicsDomain,
        reason = physicsDomain ? "loaded-unpacked-stable-physics-domain" : "pressure estimate requires stable loaded unpacked physics and matched child counts",
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

static (double mean, int count) Mean(JsonElement scopes, string name)
{
    foreach (JsonElement scope in scopes.EnumerateArray())
        if (scope.GetProperty("name").GetString() == name)
        {
            JsonElement milliseconds = scope.GetProperty("milliseconds");
            return (Number(milliseconds, "mean"), milliseconds.GetProperty("count").GetInt32());
        }
    return (0, 0);
}
