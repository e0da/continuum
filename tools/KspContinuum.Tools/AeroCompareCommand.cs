using System.Text.Json.Nodes;
using KspContinuum;

namespace KspContinuum.Tools;

internal static class AeroCompareCommand
{
    private const string CaptureSchema = "ksp-continuum-aero-capture/v3";
    private const long MaximumCaptureBytes = 32 * 1024 * 1024;
    private const string ComparisonSchema = "ksp-continuum-aero-comparison/v1";

    public static int Run(string[] args)
    {
        var options = new Arguments(args);
        Tooling.Require(options.Positionals.Count > 0,
            "usage: aero-compare DEVELOPMENT_RECEIPT [DEVELOPMENT_RECEIPT ...] [--held-out RECEIPT] [--output FILE]");
        var heldOutPath = options.Optional("--held-out");
        var developmentPaths = options.Positionals.Select(Path.GetFullPath).ToArray();
        if (heldOutPath is not null)
        {
            heldOutPath = Path.GetFullPath(heldOutPath);
            Tooling.Require(!developmentPaths.Contains(heldOutPath, StringComparer.Ordinal),
                "held-out receipt must not also be a development receipt");
        }
        var developmentReports = developmentPaths.Select(Parse).ToArray();
        var heldOutReport = heldOutPath is null ? null : Parse(heldOutPath);
        if (heldOutReport is not null)
        {
            var heldOutHash = Tooling.Sha256(heldOutPath!);
            Tooling.Require(developmentPaths.All(path => Tooling.Sha256(path) != heldOutHash),
                "held-out receipt bytes must differ from every development receipt");
            Tooling.Require(heldOutReport.SessionId is not null &&
                developmentReports.All(report => report.SessionId is null || report.SessionId != heldOutReport.SessionId),
                "held-out receipt must come from a different capture session");
        }
        var reports = heldOutReport is null ? developmentReports : developmentReports.Append(heldOutReport).ToArray();
        var provider = reports[0].Provider;
        Tooling.Require(reports.All(report => report.Provider == provider),
            "capture receipts contain mixed providers or provider versions");
        Tooling.Require(provider.Name == "stock-flight-integrator",
            "only stock-flight-integrator body-drag labels are supported");

        var rows = new List<Row>();
        var scalarRows = new List<StockDragScalarRow>();
        var setDragRows = new List<SetDragRow>();
        var setDragAbstentions = new Dictionary<string, int>(StringComparer.Ordinal);
        var abstentions = new Dictionary<string, int>(StringComparer.Ordinal);
        var bodyLiftLabels = 0;
        foreach (var report in reports)
        foreach (var publication in report.Publications)
        {
            if (publication.Kind == AeroPublicationKind.BodyLift) { bodyLiftLabels++; continue; }
            scalarRows.Add(new StockDragScalarRow(publication));
            var setDrag = AeroSetDragReconstruction.Evaluate(publication.Context);
            if (setDrag.Disposition == AeroSetDragDisposition.Valid)
                setDragRows.Add(new SetDragRow(publication, setDrag));
            else
            {
                var reason = setDrag.Reason.ToString();
                setDragAbstentions[reason] = setDragAbstentions.GetValueOrDefault(reason) + 1;
            }
            var candidate = AeroDragCubeBaseline.Evaluate(publication.Context);
            if (candidate.Disposition == AeroBaselineDisposition.Abstained)
            {
                var reason = candidate.Reason.ToString();
                abstentions[reason] = abstentions.GetValueOrDefault(reason) + 1;
                continue;
            }
            RequireFinite(candidate.ForceNewtons, "candidate force");
            RequireFinite(candidate.TorqueAboutPartCenterOfMassNewtonMeters, "candidate torque");
            Tooling.Require(double.IsFinite(candidate.DynamicPressurePascals) &&
                double.IsFinite(candidate.WeightedProjectedAreaSquareMeters), "candidate scalar output is nonfinite");
            rows.Add(new Row(publication, candidate));
        }

        var developmentSetDrag = EvaluateSetDrag(developmentReports);
        var heldOutSetDrag = heldOutReport is null ? null : EvaluateSetDrag(new[] { heldOutReport });
        var qualifiedHeldOutGate = developmentSetDrag.MeetsNumericGate && developmentSetDrag.HasCompleteReceipts &&
            heldOutSetDrag is not null && heldOutSetDrag.MeetsNumericGate && heldOutSetDrag.HasCompleteReceipts;
        var output = new JsonObject
        {
            ["schema"] = ComparisonSchema,
            ["strategy"] = AeroDragCubeBaseline.Strategy,
            ["qualifiedForAuthority"] = false,
            ["provider"] = provider.ToJson(),
            ["counts"] = new JsonObject
            {
                ["inputReceipts"] = reports.Length,
                ["capturedSamples"] = reports.Sum(report => report.SampleCount),
                ["bodyDragLabels"] = reports.Sum(report => report.BodyDragCount),
                ["bodyLiftLabelsExcluded"] = bodyLiftLabels,
                ["finiteCompared"] = rows.Count,
                ["abstentions"] = abstentions.Values.Sum()
            },
            ["abstentionsByReason"] = Object(abstentions),
            ["errors"] = Metrics(rows),
            ["setDragAreaReconstruction"] = SetDragMetrics(setDragRows, setDragAbstentions,
                qualifiedHeldOutGate),
            ["setDragQualificationSplit"] = new JsonObject
            {
                ["development"] = SetDragQualificationMetrics(developmentSetDrag, false),
                ["heldOut"] = heldOutSetDrag is null
                    ? null
                    : SetDragQualificationMetrics(heldOutSetDrag, qualifiedHeldOutGate),
                ["qualifiedHeldOutGate"] = qualifiedHeldOutGate
            },
            ["stockDragScalarDiagnostics"] = StockDragScalarMetrics(scalarRows),
            ["regimes"] = new JsonArray(Regimes(rows).Select(item => (JsonNode)item).ToArray()),
            ["incompleteness"] = new JsonArray(
                "One-step body-drag force labels only; body lift and lifting surfaces are excluded.",
                heldOutSetDrag is null
                    ? "SetDrag area reconstruction is independent of the captured stock AreaDrag label, but requires a separate complete receipt for a held-out qualification split."
                    : "Development and held-out receipts are compared separately; held-out qualification covers SetDrag area only.",
                "Stock drag scalar reconstruction evaluates the no-ocean-multiplier product for every body-drag row because capture v3 cannot identify submerged samples; disagreement may reflect the omitted ocean multiplier.",
                "The baseline omits Mach curves, pseudo-Reynolds corrections, stock drag-cube interpolation details, shielding transitions, heating, and provider-specific clamps.",
                "Application-point torque is compared, but no angular impulse or trajectory behavior is qualified.",
                "Receipt-wide provenance detects mixed input receipts; the capture schema has no per-publication provider fingerprint.",
                heldOutSetDrag is null
                    ? "No train/test split or repeated-provider envelope is established by this report."
                    : "Receipt roles establish an explicit development/held-out split, but craft-family and regime independence remain procedural and no repeated-provider envelope is established.")
        };
        var destination = FindOption(args, "--output");
        if (destination is null) Console.WriteLine(output.ToJsonString(Tooling.Json));
        else Tooling.WriteJsonNew(destination, output);
        return 0;
    }

    private static JsonObject Metrics(IReadOnlyList<Row> rows) => new()
    {
        ["forceMagnitudeAbsoluteNewtons"] = Distribution(rows.Select(row => row.ForceMagnitudeError)),
        ["forceVectorNormNewtons"] = Distribution(rows.Select(row => row.ForceVectorError)),
        ["forceDirectionDegrees"] = Distribution(rows.Where(row => row.HasDirection).Select(row => row.DirectionErrorDegrees)),
        ["torqueVectorNormNewtonMeters"] = Distribution(rows.Select(row => row.TorqueError))
    };

    private static SetDragEvaluation EvaluateSetDrag(IEnumerable<ParsedReport> reports)
    {
        var rows = new List<SetDragRow>();
        var abstentions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var publication in reports.SelectMany(report => report.Publications)
            .Where(publication => publication.Kind == AeroPublicationKind.BodyDrag))
        {
            var result = AeroSetDragReconstruction.Evaluate(publication.Context);
            if (result.Disposition == AeroSetDragDisposition.Valid) rows.Add(new SetDragRow(publication, result));
            else
            {
                var reason = result.Reason.ToString();
                abstentions[reason] = abstentions.GetValueOrDefault(reason) + 1;
            }
        }
        return new SetDragEvaluation(rows, abstentions, MeetsNumericGate(rows, abstentions),
            reports.All(report => report.SampleCount == AeroCaptureReport.MaximumSamples));
    }

    private static bool MeetsNumericGate(IReadOnlyList<SetDragRow> rows, Dictionary<string, int> abstentions)
    {
        var relative = rows.Where(row => row.StockAreaDragSquareMeters > 1e-5).Select(row => row.RelativeError).ToArray();
        var nearZero = rows.Where(row => row.StockAreaDragSquareMeters <= 1e-5).Select(row => row.AbsoluteErrorSquareMeters).ToArray();
        return rows.Count > 0 && abstentions.Count == 0 &&
            PercentileOrInfinity(relative, .99) <= 1e-5 &&
            (relative.Length == 0 ? 0 : relative.Max()) <= 1e-4 &&
            (nearZero.Length == 0 ? 0 : nearZero.Max()) <= 1e-5;
    }

    private static JsonObject SetDragMetrics(IReadOnlyList<SetDragRow> rows, Dictionary<string, int> abstentions,
        bool qualifiedHeldOutGate)
    {
        var relative = rows.Where(row => row.StockAreaDragSquareMeters > 1e-5).Select(row => row.RelativeError).ToArray();
        var absoluteNearZero = rows.Where(row => row.StockAreaDragSquareMeters <= 1e-5).Select(row => row.AbsoluteErrorSquareMeters).ToArray();
        return new JsonObject
        {
            ["scope"] = "stock DragCubeList.SetDrag area only; lift excluded",
            ["finiteCompared"] = rows.Count,
            ["abstentions"] = abstentions.Values.Sum(),
            ["abstentionsByReason"] = Object(abstentions),
            ["absoluteErrorSquareMeters"] = Distribution(rows.Select(row => row.AbsoluteErrorSquareMeters)),
            ["relativeErrorForStockAreaAbove1e-5"] = Distribution(relative),
            ["absoluteErrorForStockAreaAtMost1e-5"] = Distribution(absoluteNearZero),
            ["meetsNumericGate"] = MeetsNumericGate(rows, abstentions),
            ["qualifiedHeldOutGate"] = qualifiedHeldOutGate
        };
    }

    private static JsonObject SetDragQualificationMetrics(SetDragEvaluation evaluation, bool qualifiedHeldOutGate)
    {
        var metrics = SetDragMetrics(evaluation.Rows, evaluation.Abstentions, qualifiedHeldOutGate);
        metrics["completeCaptureReceipts"] = evaluation.HasCompleteReceipts;
        metrics["expectedSamplesPerReceipt"] = AeroCaptureReport.MaximumSamples;
        return metrics;
    }

    private static double PercentileOrInfinity(double[] values, double probability)
    {
        if (values.Length == 0) return 0;
        System.Array.Sort(values); return Percentile(values, probability);
    }

    private static JsonObject StockDragScalarMetrics(IReadOnlyList<StockDragScalarRow> rows) => new()
    {
        ["scope"] = "all body-drag rows; no-ocean-multiplier product; submerged samples indistinguishable",
        ["count"] = rows.Count,
        ["reconstructedForceMagnitudeNewtons"] = Distribution(rows.Select(row => row.ReconstructedMagnitudeNewtons)),
        ["dragScalarForceMagnitudeNewtons"] = Distribution(rows.Select(row => row.DragScalarMagnitudeNewtons)),
        ["observedForceMagnitudeNewtons"] = Distribution(rows.Select(row => row.ObservedMagnitudeNewtons)),
        ["reconstructedToDragScalar"] = Comparison(
            rows.Select(row => row.ReconstructedToDragScalarAbsoluteErrorNewtons),
            rows.Select(row => row.ReconstructedToDragScalarAgreementRatio)),
        ["reconstructedToObserved"] = Comparison(
            rows.Select(row => row.ReconstructedToObservedAbsoluteErrorNewtons),
            rows.Select(row => row.ReconstructedToObservedAgreementRatio)),
        ["dragScalarToObserved"] = Comparison(
            rows.Select(row => row.DragScalarToObservedAbsoluteErrorNewtons),
            rows.Select(row => row.DragScalarToObservedAgreementRatio))
    };

    private static JsonObject Comparison(IEnumerable<double> absoluteErrors, IEnumerable<double> agreementRatios) => new()
    {
        ["absoluteErrorNewtons"] = Distribution(absoluteErrors),
        ["agreementRatio"] = Distribution(agreementRatios)
    };

    private static IEnumerable<JsonObject> Regimes(IReadOnlyList<Row> rows)
    {
        foreach (var definition in new[] {
            ("zero-dynamic-pressure", 0d, 0d), ("subsonic", 0d, .8),
            ("transonic", .8, 1.2), ("supersonic", 1.2, 5d), ("hypersonic", 5d, double.PositiveInfinity) })
        {
            var bucket = rows.Where(row => definition.Item1 == "zero-dynamic-pressure"
                ? row.DynamicPressure == 0
                : row.DynamicPressure > 0 && row.Mach >= definition.Item2 && row.Mach < definition.Item3).ToArray();
            yield return new JsonObject
            {
                ["name"] = definition.Item1,
                ["finiteCompared"] = bucket.Length,
                ["forceVectorNormNewtons"] = Distribution(bucket.Select(row => row.ForceVectorError)),
                ["torqueVectorNormNewtonMeters"] = Distribution(bucket.Select(row => row.TorqueError))
            };
        }
    }

    private static JsonObject Distribution(IEnumerable<double> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0) return new JsonObject { ["count"] = 0 };
        return new JsonObject
        {
            ["count"] = values.Length,
            ["mean"] = values.Average(),
            ["rms"] = Math.Sqrt(values.Average(value => value * value)),
            ["p50"] = Percentile(values, .5),
            ["p95"] = Percentile(values, .95),
            ["p99"] = Percentile(values, .99),
            ["maximum"] = values[^1]
        };
    }

    private static double Percentile(double[] sorted, double probability)
    {
        var position = probability * (sorted.Length - 1);
        var lower = (int)Math.Floor(position); var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static JsonObject Object(Dictionary<string, int> values)
    {
        var result = new JsonObject();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) result[pair.Key] = pair.Value;
        return result;
    }

    private static ParsedReport Parse(string path)
    {
        var root = Tooling.ReadObject(path, MaximumCaptureBytes);
        Tooling.Require(Text(root, "schema") == CaptureSchema, $"{path}: unsupported capture schema");
        Tooling.Require(Text(root, "disposition") == "Valid", $"{path}: capture is not valid");
        Tooling.Require(Text(root, "reason") == "None", $"{path}: valid capture has a failure reason");
        Tooling.Require(Text(root, "cleanup") == "RemovedOwnedPatches", $"{path}: capture cleanup is unconfirmed");
        var provenance = Child(root, "provenance"); var providerNode = Child(provenance, "provider");
        var provider = new Provider(Text(providerNode, "provider"), Text(providerNode, "providerVersion"),
            Text(providerNode, "assemblyName"), Hex(providerNode, "assemblySha256"), GuidText(providerNode, "assemblyMvid"));
        Tooling.Require(Text(provenance, "captureOwner") == "continuum.capture", $"{path}: unexpected capture owner");
        ValidatePatchGraph(provenance, path);
        var samples = Array(root, "samples");
        Tooling.Require(samples.Count <= AeroCaptureReport.MaximumSamples, $"{path}: sample bound exceeded");
        var publications = new List<Publication>();
        string? session = null; long previousEpoch = 0;
        foreach (var sampleNode in samples)
        {
            var sample = Object(sampleNode, "sample"); var sampleContext = ParseStep(Child(sample, "context"));
            session ??= sampleContext.sessionId;
            Tooling.Require(sampleContext.sessionId == session && sampleContext.physicsEpoch > previousEpoch,
                $"{path}: samples must share one session and increase by physics epoch");
            previousEpoch = sampleContext.physicsEpoch;
            var seen = new HashSet<string>(StringComparer.Ordinal); var ordinals = new HashSet<int>();
            var parts = new Dictionary<long, AeroPartContext>();
            var items = Array(sample, "publications");
            Tooling.Require(items.Count <= AeroCaptureReport.MaximumPartsPerSample * 2, $"{path}: publication bound exceeded");
            foreach (var item in items)
            {
                var publication = Object(item, "publication"); var context = ParsePart(Child(publication, "context"));
                Tooling.Require(sampleContext.SameStep(context.step), $"{path}: publication step mismatch");
                Tooling.Require(ordinals.Add(context.step.callOrdinal), $"{path}: duplicate call ordinal");
                var kind = EnumValue<AeroPublicationKind>(publication, "kind");
                Tooling.Require(seen.Add(context.flightId + ":" + kind), $"{path}: duplicate part publication kind");
                if (parts.TryGetValue(context.flightId, out var prior)) Tooling.Require(prior.SameState(context), $"{path}: drag/lift state mismatch");
                parts[context.flightId] = context;
                var dragScalars = kind == AeroPublicationKind.BodyDrag
                    ? ParseDragScalars(Child(publication, "stockDragScalars"))
                    : null;
                if (kind == AeroPublicationKind.BodyLift)
                    Tooling.Require(publication["stockDragScalars"] is null, $"{path}: lift publication has drag scalars");
                var label = new AeroBodyPublication(context, kind, EnumValue<AeroApplicationMode>(publication, "applicationMode"),
                    Vector(publication, "forceNewtons"), Vector(publication, "worldApplicationPosition"),
                    Vector(publication, "torqueAboutPartCenterOfMassNewtonMeters"), dragScalars);
                publications.Add(new Publication(label));
            }
            Tooling.Require(parts.Count <= AeroCaptureReport.MaximumPartsPerSample, $"{path}: part bound exceeded");
        }
        return new ParsedReport(provider, samples.Count, session, publications);
    }

    private static void ValidatePatchGraph(JsonObject provenance, string path)
    {
        var targets = Array(provenance, "targets");
        var required = new HashSet<string>(new[] { "FlightIntegrator.UpdateAerodynamics", "FlightIntegrator.ApplyAeroDrag", "FlightIntegrator.ApplyAeroLift" }, StringComparer.Ordinal);
        var expected = new HashSet<string>(new[] {
            "FlightIntegrator.UpdateAerodynamics|continuum.capture|KspContinuum.AeroCapture.UpdatePrefix|prefix|400",
            "FlightIntegrator.UpdateAerodynamics|continuum.capture|KspContinuum.AeroCapture.UpdatePostfix|postfix|0",
            "FlightIntegrator.UpdateAerodynamics|continuum.capture|KspContinuum.AeroCapture.UpdateFinalizer|finalizer|0",
            "FlightIntegrator.ApplyAeroDrag|continuum.capture|KspContinuum.AeroCapture.DragPrefix|prefix|400",
            "FlightIntegrator.ApplyAeroLift|continuum.capture|KspContinuum.AeroCapture.LiftPrefix|prefix|400" }, StringComparer.Ordinal);
        foreach (var targetNode in targets)
        {
            var target = Object(targetNode, "patch target"); var name = Text(target, "targetMethod");
            Tooling.Require(required.Remove(name), $"{path}: duplicate or unexpected patch target");
            var patches = Array(target, "orderedPatches");
            for (var index = 0; index < patches.Count; index++)
            {
                var patch = Object(patches[index], "patch");
                Tooling.Require(Integer(patch, "executionIndex") == index, $"{path}: noncontiguous patch order");
                var identity = name + "|" + Text(patch, "owner") + "|" + Text(patch, "patchMethod") + "|" +
                    Text(patch, "patchKind") + "|" + Integer(patch, "harmonyPriority");
                expected.Remove(identity);
                _ = Hex(patch, "assemblySha256"); _ = Array(patch, "beforeOwners"); _ = Array(patch, "afterOwners");
            }
        }
        Tooling.Require(required.Count == 0 && targets.Count == 3 && expected.Count == 0,
            $"{path}: incomplete stock aero patch graph");
    }

    private static AeroCaptureContext ParseStep(JsonObject value) => new(Text(value, "sessionId"), Text(value, "vesselId"),
        Text(value, "frameKey"), Long(value, "physicsEpoch"), Long(value, "topologyGeneration"), Long(value, "frameGeneration"),
        Integer(value, "unityFrame"), Integer(value, "mainThreadId"), Integer(value, "callOrdinal"), Number(value, "universalTime"),
        Number(value, "fixedTimeSeconds"), Number(value, "stepSeconds"));

    private static AeroPartContext ParsePart(JsonObject value)
    {
        var cubes = Array(value, "dragCubes").Select(node =>
        {
            var cube = Object(node, "drag cube");
            return new AeroDragCubeState(Text(cube, "name"), Number(cube, "weight"), Vector(cube, "center"), Vector(cube, "size"),
                Numbers(cube, "area", 6), Numbers(cube, "drag", 6), Numbers(cube, "depth", 6), Numbers(cube, "dragModifiers", 6));
        }).ToArray();
        var setDrag = Child(value, "setDragInputs");
        var surface = Child(setDrag, "surfaceCurves");
        var setDragInputs = new AeroSetDragInputs(Numbers(setDrag, "areaOccludedSquareMeters", 6),
            Numbers(setDrag, "weightedDragCoefficients", 6),
            new AeroSurfaceCurveDefinitions(ParseCurve(Child(surface, "tail")), ParseCurve(Child(surface, "surface")),
                ParseCurve(Child(surface, "multiplier")), ParseCurve(Child(surface, "tip"))),
            ParseCurve(Child(setDrag, "dragCurveCd")), ParseCurve(Child(setDrag, "dragCurveCdPower")));
        return new AeroPartContext(ParseStep(Child(value, "step")), Long(value, "flightId"), Integer(value, "nativePartInstanceId"),
            Integer(value, "nativeRigidbodyInstanceId"), Number(value, "massKilograms"), Number(value, "densityKilogramsPerCubicMeter"),
            Number(value, "staticPressurePascals"), Number(value, "temperatureKelvin"), Number(value, "speedOfSoundMetersPerSecond"),
            Number(value, "mach"), Number(value, "aerodynamicAreaSquareMeters"), Number(value, "exposedAreaSquareMeters"),
            Boolean(value, "shielded"), Vector(value, "worldCenterOfMass"), Vector(value, "worldVelocity"),
            Vector(value, "relativeAirVelocity"), Vector(value, "worldAngularVelocity"), Vector(value, "worldAttitudeXYZ"),
            Number(value, "worldAttitudeW"), cubes, setDragInputs);
    }

    private static AeroFloatCurveDefinition ParseCurve(JsonObject value)
    {
        var keys = Array(value, "keys").Select(node =>
        {
            var key = Object(node, "curve key");
            return new AeroCurveKey(Number(key, "time"), Number(key, "value"), Number(key, "inTangent"),
                Number(key, "outTangent"), Number(key, "inWeight"), Number(key, "outWeight"), Integer(key, "weightedMode"));
        }).ToArray();
        var curve = new AeroFloatCurveDefinition(Integer(value, "preWrapMode"), Integer(value, "postWrapMode"), keys);
        Tooling.Require(curve.contentSha256 == Hex(value, "contentSha256"), "curve content hash does not match its parameters");
        return curve;
    }

    private static AeroStockDragScalars ParseDragScalars(JsonObject value) => new(
        Number(value, "areaDragSquareMeters"), Number(value, "dynamicPressurePascals"),
        Number(value, "pseudoReynoldsDragMultiplier"), Number(value, "cachedDragCubeMultiplier"),
        Number(value, "cachedGlobalDragMultiplier"), Number(value, "dragScalarKilonewtons"));

    private static Vec Vector(JsonObject owner, string name)
    { var value = Child(owner, name); return new Vec(Number(value, "X"), Number(value, "Y"), Number(value, "Z")); }
    private static double[] Numbers(JsonObject owner, string name, int count)
    { var values = Array(owner, name); Tooling.Require(values.Count == count, $"{name} requires {count} values"); return values.Select((node, index) => Tooling.Finite(node, $"{name}[{index}]")).ToArray(); }
    private static JsonObject Child(JsonObject owner, string name) => owner[name] as JsonObject ?? throw new ToolException($"{name} must be an object");
    private static JsonObject Object(JsonNode? node, string name) => node as JsonObject ?? throw new ToolException($"{name} must be an object");
    private static JsonArray Array(JsonObject owner, string name) => owner[name] as JsonArray ?? throw new ToolException($"{name} must be an array");
    private static string Text(JsonObject owner, string name) => Tooling.Text(owner[name], name);
    private static double Number(JsonObject owner, string name) => Tooling.Finite(owner[name], name);
    private static int Integer(JsonObject owner, string name) { var value = Long(owner, name); Tooling.Require(value >= int.MinValue && value <= int.MaxValue, $"{name} is outside Int32"); return (int)value; }
    private static long Long(JsonObject owner, string name) => owner[name]?.GetValue<long>() ?? throw new ToolException($"{name} must be an integer");
    private static bool Boolean(JsonObject owner, string name) => owner[name]?.GetValue<bool>() ?? throw new ToolException($"{name} must be boolean");
    private static string Hex(JsonObject owner, string name) { var value = Text(owner, name).ToLowerInvariant(); Tooling.Require(value.Length == 64 && value.All(Uri.IsHexDigit), $"{name} must be SHA-256 hex"); return value; }
    private static string GuidText(JsonObject owner, string name) { var value = Text(owner, name); Tooling.Require(Guid.TryParse(value, out var parsed) && parsed != Guid.Empty, $"{name} must be a nonempty GUID"); return parsed.ToString("D"); }
    private static T EnumValue<T>(JsonObject owner, string name) where T : struct, Enum
    { var value = Text(owner, name); Tooling.Require(Enum.TryParse<T>(value, false, out var parsed) && Enum.IsDefined(parsed), $"{name} is unsupported"); return parsed; }
    private static void RequireFinite(Vec value, string name) => Tooling.Require(double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z), $"{name} is nonfinite");
    private static string? FindOption(string[] args, string name) { for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1]; return null; }

    private sealed record ParsedReport(Provider Provider, int SampleCount, string? SessionId,
        IReadOnlyList<Publication> Publications)
    { public int BodyDragCount => Publications.Count(item => item.Kind == AeroPublicationKind.BodyDrag); }
    private sealed record SetDragEvaluation(IReadOnlyList<SetDragRow> Rows,
        Dictionary<string, int> Abstentions, bool MeetsNumericGate, bool HasCompleteReceipts);
    private sealed record Provider(string Name, string Version, string Assembly, string Sha256, string Mvid)
    { public JsonObject ToJson() => new() { ["name"] = Name, ["version"] = Version, ["assembly"] = Assembly, ["assemblySha256"] = Sha256, ["assemblyMvid"] = Mvid }; }
    private sealed class Publication
    {
        public Publication(AeroBodyPublication value) { Context = value.context; Kind = value.kind; Force = value.forceNewtons; Torque = value.torqueAboutPartCenterOfMassNewtonMeters; DragScalars = value.stockDragScalars; }
        public AeroPartContext Context { get; } public AeroPublicationKind Kind { get; } public Vec Force { get; } public Vec Torque { get; }
        public AeroStockDragScalars? DragScalars { get; }
    }
    private sealed class StockDragScalarRow
    {
        public StockDragScalarRow(Publication label)
        {
            var scalars = label.DragScalars ?? throw new ToolException("body-drag publication is missing stock drag scalars");
            ReconstructedMagnitudeNewtons = scalars.dynamicPressurePascals * scalars.areaDragSquareMeters *
                scalars.pseudoReynoldsDragMultiplier * scalars.cachedDragCubeMultiplier * scalars.cachedGlobalDragMultiplier;
            DragScalarMagnitudeNewtons = scalars.dragScalarKilonewtons * 1000;
            ObservedMagnitudeNewtons = Length(label.Force);
            ReconstructedToDragScalarAbsoluteErrorNewtons = Math.Abs(ReconstructedMagnitudeNewtons - DragScalarMagnitudeNewtons);
            ReconstructedToObservedAbsoluteErrorNewtons = Math.Abs(ReconstructedMagnitudeNewtons - ObservedMagnitudeNewtons);
            DragScalarToObservedAbsoluteErrorNewtons = Math.Abs(DragScalarMagnitudeNewtons - ObservedMagnitudeNewtons);
            ReconstructedToDragScalarAgreementRatio = AgreementRatio(ReconstructedMagnitudeNewtons, DragScalarMagnitudeNewtons);
            ReconstructedToObservedAgreementRatio = AgreementRatio(ReconstructedMagnitudeNewtons, ObservedMagnitudeNewtons);
            DragScalarToObservedAgreementRatio = AgreementRatio(DragScalarMagnitudeNewtons, ObservedMagnitudeNewtons);
            foreach (var value in new[] { ReconstructedMagnitudeNewtons, DragScalarMagnitudeNewtons, ObservedMagnitudeNewtons,
                ReconstructedToDragScalarAbsoluteErrorNewtons, ReconstructedToObservedAbsoluteErrorNewtons,
                DragScalarToObservedAbsoluteErrorNewtons, ReconstructedToDragScalarAgreementRatio,
                ReconstructedToObservedAgreementRatio, DragScalarToObservedAgreementRatio })
                Tooling.Require(double.IsFinite(value), "stock drag scalar diagnostic is nonfinite");
        }
        public double ReconstructedMagnitudeNewtons { get; }
        public double DragScalarMagnitudeNewtons { get; }
        public double ObservedMagnitudeNewtons { get; }
        public double ReconstructedToDragScalarAbsoluteErrorNewtons { get; }
        public double ReconstructedToObservedAbsoluteErrorNewtons { get; }
        public double DragScalarToObservedAbsoluteErrorNewtons { get; }
        public double ReconstructedToDragScalarAgreementRatio { get; }
        public double ReconstructedToObservedAgreementRatio { get; }
        public double DragScalarToObservedAgreementRatio { get; }

        private static double AgreementRatio(double left, double right)
        {
            var maximum = Math.Max(left, right);
            return maximum == 0 ? 1 : Math.Min(left, right) / maximum;
        }
        private static double Length(Vec value) => Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
    }
    private sealed class SetDragRow
    {
        public SetDragRow(Publication label, AeroSetDragResult candidate)
        {
            var scalars = label.DragScalars ?? throw new ToolException("body-drag publication is missing stock drag scalars");
            StockAreaDragSquareMeters = scalars.areaDragSquareMeters;
            AbsoluteErrorSquareMeters = Math.Abs(candidate.AreaDragSquareMeters - StockAreaDragSquareMeters);
            RelativeError = AbsoluteErrorSquareMeters / Math.Max(Math.Abs(StockAreaDragSquareMeters), 1e-30);
            Tooling.Require(double.IsFinite(AbsoluteErrorSquareMeters) && double.IsFinite(RelativeError),
                "SetDrag reconstruction metric is nonfinite");
        }
        public double StockAreaDragSquareMeters { get; }
        public double AbsoluteErrorSquareMeters { get; }
        public double RelativeError { get; }
    }
    private sealed class Row
    {
        public Row(Publication label, AeroBaselineResult candidate)
        {
            DynamicPressure = candidate.DynamicPressurePascals; Mach = label.Context.mach;
            var expectedMagnitude = Length(label.Force); var actualMagnitude = Length(candidate.ForceNewtons);
            ForceMagnitudeError = Math.Abs(actualMagnitude - expectedMagnitude);
            ForceVectorError = Length(Subtract(candidate.ForceNewtons, label.Force));
            TorqueError = Length(Subtract(candidate.TorqueAboutPartCenterOfMassNewtonMeters, label.Torque));
            HasDirection = expectedMagnitude > 0 && actualMagnitude > 0;
            if (HasDirection) DirectionErrorDegrees = Math.Acos(Math.Clamp(Dot(label.Force, candidate.ForceNewtons) / (expectedMagnitude * actualMagnitude), -1, 1)) * 180 / Math.PI;
            Tooling.Require(double.IsFinite(DynamicPressure) && double.IsFinite(ForceMagnitudeError) &&
                double.IsFinite(ForceVectorError) && double.IsFinite(TorqueError) &&
                (!HasDirection || double.IsFinite(DirectionErrorDegrees)), "comparison metric is nonfinite");
        }
        public double DynamicPressure { get; } public double Mach { get; } public double ForceMagnitudeError { get; }
        public double ForceVectorError { get; } public double TorqueError { get; } public bool HasDirection { get; }
        public double DirectionErrorDegrees { get; }
        private static double Length(Vec value) => Math.Sqrt(Dot(value, value));
        private static Vec Subtract(Vec left, Vec right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        private static double Dot(Vec left, Vec right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    }
}
