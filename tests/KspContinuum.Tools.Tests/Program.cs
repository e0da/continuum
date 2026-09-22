using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using KspContinuum;

var root = FindRoot();
var project = Path.Combine(root, "tools/KspContinuum.Tools");
var temporary = Path.Combine(Path.GetTempPath(), "continuum-tools-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var timeline = "schema,ksp-continuum-input-timeline/v1\nduration,10\ntrack,name,min,max\ntrack,pitch,-1,1\nkey,track,time,value,mode,control1,control2\nkey,pitch,0,0,linear,0,0\nkey,pitch,10,1,step,1,1\nevent,time,name,value\n";
    var left = Path.Combine(temporary, "left.csv"); var right = Path.Combine(temporary, "right.csv"); var comparison = Path.Combine(temporary, "comparison.json"); File.WriteAllText(left, timeline); File.WriteAllText(right, timeline);
    Require(Run("compare-inputs", left, right, "--start", "0", "--end", "10", "--samples", "11", "--tolerance", "0", "--output", comparison) == 0, "comparison failed");
    var report = JsonNode.Parse(File.ReadAllText(comparison))!.AsObject(); Require(report["overall"]!["withinTolerance"]!.GetValue<bool>(), "identical timelines diverged");
    Require(Run("compare-inputs", left, right, "--start", "10", "--end", "0", "--samples", "11", "--tolerance", "0") != 0, "invalid domain accepted");

    var telemetry = Path.Combine(temporary, "mission.csv"); File.WriteAllText(telemetry, "wall_s,ut_s,phase,body,situation,altitude_m,surface_speed_mps,throttle,stage,parts,packed,autopilot\n0,1,launch,Kerbin,PRELAUNCH,70,0,0,0,17,False,off\n"); var player = Path.Combine(temporary, "player.html"); Require(Run("telemetry-player", telemetry, "--title", "Test flight", "--output", player) == 0 && File.ReadAllText(player).Contains("Test flight", StringComparison.Ordinal), "telemetry player failed");

    var mission = Path.Combine(temporary, "mission-session"); var inputs = Path.Combine(temporary, "inputs-session"); Directory.CreateDirectory(mission); Directory.CreateDirectory(inputs);
    File.WriteAllText(Path.Combine(mission, "mission.txt"), $"status=running\ninputDirectory={inputs}\nstatus=passed\nreason=Landed safely.\n");
    File.WriteAllText(Path.Combine(mission, "mission.csv"), "wall_s,ut_s,phase,body,situation,altitude_m,apoapsis_m,periapsis_m,surface_speed_mps,throttle,stage,parts,packed,autopilot\n0,100,Launch,Kerbin,FLYING,100,200,-10,50,1,2,17,False,ASCENT\n10,110,Landed,Minmus,LANDED,1,2,-1,0,0,1,17,False,IDLE\n");
    File.WriteAllText(Path.Combine(mission, "screenshots.csv"), "10,requested,landing.png\n10.1,png-written,landing.png\n"); File.WriteAllBytes(Path.Combine(mission, "landing.png"), [1, 2, 3]); File.WriteAllText(Path.Combine(inputs, "segment-00000.csv"), timeline);
    var metadata = Path.Combine(temporary, "metadata.json"); File.WriteAllText(metadata, new JsonObject { ["schema"] = "ksp-continuum-chronicle-metadata/v1", ["mission_id"] = "CSP-0001", ["name"] = "Minmus <Pathfinder>", ["attempt_id"] = "CSP-0001-A001", ["vehicle_design_id"] = "CV-0001-R01", ["objective"] = "Land safely", ["next_experiment"] = "Repeat" }.ToJsonString());
    var chronicle = Path.Combine(temporary, "render-1"); Require(Run("chronicle", "--mission", mission, "--inputs", inputs, "--output", chronicle, "--metadata", metadata) == 0, "chronicle failed");
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(chronicle, "manifest.json")))!.AsObject(); Require(manifest["outcome"]!.ToString() == "passed" && manifest["media"]!.AsArray().Count == 1 && File.Exists(Path.Combine(chronicle, "media", "landing.png")), "chronicle lost evidence"); Require(File.ReadAllText(Path.Combine(chronicle, "index.html")).Contains("Minmus &lt;Pathfinder&gt;", StringComparison.Ordinal), "chronicle did not escape metadata");
    var archive = temporary; var catalog = Path.Combine(archive, "catalog.json"); File.WriteAllText(catalog, new JsonObject { ["schema"] = "ksp-continuum-space-program/v1", ["program"] = new JsonObject { ["name"] = "Continuum Space Program" }, ["missions"] = new JsonArray(new JsonObject { ["id"] = "CSP-0001", ["name"] = "Minmus Pathfinder", ["summary"] = "First mission" }), ["vehicles"] = new JsonArray(), ["sites"] = new JsonArray(), ["experiments"] = new JsonArray() }.ToJsonString()); var site = Path.Combine(archive, "site"); Require(Run("space-program", "--archive", archive, "--catalog", catalog, "--output", site) == 0 && File.Exists(Path.Combine(site, "attempts", "CSP-0001-A001", "media", "landing.png")), "space program lost connected media");

    var qualification = Path.Combine(temporary, "qualification"); Directory.CreateDirectory(qualification); File.WriteAllText(Path.Combine(qualification, "scope.txt"), "bounded scope\n"); File.WriteAllText(Path.Combine(qualification, "status.txt"), "complete\n"); File.WriteAllText(Path.Combine(qualification, "coast-markers.json"), new JsonObject { ["status"] = "complete", ["requestedFrames"] = 2, ["completedFrames"] = 2, ["markers"] = new JsonArray(new JsonObject { ["name"] = "Physics.Simulate", ["status"] = "available-sampled" }) }.ToJsonString()); var qualificationOutput = Path.Combine(temporary, "qualification-report"); Require(Run("qualification-report", qualification, "--output", qualificationOutput) == 0 && File.ReadAllText(Path.Combine(qualificationOutput, "index.html")).Contains("Physics.Simulate", StringComparison.Ordinal), "qualification report lost markers");

    var shadow = Path.Combine(temporary, "shadow.json"); File.WriteAllText(shadow, new JsonObject { ["schema"] = "ksp-continuum-flight-shadow/v2", ["status"] = "complete", ["strategy"] = "rigid-cluster", ["samples"] = new JsonArray(new JsonObject { ["comparisonStatus"] = "compared", ["bodies"] = 2, ["observedPositionMaxMeters"] = 2.0, ["observedPositionRmsMeters"] = 1.0, ["observedVelocityMaxMetersPerSecond"] = 4.0, ["observedVelocityRmsMetersPerSecond"] = 3.0 }) }.ToJsonString()); var shadowOutput = Path.Combine(temporary, "shadow-summary.json"); Require(Run("shadow-report", "--input", shadow, "--output", shadowOutput) == 0 && JsonNode.Parse(File.ReadAllText(shadowOutput))!["positionResidualMeters"]!["comparedBodies"]!.GetValue<int>() == 2, "shadow report lost weighted residual population");

    var aero = Path.Combine(temporary, "aero.json"); File.WriteAllText(aero, ReportJson.Encode(AeroReceipt("1.12.5"))); var aeroOutput = Path.Combine(temporary, "aero-comparison.json");
    Require(Run("aero-compare", aero, "--output", aeroOutput) == 0, "aero comparison failed");
    var aeroComparison = JsonNode.Parse(File.ReadAllText(aeroOutput))!.AsObject();
    Require(aeroComparison["schema"]!.ToString() == "ksp-continuum-aero-comparison/v1", "aero comparison schema changed");
    Require(aeroComparison["counts"]!["capturedSamples"]!.GetValue<int>() == 1 && aeroComparison["counts"]!["bodyDragLabels"]!.GetValue<int>() == 2, "aero comparison lost labels");
    Require(aeroComparison["counts"]!["finiteCompared"]!.GetValue<int>() == 1 && aeroComparison["counts"]!["abstentions"]!.GetValue<int>() == 1, "aero comparison hid abstention");
    Require(aeroComparison["errors"]!["forceVectorNormNewtons"]!["maximum"]!.GetValue<double>() == 0, "exact baseline label diverged");
    var setDragMetrics = aeroComparison["setDragAreaReconstruction"]!;
    Require(setDragMetrics["finiteCompared"]!.GetValue<int>() == 2 &&
        setDragMetrics["absoluteErrorSquareMeters"]!["maximum"]!.GetValue<double>() == 0,
        "independent SetDrag area reconstruction diverged");
    Require(setDragMetrics["meetsNumericGate"]!.GetValue<bool>() &&
        !setDragMetrics["qualifiedHeldOutGate"]!.GetValue<bool>(),
        "portable SetDrag fixture either missed its numeric gate or overstated held-out qualification");
    Require(aeroComparison["setDragQualificationSplit"]!["heldOut"] is null,
        "ordinary comparison invented a held-out dataset");
    var development = Path.Combine(temporary, "aero-development.json");
    File.WriteAllText(development, ReportJson.Encode(AeroReceipt("1.12.5",
        "00000000-0000-0000-0000-000000000001", AeroCaptureReport.MaximumSamples)));
    var heldOut = Path.Combine(temporary, "aero-held-out.json");
    File.WriteAllText(heldOut, ReportJson.Encode(AeroReceipt("1.12.5",
        "00000000-0000-0000-0000-000000000003", AeroCaptureReport.MaximumSamples)));
    var qualifiedOutput = Path.Combine(temporary, "aero-qualified.json");
    Require(Run("aero-compare", development, "--held-out", heldOut, "--output", qualifiedOutput) == 0,
        "development/held-out comparison failed");
    var qualified = JsonNode.Parse(File.ReadAllText(qualifiedOutput))!.AsObject();
    Require(qualified["setDragQualificationSplit"]!["development"]!["meetsNumericGate"]!.GetValue<bool>() &&
        qualified["setDragQualificationSplit"]!["heldOut"]!["meetsNumericGate"]!.GetValue<bool>() &&
        qualified["setDragQualificationSplit"]!["qualifiedHeldOutGate"]!.GetValue<bool>() &&
        qualified["setDragAreaReconstruction"]!["qualifiedHeldOutGate"]!.GetValue<bool>(),
        "separate passing receipt did not satisfy held-out SetDrag gate");
    var partialOutput = Path.Combine(temporary, "aero-partial.json");
    Require(Run("aero-compare", aero, "--held-out", heldOut, "--output", partialOutput) == 0 &&
        !JsonNode.Parse(File.ReadAllText(partialOutput))!["setDragQualificationSplit"]!["qualifiedHeldOutGate"]!.GetValue<bool>() &&
        !JsonNode.Parse(File.ReadAllText(partialOutput))!["setDragQualificationSplit"]!["development"]!["completeCaptureReceipts"]!.GetValue<bool>(),
        "partial development receipt incorrectly qualified as trajectory evidence");
    Require(qualified["incompleteness"]!.AsArray().Any(item =>
        item!.ToString().Contains("explicit development/held-out split", StringComparison.Ordinal) &&
        item.ToString().Contains("craft-family and regime independence remain procedural", StringComparison.Ordinal)) &&
        !qualified["incompleteness"]!.AsArray().Any(item =>
            item!.ToString().StartsWith("No train/test split", StringComparison.Ordinal)),
        "held-out report contradicted its admitted split or overstated independence");
    Require(Run("aero-compare", aero, "--held-out", aero) != 0,
        "same receipt was accepted as development and held-out evidence");
    var sameSession = Path.Combine(temporary, "aero-same-session.json");
    File.WriteAllText(sameSession, JsonNode.Parse(File.ReadAllText(aero))!.ToJsonString(new() { WriteIndented = true }));
    Require(Run("aero-compare", aero, "--held-out", sameSession) != 0,
        "reformatted receipt from the development capture session was accepted as held-out evidence");
    var scalarDiagnostics = aeroComparison["stockDragScalarDiagnostics"]!;
    Require(scalarDiagnostics["count"]!.GetValue<int>() == 2, "stock diagnostics did not cover every body-drag label");
    Require(scalarDiagnostics["scope"]!.ToString() ==
        "all body-drag rows; no-ocean-multiplier product; submerged samples indistinguishable",
        "stock diagnostics overstated their ability to identify non-submerged samples");
    Require(aeroComparison["incompleteness"]!.AsArray().Any(item =>
        item!.ToString().Contains("disagreement may reflect the omitted ocean multiplier", StringComparison.Ordinal)),
        "stock diagnostics did not explain possible submerged-sample disagreement");
    Require(scalarDiagnostics["reconstructedForceMagnitudeNewtons"]!["maximum"]!.GetValue<double>() == 60,
        "stock scalar product did not reconstruct newtons");
    Require(scalarDiagnostics["dragScalarForceMagnitudeNewtons"]!["maximum"]!.GetValue<double>() == 60,
        "stock drag scalar was not converted from kilonewtons to newtons");
    Require(scalarDiagnostics["reconstructedToDragScalar"]!["absoluteErrorNewtons"]!["maximum"]!.GetValue<double>() == 0 &&
        scalarDiagnostics["reconstructedToDragScalar"]!["agreementRatio"]!["mean"]!.GetValue<double>() == 1,
        "stock scalar decomposition disagreed despite equivalent SI magnitudes");
    Require(scalarDiagnostics["reconstructedToObserved"]!["absoluteErrorNewtons"]!["maximum"]!.GetValue<double>() == 0 &&
        scalarDiagnostics["dragScalarToObserved"]!["absoluteErrorNewtons"]!["maximum"]!.GetValue<double>() == 0,
        "stock scalar decomposition disagreed with observed force magnitude");
    Require(aeroComparison["counts"]!["bodyLiftLabelsExcluded"]!.GetValue<int>() == 1 && !aeroComparison["qualifiedForAuthority"]!.GetValue<bool>(), "aero comparison overstated coverage");
    var otherAero = Path.Combine(temporary, "aero-other.json"); File.WriteAllText(otherAero, ReportJson.Encode(AeroReceipt("1.12.5-other")));
    Require(Run("aero-compare", aero, otherAero) != 0, "mixed providers accepted");
    var malformedAero = Path.Combine(temporary, "aero-malformed.json"); File.WriteAllText(malformedAero, "{\"schema\":\"ksp-continuum-aero-capture/v3\",\"disposition\":\"Valid\"}");
    Require(Run("aero-compare", malformedAero) != 0, "malformed aero receipt accepted");
    var alteredCurve = Path.Combine(temporary, "aero-altered-curve.json");
    var alteredRoot = JsonNode.Parse(File.ReadAllText(aero))!.AsObject();
    alteredRoot["samples"]![0]!["publications"]![0]!["context"]!["setDragInputs"]!["dragCurveCd"]!["keys"]![0]!["value"] = 2;
    File.WriteAllText(alteredCurve, alteredRoot.ToJsonString());
    Require(Run("aero-compare", alteredCurve) != 0, "curve parameters changed without a matching content address");

    var plugin = Path.Combine(temporary, "KspContinuum.dll"); File.WriteAllBytes(plugin, [4, 5, 6]);
    var nativeLibrary = Path.Combine(temporary, "libcontinuum_native_boundary.dylib"); WriteMachO(nativeLibrary, 0x01000007);
    var package = Path.Combine(temporary, "continuum.zip"); var download = "https://packages.example.invalid/ksp-continuum.zip";
    Require(Run("package", "--plugin", plugin, "--native-library", nativeLibrary, "--output", package, "--download-url", download) == 0, "package generation failed");
    var packageMetadata = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(package, ".ckan")))!.AsObject();
    Require(packageMetadata["identifier"]!.ToString() == "KspContinuum", "package identifier changed");
    Require(packageMetadata["version"]!.ToString().StartsWith("0.2.0-aero.", StringComparison.Ordinal), "qualification package version is not above the installed 0.1.x line");
    Require(packageMetadata["release_status"]!.ToString() == "testing", "local package was presented as a release");
    Require(packageMetadata["license"]!.ToString() == "restricted", "package claimed an ungranted license");
    Require(packageMetadata["depends"]!.AsArray().Any(item => item!["name"]!.ToString() == "Harmony2"), "package omitted shared Harmony2 dependency");
    Require(packageMetadata["install"]!.AsArray().Single()!["find"]!.ToString() == "KspContinuum", "package install root changed");
    Require(packageMetadata["download"]!.ToString() == download, "metadata lost the reachable download URL override");
    Require(packageMetadata["download_size"]!.GetValue<long>() == new FileInfo(package).Length, "metadata archive size changed");
    Require(packageMetadata["download_hash"]!["sha256"]!.ToString() == Sha256(package).ToUpperInvariant(), "metadata does not bind exact archive with CKAN-compatible hash casing");
    Require(packageMetadata["download_hash"]!["sha1"]!.ToString().All(character => !char.IsLetter(character) || char.IsUpper(character)), "CKAN SHA-1 hash is not uppercase");
    using (var packaged = ZipFile.OpenRead(package))
    {
        Require(!packaged.Entries.Any(entry => string.Equals(Path.GetFileName(entry.FullName), "0Harmony.dll", StringComparison.OrdinalIgnoreCase)), "package bundled Harmony runtime");
        var native = packaged.GetEntry("GameData/KspContinuum/Plugins/libcontinuum_native_boundary.dylib") ?? throw new InvalidOperationException("package omitted native boundary");
        using var nativeStream = native.Open(); using var nativeBytes = new MemoryStream(); nativeStream.CopyTo(nativeBytes);
        var nativeManifest = packaged.GetEntry("GameData/KspContinuum/Plugins/libcontinuum_native_boundary.dylib.sha256") ?? throw new InvalidOperationException("package omitted native boundary hash");
        using var reader = new StreamReader(nativeManifest.Open());
        Require(reader.ReadToEnd() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(nativeBytes.ToArray())).ToLowerInvariant() + "  libcontinuum_native_boundary.dylib\n", "native boundary hash does not bind packaged bytes");
    }
    var localPackageHint = Path.Combine(temporary, "local-package.zip");
    Require(Run("package", "--plugin", plugin, "--native-library", nativeLibrary, "--output", localPackageHint) == 0, "local package generation failed");
    var firstLocalArchive = Directory.GetFiles(temporary, "local-package-*.zip").Single();
    var firstLocalMetadata = Path.ChangeExtension(firstLocalArchive, ".ckan");
    var firstLocalHash = Sha256(firstLocalArchive);
    var firstLocalCkan = JsonNode.Parse(File.ReadAllText(firstLocalMetadata))!.AsObject();
    Require(Path.GetFileNameWithoutExtension(firstLocalArchive).EndsWith(firstLocalHash, StringComparison.Ordinal), "local archive name is not content-addressed");
    Require(firstLocalCkan["download"]!.ToString() == new Uri(firstLocalArchive).AbsoluteUri, "local metadata does not download its content-addressed archive");
    File.WriteAllBytes(plugin, [7, 8, 9]);
    Require(Run("package", "--plugin", plugin, "--native-library", nativeLibrary, "--output", localPackageHint) == 0, "changed local package generation failed");
    var localArchives = Directory.GetFiles(temporary, "local-package-*.zip").Order().ToArray();
    Require(localArchives.Length == 2, "changed package bytes reused or replaced the prior local archive path");
    Require(localArchives.All(path => File.Exists(Path.ChangeExtension(path, ".ckan"))), "content-addressed archive is missing sibling metadata");
    Require(localArchives.Select(Sha256).Distinct(StringComparer.Ordinal).Count() == 2, "changed plugin bytes produced the same package bytes");
    foreach (var localArchive in localArchives)
    {
        var localHash = Sha256(localArchive);
        var localCkan = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(localArchive, ".ckan")))!.AsObject();
        Require(Path.GetFileNameWithoutExtension(localArchive).EndsWith(localHash, StringComparison.Ordinal), "local package filename does not match its bytes");
        Require(localCkan["download"]!.ToString() == new Uri(localArchive).AbsoluteUri, "local package metadata points CKAN at a stale archive path");
        Require(localCkan["download_hash"]!["sha256"]!.ToString() == localHash.ToUpperInvariant(), "local metadata hash does not match its named archive");
    }
    var rejectedPackage = Path.Combine(temporary, "invalid-download.zip");
    Require(Run("package", "--plugin", plugin, "--native-library", nativeLibrary, "--output", rejectedPackage, "--download-url", "relative/package.zip") != 0 && !File.Exists(rejectedPackage), "package accepted a relative download URL");
    Require(Run("package", "--plugin", plugin, "--native-library", nativeLibrary, "--output", rejectedPackage, "--download-url", "ftp://packages.example.invalid/continuum.zip") != 0 && !File.Exists(rejectedPackage), "package accepted an unsupported download URL scheme");
    var armLibrary = Path.Combine(temporary, "arm64.dylib"); WriteMachO(armLibrary, 0x0100000c);
    Require(Run("package", "--plugin", plugin, "--native-library", armLibrary, "--output", rejectedPackage) != 0 && !File.Exists(rejectedPackage), "package accepted an arm64 native boundary for x86_64 KSP");
    var executable = Path.Combine(temporary, "executable"); WriteMachO(executable, 0x01000007, 2);
    Require(Run("package", "--plugin", plugin, "--native-library", executable, "--output", rejectedPackage) != 0 && !File.Exists(rejectedPackage), "package accepted a Mach-O executable as its native library");
    Console.WriteLine("KspContinuum.Tools.Tests passed"); return 0;
}
finally { Directory.Delete(temporary, true); }

int Run(params string[] arguments) { var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true }; start.ArgumentList.Add("run"); start.ArgumentList.Add("--project"); start.ArgumentList.Add(project); start.ArgumentList.Add("-c"); start.ArgumentList.Add("Release"); start.ArgumentList.Add("--no-build"); start.ArgumentList.Add("--"); foreach (var argument in arguments) start.ArgumentList.Add(argument); using var process = Process.Start(start)!; process.WaitForExit(); if (process.ExitCode != 0) Console.Error.Write(process.StandardError.ReadToEnd()); return process.ExitCode; }
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
string FindRoot() { var current = new DirectoryInfo(AppContext.BaseDirectory); while (current is not null && !File.Exists(Path.Combine(current.FullName, "README.md"))) current = current.Parent; return current?.FullName ?? throw new InvalidOperationException("repository root not found"); }
void WriteMachO(string path, uint cpuType, uint fileType = 6) { var bytes = new byte[32]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), cpuType); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), fileType); File.WriteAllBytes(path, bytes); }
AeroCaptureReport AeroReceipt(string version, string sessionId = "00000000-0000-0000-0000-000000000001", int sampleCount = 1)
{
    const string hash = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
    var provider = new AeroProviderFingerprint("stock-flight-integrator", version, "Assembly-CSharp", hash, "10657063-2fc3-43a7-84fa-d39e75e877bf");
    AeroPatchEntry Entry(string method, string kind, int index, int priority = AeroPatchEntry.DefaultPriority) => new("continuum.capture", method, kind, index, hash, priority);
    var provenance = new AeroPatchProvenance(provider, "continuum.capture", [
        new AeroPatchTarget("FlightIntegrator.UpdateAerodynamics", [
            Entry("KspContinuum.AeroCapture.UpdatePrefix", "prefix", 0),
            Entry("KspContinuum.AeroCapture.UpdatePostfix", "postfix", 1, AeroPatchEntry.PriorityLast),
            Entry("KspContinuum.AeroCapture.UpdateFinalizer", "finalizer", 2, AeroPatchEntry.PriorityLast)]),
        new AeroPatchTarget("FlightIntegrator.ApplyAeroDrag", [Entry("KspContinuum.AeroCapture.DragPrefix", "prefix", 0)]),
        new AeroPatchTarget("FlightIntegrator.ApplyAeroLift", [Entry("KspContinuum.AeroCapture.LiftPrefix", "prefix", 0)])]);
    AeroCaptureContext Step(int epoch, int ordinal) => new(sessionId, "00000000-0000-0000-0000-000000000002", "frame", epoch, 1, 1, epoch, 1, ordinal, 100 + epoch, 2 + epoch * .02, .02);
    var faces = new[] { 1d, 1, 1, 1, 1, 1 };
    var curve = new AeroFloatCurveDefinition(0, 0, [new AeroCurveKey(0, 1, 0, 0, 0, 0, 0)]);
    var setDragInputs = new AeroSetDragInputs([0, 3, 0, 0, 0, 0], faces,
        new AeroSurfaceCurveDefinitions(curve, curve, curve, curve), curve, curve);
    var emptySetDragInputs = new AeroSetDragInputs(new double[6], faces,
        new AeroSurfaceCurveDefinitions(curve, curve, curve, curve), curve, curve);
    AeroPartContext Part(int epoch, long id, int ordinal, bool cubes) => new(Step(epoch, ordinal), id, (int)id + 10, (int)id + 20, 10, 1.2, 100000, 280, 330, .5, 1, 1, false,
        new Vec(id, 0, 0), new Vec(-10, 0, 0), new Vec(10, 0, 0), new Vec(), new Vec(), 1,
        cubes ? [new AeroDragCubeState("Default", 1, new Vec(), new Vec(1, 1, 1), faces, faces, faces, faces)] : [],
        cubes ? setDragInputs : emptySetDragInputs);
    AeroCaptureSample Sample(int epoch) {
        var dragContext = Part(epoch, 1, 0, true); var exact = AeroDragCubeBaseline.Evaluate(dragContext);
        var drag = new AeroBodyPublication(dragContext, AeroPublicationKind.BodyDrag, AeroApplicationMode.AtWorldPosition,
            exact.ForceNewtons, exact.WorldApplicationPosition, exact.TorqueAboutPartCenterOfMassNewtonMeters,
            new AeroStockDragScalars(3, 2, 4, 5, .5, .06));
        var liftContext = Part(epoch, 1, 1, true); var lift = new AeroBodyPublication(liftContext, AeroPublicationKind.BodyLift,
            AeroApplicationMode.AtCenterOfMass, new Vec(), liftContext.worldCenterOfMass, new Vec());
        var absentContext = Part(epoch, 2, 2, false); var absent = new AeroBodyPublication(absentContext, AeroPublicationKind.BodyDrag,
            AeroApplicationMode.AtCenterOfMass, new Vec(), absentContext.worldCenterOfMass, new Vec(),
            new AeroStockDragScalars(0, 60, 1, 1, 1, 0));
        return new AeroCaptureSample(Step(epoch, 3), [drag, lift, absent]);
    }
    return new AeroCaptureReport(provenance, AeroCaptureDisposition.Valid, AeroCaptureReason.None,
        AeroCleanupOutcome.RemovedOwnedPatches, Enumerable.Range(1, sampleCount).Select(Sample).ToArray());
}
string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
