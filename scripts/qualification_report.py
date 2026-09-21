#!/usr/bin/env python3
"""Summarize bounded native flight profiling and shadow-transport receipts."""
import argparse
import hashlib
import html
import json
import math
import os
from pathlib import Path
import re
import shutil
import sys


PHASES = ("coast", "powered", "contact")
MAX_FILE_BYTES = 16 * 1024 * 1024
MAX_TOTAL_BYTES = 64 * 1024 * 1024
MAX_FRAMES = 600
MAX_SHADOW_SAMPLES = 512
MAX_BODIES = 512
SAFE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z")
SAFE_TEXT = re.compile(r"[A-Za-z0-9_. ():+-]{0,160}\Z")


class ReportError(ValueError):
    pass


def finite(value, label, minimum=0.0, maximum=1e15):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ReportError(label + " must be a finite number")
    number = float(value)
    if not math.isfinite(number):
        raise ReportError(label + " must be finite")
    if number < minimum or number > maximum:
        raise ReportError(label + " is outside its numeric bound")
    return number


def integer(value, label, minimum=0, maximum=MAX_FRAMES):
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum or value > maximum:
        raise ReportError(label + " must be a bounded integer")
    return value


def token(value, label, allow_null=False):
    if value is None and allow_null:
        return None
    if not isinstance(value, str) or not SAFE_TEXT.fullmatch(value):
        raise ReportError(label + " is invalid")
    return value


def distribution(values):
    if not values:
        return None
    ordered = sorted(values)
    mean = 0.0
    for index, value in enumerate(values, 1):
        mean += (value - mean) / index

    def percentile(fraction):
        position = (len(ordered) - 1) * fraction
        lower = int(position)
        upper = min(lower + 1, len(ordered) - 1)
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)

    return {
        "count": len(values), "minimum": ordered[0], "maximum": ordered[-1],
        "mean": mean, "p50": percentile(0.5), "p95": percentile(0.95),
        "p99": percentile(0.99),
    }


class SourceReader:
    def __init__(self, root):
        self.root = root
        self.total = 0
        self.sources = []

    def bytes(self, name):
        if not SAFE_NAME.fullmatch(name):
            raise ReportError("unsafe source filename")
        path = self.root / name
        if path.is_symlink():
            raise ReportError("source may not be symbolic: " + name)
        if not path.is_file():
            raise ReportError("missing source: " + name)
        with path.open("rb") as stream:
            raw = stream.read(MAX_FILE_BYTES + 1)
        if len(raw) > MAX_FILE_BYTES:
            raise ReportError("source exceeds byte limit: " + name)
        self.total += len(raw)
        if self.total > MAX_TOTAL_BYTES:
            raise ReportError("sources exceed total byte limit")
        self.sources.append({
            "path": name, "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest(),
        })
        return raw

    def text(self, name):
        try:
            return self.bytes(name).decode("utf-8")
        except UnicodeDecodeError as error:
            raise ReportError("source is not valid UTF-8: " + name) from error

    def json(self, name):
        raw = self.text(name)
        def unique_object(pairs):
            result = {}
            for key, value in pairs:
                if key in result:
                    raise ReportError("duplicate JSON field: " + key)
                result[key] = value
            return result
        try:
            value = json.loads(raw, parse_constant=lambda item: (_ for _ in ()).throw(
                ReportError("JSON number must be finite: " + name)), object_pairs_hook=unique_object)
        except json.JSONDecodeError as error:
            raise ReportError("invalid JSON: " + name) from error
        if not isinstance(value, dict):
            raise ReportError("JSON report must be an object: " + name)
        return value


def parse_status(text):
    lines = text.splitlines()
    if len(lines) != 2 or lines[0] not in ("complete", "timeout", "error", "interrupted"):
        raise ReportError("invalid qualification status")
    if not re.fullmatch(r"completedWindows=[0-3]", lines[1]):
        raise ReportError("invalid completed window count")
    return lines[0], int(lines[1][-1])


def parse_key_values(text, label):
    result = {}
    for line in text.splitlines():
        if "=" not in line:
            raise ReportError("invalid " + label)
        key, value = line.split("=", 1)
        if key in result:
            raise ReportError("duplicate " + label + " field")
        result[key] = value
    return result


def parse_start(text, phase):
    values = parse_key_values(text, phase + " start context")
    if set(values) != {"vessel", "situation", "parts", "ut", "throttleCommand"}:
        raise ReportError("invalid " + phase + " start context fields")
    if not re.fullmatch(r"[0-9A-Fa-f-]{1,64}", values["vessel"]):
        raise ReportError("invalid start vessel")
    try:
        parts = int(values["parts"])
        ut = float(values["ut"])
        throttle = float(values["throttleCommand"])
    except ValueError as error:
        raise ReportError("invalid numeric start context") from error
    integer(parts, "start parts", 0, MAX_BODIES)
    finite(ut, "start UT")
    finite(throttle, "start throttle", 0, 1)
    return {
        "vessel": values["vessel"], "situation": token(values["situation"], "start situation"),
        "parts": parts, "ut": ut, "throttleCommand": throttle,
    }


def parse_source_distribution(value, label, allow_null=False):
    if value is None and allow_null:
        return None
    if not isinstance(value, dict):
        raise ReportError(label + " distribution is invalid")
    keys = {"count", "minimum", "maximum", "mean", "p50", "p95", "p99"}
    if set(value) != keys:
        raise ReportError(label + " distribution fields are invalid")
    count = integer(value["count"], label + " count", 1, MAX_FRAMES)
    result = {"count": count}
    for key in keys - {"count"}:
        result[key] = finite(value[key], label + " " + key)
    if not result["minimum"] <= result["mean"] <= result["maximum"]:
        raise ReportError(label + " distribution range is invalid")
    if any(not result["minimum"] <= result[key] <= result["maximum"] for key in ("p50", "p95", "p99")):
        raise ReportError(label + " percentile is outside its range")
    return result


def parse_profiler(data, phase):
    if data.get("schema") != "ksp-continuum-markers/v2":
        raise ReportError("unsupported marker schema for " + phase)
    status = data.get("status")
    if status not in ("complete", "interrupted", "cleanup-error"):
        raise ReportError("invalid profiler status for " + phase)
    requested = integer(data.get("requestedFrames"), "requested profiler frames", 1, MAX_FRAMES)
    completed = integer(data.get("completedFrames"), "completed profiler frames", 0, MAX_FRAMES)
    frames = data.get("frames")
    if not isinstance(frames, list) or len(frames) != completed:
        raise ReportError("profiler frame count does not match completedFrames")
    if completed > requested:
        raise ReportError("completed profiler frames exceed requested frames")
    intervals = []
    throttle = {"nearZero": 0, "positive": 0, "other": 0, "unknown": 0}
    packed = {"true": 0, "false": 0, "unknown": 0}
    bodies, situations = {}, {}
    structural_names = ("rigidbodies", "joints", "colliders", "loadedVessels")
    structural = {name: {"values": [], "unavailableFrames": 0} for name in structural_names}
    structural_frames = 0
    aligned = 0
    for frame in frames:
        if not isinstance(frame, dict):
            raise ReportError("profiler frame is invalid")
        structural_presence = [name in frame for name in structural_names]
        if any(structural_presence) and not all(structural_presence):
            raise ReportError("structural frame fields must be a complete set")
        if all(structural_presence):
            structural_frames += 1
            for name in structural_names:
                value = integer(frame[name], "frame " + name, -1, 2 ** 31 - 1)
                if value == -1:
                    structural[name]["unavailableFrames"] += 1
                else:
                    structural[name]["values"].append(value)
        intervals.append(finite(frame.get("wallMilliseconds"), "frame interval", 0, 1e9))
        context_aligned = frame.get("contextAligned")
        if not isinstance(context_aligned, bool):
            raise ReportError("frame alignment must be boolean")
        aligned += int(context_aligned)
        command = frame.get("throttleCommand")
        if command is None:
            throttle["unknown"] += 1
        else:
            command = finite(command, "frame throttle", 0, 1)
            throttle["nearZero" if command < 0.01 else "positive" if command > 0.05 else "other"] += 1
        state = frame.get("packed")
        if state is None:
            packed["unknown"] += 1
        elif isinstance(state, bool):
            packed["true" if state else "false"] += 1
        else:
            raise ReportError("frame packed state is invalid")
        for key, output in (("body", bodies), ("situation", situations)):
            item = token(frame.get(key), "frame " + key, allow_null=True)
            if item is not None:
                output[item] = output.get(item, 0) + 1
    misaligned = integer(data.get("contextMisalignedFrames"), "misaligned profiler frames", 0, completed)
    if structural_frames not in (0, completed):
        raise ReportError("structural frame fields must be present for every frame or none")
    if misaligned != completed - aligned:
        raise ReportError("profiler alignment count does not match frames")
    parse_source_distribution(data.get("wallIntervals"), "source frame interval", allow_null=completed == 0)

    marker_values = data.get("markers")
    if not isinstance(marker_values, list) or len(marker_values) > 32:
        raise ReportError("marker list is invalid")
    markers, names = [], set()
    for marker in marker_values:
        if not isinstance(marker, dict):
            raise ReportError("marker entry is invalid")
        name = token(marker.get("name"), "marker name")
        if not name or name in names:
            raise ReportError("marker names must be unique")
        names.add(name)
        marker_status = marker.get("status")
        if marker_status not in ("observed", "available-no-samples", "unavailable"):
            raise ReportError("marker status is invalid")
        recorder_available = marker.get("recorderAvailableAtStart")
        if not isinstance(recorder_available, bool):
            raise ReportError("marker recorder availability is invalid")
        summary = marker.get("summary")
        if not isinstance(summary, dict):
            raise ReportError("marker summary is invalid")
        available = integer(summary.get("availableFrames"), "available marker frames", 0, completed)
        unavailable = integer(summary.get("unavailableFrames"), "unavailable marker frames", 0, completed)
        observed = integer(summary.get("observedFrames"), "observed marker frames", 0, completed)
        zero = integer(summary.get("zeroBlockFrames"), "zero-block marker frames", 0, completed)
        if available + unavailable != completed or observed + zero != available:
            raise ReportError("marker frame counts do not match profiler frames")
        if marker_status == "observed" and observed == 0:
            raise ReportError("observed marker has no observed frames")
        if marker_status == "unavailable" and (available != 0 or observed != 0 or recorder_available):
            raise ReportError("unavailable marker has available frames")
        if marker_status == "available-no-samples" and (observed != 0 or (available == 0 and not recorder_available)):
            raise ReportError("available-no-samples marker has observations or no available recorder")
        if marker_status != "observed" and observed != 0:
            raise ReportError(marker_status + " marker has observations")
        blocks = integer(summary.get("totalBlocks"), "marker blocks", 0, 2 ** 63 - 1)
        observed_ms = parse_source_distribution(summary.get("observedMilliseconds"),
                                                "observed marker time", allow_null=observed == 0)
        if (observed_ms is None) != (observed == 0) or (observed_ms and observed_ms["count"] != observed):
            raise ReportError("marker duration count does not match observations")
        markers.append({
            "name": name, "status": marker_status, "availableFrames": available,
            "unavailableFrames": unavailable, "observedFrames": observed,
            "zeroBlockFrames": zero, "totalBlocks": blocks,
            "observedMilliseconds": observed_ms,
        })
    part_forces = parse_part_forces(data.get("partForces"))
    if part_forces and part_forces["cleanupStatus"] == "cleanup-error" and status != "cleanup-error":
        raise ReportError("part-force cleanup failure contradicts profiler status")
    player_loop = parse_player_loop(data.get("playerLoop"))
    if player_loop and player_loop["cleanupStatus"] == "cleanup-error" and status != "cleanup-error":
        raise ReportError("player-loop cleanup failure must propagate to profiler cleanup status")
    structural_summary = None
    if structural_frames:
        structural_summary = {}
        for name in structural_names:
            values = structural[name]["values"]
            structural_summary[name] = {
                "observedFrames": len(values),
                "unavailableFrames": structural[name]["unavailableFrames"],
                "minimum": min(values) if values else None,
                "maximum": max(values) if values else None,
            }
    return {
        "status": status, "requestedFrames": requested, "completedFrames": completed,
        "contextMisalignedFrames": misaligned, "frameIntervalsMilliseconds": distribution(intervals),
        "frameContexts": {"throttleCommand": throttle, "packed": packed,
                          "bodies": bodies, "situations": situations,
                          "structuralCounts": structural_summary},
        "markers": markers, "playerLoop": player_loop, "partForces": part_forces,
    }


def parse_part_forces(data):
    if data is None:
        return None
    if not isinstance(data, dict) or data.get("schema") != "ksp-continuum-part-force-observation/v1":
        raise ReportError("unsupported part-force contract")
    if (data.get("provider") != "continuum-part-census" or data.get("providerVersion") != "1"
            or data.get("timingStage") != "TimingManager.FashionablyLate"):
        raise ReportError("unsupported part-force provider")
    status, cleanup = data.get("status"), data.get("cleanupStatus")
    if status not in ("complete", "interrupted", "unavailable", "bounded", "invalid") or cleanup not in (
            "not-registered", "removed-owned-callbacks", "owner-destroyed", "cleanup-error"):
        raise ReportError("invalid part-force lifecycle")
    for key in ("stockAerodynamics", "gravity", "contactsAndConstraints", "directRigidbodyWrites"):
        if data.get(key) != "unavailable-not-observed":
            raise ReportError("part census cannot establish coverage of " + key)
    for key, bound in (("maxBatches", 16), ("maxPartsPerBatch", 512), ("maxHoldersPerPart", 64),
                       ("maxRetainedParts", 2048), ("maxRetainedHolders", 4096)):
        if integer(data.get(key), key, 1, bound) != bound:
            raise ReportError("unsupported force capture bounds")

    def identity(value):
        if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value):
            raise ReportError("invalid force identity")
        return value

    def vector(value):
        if not isinstance(value, dict) or set(value) != {"X", "Y", "Z"}:
            raise ReportError("invalid force vector")
        for component in value.values():
            finite(component, "force vector", -1e30, 1e30)

    identity(data.get("sessionId"))
    identity(data.get("nativeAssemblyMvid"))
    batches = data.get("batches")
    if not isinstance(batches, list) or len(batches) > 16:
        raise ReportError("invalid force batch list")
    count = integer(data.get("completedBatches"), "force batch count", 0, 16)
    epochs = integer(data.get("physicsEpochs"), "force epochs", 0, 2 ** 63 - 1)
    origins = integer(data.get("originEvents"), "force origins", 0, 2 ** 63 - 1)
    skipped = integer(data.get("skippedCallbacks"), "skipped force callbacks", 0, 2 ** 31 - 1)
    if count != len(batches) or (status == "complete" and count != 16) or (status == "unavailable" and count):
        raise ReportError("force lifecycle contradicts captured batches")
    if cleanup == "not-registered" and (count or status != "unavailable"):
        raise ReportError("force observations lack registration evidence")
    expected = "captured-component-only" if count else "not-observed"
    if data.get("partCensusStatus") != expected:
        raise ReportError("force component status contradicts observations")
    total_parts, total_holders, last_epoch = 0, 0, 0
    for batch in batches:
        if not isinstance(batch, dict) or not isinstance(batch.get("context"), dict):
            raise ReportError("invalid force batch")
        ctx = batch["context"]
        if (ctx.get("providerSession") != data["sessionId"] or ctx.get("scene") != "FLIGHT"
                or ctx.get("referenceFrame") != "unity-world-at-FashionablyLate"):
            raise ReportError("invalid force frame provenance")
        identity(ctx.get("vesselId"))
        frame_key = ctx.get("frameKey")
        if not isinstance(frame_key, str) or not 1 <= len(frame_key) <= 512:
            raise ReportError("invalid force frame key")
        integer(ctx.get("unityFrame"), "force Unity frame", 0, 2 ** 31 - 1)
        integer(ctx.get("mainBodyInstanceId"), "force body instance", -(2 ** 31), 2 ** 31 - 1)
        epoch = integer(ctx.get("physicsEpoch"), "force batch epoch", last_epoch + 1, epochs)
        last_epoch = epoch
        for key in ("topologyGeneration", "frameGeneration"):
            integer(ctx.get(key), key, 1, 2 ** 63 - 1)
        integer(ctx.get("floatingOriginEvents"), "force origin events", 0, origins)
        for key in ("universalTime", "fixedTimeSeconds"):
            finite(ctx.get(key), key, -1e15, 1e15)
        if finite(ctx.get("stepSeconds"), "force step", 0, 1e6) == 0:
            raise ReportError("force step must be positive")
        vector(ctx.get("rawKrakensbaneFrameVelocity"))
        parts = batch.get("parts")
        if not isinstance(parts, list) or not 1 <= len(parts) <= 512:
            raise ReportError("invalid force parts")
        ids, native_ids = set(), set()
        total_parts += len(parts)
        for part in parts:
            if not isinstance(part, dict):
                raise ReportError("invalid force part")
            pid = integer(part.get("flightId"), "force part ID", 1, 2 ** 32 - 1)
            native_id = integer(part.get("nativePartInstanceId"), "force native part ID", -(2 ** 31), 2 ** 31 - 1)
            if pid in ids or native_id in native_ids:
                raise ReportError("duplicate force part")
            ids.add(pid); native_ids.add(native_id)
            if integer(part.get("parentFlightId"), "force parent ID", 0, 2 ** 32 - 1) == pid:
                raise ReportError("force part is its own parent")
            if part.get("rigidBodyPartFlightId") is not None:
                integer(part["rigidBodyPartFlightId"], "force physical part ID", 1, 2 ** 32 - 1)
            body = part.get("nativeRigidbodyInstanceId")
            center = part.get("worldCenterOfMass")
            if (body is None) != (center is None):
                raise ReportError("force center requires native body")
            if body is not None:
                integer(body, "force rigidbody ID", -(2 ** 31), 2 ** 31 - 1)
                vector(center)
            vector(part.get("force")); vector(part.get("torque"))
            holders = part.get("forces")
            if not isinstance(holders, list) or len(holders) > 64:
                raise ReportError("invalid positioned-force entries")
            total_holders += len(holders)
            for holder in holders:
                if not isinstance(holder, dict):
                    raise ReportError("invalid positioned force")
                vector(holder.get("force")); vector(holder.get("worldPosition"))
                if (holder.get("worldLeverArm") is None) != (body is None):
                    raise ReportError("force lever arm requires same-callback body context")
                if body is not None:
                    vector(holder["worldLeverArm"])
        for part in parts:
            if (part["parentFlightId"] != 0 and part["parentFlightId"] not in ids
                    or part.get("rigidBodyPartFlightId") is not None and part["rigidBodyPartFlightId"] not in ids):
                raise ReportError("force part references an absent captured part")
    if (total_parts != integer(data.get("retainedParts"), "retained force parts", 0, 2048)
            or total_holders != integer(data.get("retainedHolders"), "retained force holders", 0, 4096)):
        raise ReportError("force retained counts contradict observations")
    return {"status": status, "cleanupStatus": cleanup, "partCensusStatus": expected,
            "capturedBatches": count, "capturedParts": total_parts, "capturedPositionForces": total_holders,
            "skippedCallbacks": skipped, "physicsEpochs": epochs,
            "usableComponentCapture": count > 0 and status in ("complete", "interrupted", "bounded") and cleanup == "removed-owned-callbacks"}


def part_forces_html(capture):
    heading = "<h3>Part force observations</h3>"
    if capture is None:
        return heading + "<p>No part-force observation recorded.</p>"
    content = "<p>Status: {}; cleanup: {}. Retained: {} batches, {} part records, {} positioned-force entries.</p>".format(
        html.escape(capture["status"]), html.escape(capture["cleanupStatus"]), capture["capturedBatches"],
        capture["capturedParts"], capture["capturedPositionForces"])
    content += ("<p>Raw Part census at FashionablyLate; not total force. Stock aerodynamics, gravity, contacts, "
                "constraints and direct Rigidbody writes remain unavailable. These provider-local epochs cannot "
                "be joined to the independent shadow capture by counter equality. Part records across batches "
                "are repeated observations, not unique craft parts.</p>")
    if not capture["usableComponentCapture"]:
        content += "<p>Capture lifecycle is unqualified; retained entries are diagnostic only.</p>"
    if capture["status"] == "bounded":
        content += "<p>Retention budget reached; the next whole batch was rejected.</p>"
    return heading + content


def parse_player_loop(data):
    if data is None:
        return None
    if not isinstance(data, dict) or data.get("schema") not in (
            "ksp-continuum-playerloop/v1", "ksp-continuum-playerloop/v2"):
        raise ReportError("unsupported player-loop contract")
    schema = data["schema"]
    status, integrity, cleanup = (data.get(key) for key in ("status", "integrityStatus", "cleanupStatus"))
    if (status not in ("unavailable", "observed", "no-samples", "invalid")
            or integrity not in ("verified-at-boundaries", "invalidated", "not-installed")
            or cleanup not in ("not-installed", "removed-owned-hooks", "cleanup-error")):
        raise ReportError("invalid player-loop status")
    usable = status in ("observed", "no-samples")
    if usable and (integrity != "verified-at-boundaries" or cleanup != "removed-owned-hooks"):
        raise ReportError("player-loop timing lacks intact boundary and cleanup evidence")
    frequency = integer(data.get("clockFrequency"), "player-loop clock frequency", 1, 10 ** 12)
    raw_scopes = data.get("scopes")
    expected = {
        "ksp-continuum-playerloop/v1": {
            "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate": None,
            "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate": None,
        },
        "ksp-continuum-playerloop/v2": {
            "UnityEngine.PlayerLoop.FixedUpdate": ("fixed", "contains-fixed-children"),
            "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate": ("fixed", "contained-by-fixed"),
            "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate": ("fixed", "contained-by-fixed"),
            "UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate": ("frame", "separate-frame-phase"),
            "UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate": ("frame", "separate-frame-phase"),
        },
    }[schema]
    if not isinstance(raw_scopes, list) or len(raw_scopes) != len(expected):
        raise ReportError("invalid player-loop scope list")
    scopes, names = [], set()
    for scope in raw_scopes:
        if not isinstance(scope, dict):
            raise ReportError("invalid player-loop scope")
        name = scope.get("name")
        if name not in expected or name in names:
            raise ReportError("unsupported or duplicate player-loop scope")
        names.add(name)
        time_domain = overlap = None
        if schema.endswith("/v2"):
            time_domain, overlap = scope.get("timeDomain"), scope.get("overlap")
            if (time_domain, overlap) != expected[name]:
                raise ReportError("player-loop scope time domain or overlap is invalid")
        scope_status = scope.get("status")
        if scope_status not in ("observed", "no-samples", "invalid"):
            raise ReportError("invalid player-loop scope status")
        dropped = integer(scope.get("droppedSamples"), "dropped loop samples", 0, 2 ** 31 - 1)
        errors = integer(scope.get("sequenceErrors"), "loop sequence errors", 0, 2 ** 31 - 1)
        samples = scope.get("samples")
        if not isinstance(samples, list) or len(samples) > 4096:
            raise ReportError("invalid or unbounded player-loop samples")
        if ((scope_status == "observed" and (not samples or errors))
                or (scope_status == "no-samples" and (samples or errors or dropped))
                or (usable and scope_status == "invalid")):
            raise ReportError("player-loop scope status contradicts samples or sequence errors")
        durations, frames = [], set()
        for sample in samples:
            if not isinstance(sample, dict):
                raise ReportError("invalid player-loop sample")
            frames.add(integer(sample.get("frame"), "loop sample frame", 0, 2 ** 31 - 1))
            finite(sample.get("fixedTimeSeconds"), "loop fixed time", 0, 1e15)
            delta = finite(sample.get("fixedDeltaSeconds"), "loop fixed delta", 0, 1e6)
            if delta == 0:
                raise ReportError("loop fixed delta must be positive")
            ticks = integer(sample.get("elapsedTicks"), "loop elapsed ticks", 0, 2 ** 63 - 1)
            durations.append(ticks * 1000.0 / frequency)
        scopes.append({"name": name, "timeDomain": time_domain, "overlap": overlap,
                       "status": scope_status, "sampleCount": len(samples),
                       "sampledFrames": len(frames), "droppedSamples": dropped,
                       "sequenceErrors": errors,
                       "milliseconds": distribution(durations) if usable else None})
    invalid_scopes = sum(scope["status"] == "invalid" for scope in scopes)
    if status == "invalid" and (integrity != "invalidated" or cleanup == "not-installed" or invalid_scopes != len(expected)):
        raise ReportError("invalid player-loop capture lacks invalidation evidence")
    if status == "unavailable":
        expected_integrity = "not-installed" if cleanup == "not-installed" else "invalidated"
        if integrity != expected_integrity or invalid_scopes != len(expected) or any(scope["sampleCount"] for scope in scopes):
            raise ReportError("unavailable player-loop capture contradicts lifecycle evidence")
    any_samples = any(scope["sampleCount"] for scope in scopes)
    if (status == "observed" and not any_samples) or (status == "no-samples" and any_samples):
        raise ReportError("player-loop status contradicts observations")
    if names != set(expected):
        raise ReportError("player-loop scope set is incomplete")
    return {"schema": schema, "status": status, "integrityStatus": integrity,
            "cleanupStatus": cleanup, "clockFrequency": frequency,
            "hasOverlappingScopes": schema.endswith("/v2"), "scopes": scopes}


def player_loop_html(loop):
    heading = "<h3>Fixed-step loop brackets</h3>"
    if loop is None:
        return heading + "<p>No player-loop timing recorded.</p>"
    content = "<p>Status: {}; integrity: {}; cleanup: {}.</p>".format(
        html.escape(loop["status"]), html.escape(loop["integrityStatus"]), html.escape(loop["cleanupStatus"]))
    content += ("<p>Elapsed wall time, not exclusive CPU time. These brackets include waits and callback overhead. "
                "The install-to-cleanup window includes warmup; multiple fixed steps can share one rendered frame. "
                "Do not sum these with Recorder markers or infer a physics percentage.</p>")
    if loop.get("hasOverlappingScopes"):
        content += ("<p><strong>Overlapping scopes:</strong> FixedUpdate contains its measured physics and "
                    "script children. Do not add parent and child durations. Update and LateUpdate are "
                    "separate rendered-frame phases.</p>")
    if loop["status"] not in ("observed", "no-samples"):
        content += "<p>Timing summaries withheld: capture integrity or availability is unqualified.</p>"
    content += "<ul>" + "".join(
        "<li><strong>{}</strong>: {} raw samples across {} sampled frames; {} dropped; {} sequence errors. "
        "Elapsed ms: {}.</li>".format(html.escape(scope["name"]), scope["sampleCount"], scope["sampledFrames"],
                                      scope["droppedSamples"], scope["sequenceErrors"],
                                      html.escape(dist_text(scope["milliseconds"])))
        for scope in loop["scopes"]) + "</ul>"
    return heading + content


def parse_shadow(data, phase):
    schema = data.get("schema")
    if schema not in ("ksp-continuum-flight-shadow/v1", "ksp-continuum-flight-shadow/v2"):
        raise ReportError("unsupported shadow schema for " + phase)
    status = data.get("status")
    if status not in ("complete", "timeout", "unavailable", "interrupted", "failed"):
        raise ReportError("invalid shadow status for " + phase)
    submitted = integer(data.get("submitted"), "shadow submitted count", 0, MAX_SHADOW_SAMPLES)
    accepted = integer(data.get("accepted"), "shadow accepted count", 0, submitted)
    stale = integer(data.get("stale"), "shadow stale count", 0, submitted)
    wall = finite(data.get("wallSeconds"), "shadow wall seconds", 0, 3600)
    samples = data.get("samples")
    if not isinstance(samples, list) or len(samples) != submitted:
        raise ReportError("shadow sample count does not match submitted")
    timing_fields = {
        "capture": "captureMilliseconds", "submit": "submitMilliseconds",
        "collect": "collectMilliseconds", "collectAudit": "collectAuditMilliseconds",
        "handoff": "handoffWallMilliseconds",
    }
    timings = {key: [] for key in timing_fields}
    observed_accepted = observed_stale = 0
    observed_compared = observed_comparison_skipped = 0
    body_counts = []
    for sample in samples:
        if not isinstance(sample, dict):
            raise ReportError("shadow sample is invalid")
        sample_status = sample.get("status")
        if sample_status == "accepted":
            observed_accepted += 1
        elif sample_status == "stale-discarded":
            observed_stale += 1
        elif not isinstance(sample_status, str) or not sample_status.startswith("abandoned-on-"):
            raise ReportError("shadow sample status is invalid")
        if schema.endswith("/v2"):
            comparison_status = sample.get("comparisonStatus")
            available = sample.get("observedComparisonAvailable")
            if comparison_status == "compared" and available is True:
                observed_compared += 1
            elif isinstance(comparison_status, str) and comparison_status.startswith("skipped-") and available is False:
                observed_comparison_skipped += 1
            elif comparison_status != "not-accepted" or available is not False:
                raise ReportError("shadow comparison status is invalid")
        body_counts.append(integer(sample.get("bodies"), "shadow body count", 1, MAX_BODIES))
        for label, field in timing_fields.items():
            timings[label].append(finite(sample.get(field), "shadow " + field, 0, 1e9))
    if observed_accepted != accepted or observed_stale != stale or accepted + stale > submitted:
        raise ReportError("shadow status counts do not match samples")
    compared = comparison_skipped = None
    if schema.endswith("/v2"):
        compared = integer(data.get("compared"), "shadow compared count", 0, accepted)
        comparison_skipped = integer(data.get("comparisonSkipped"), "shadow comparison skipped count", 0, accepted)
        if compared != observed_compared or comparison_skipped != observed_comparison_skipped:
            raise ReportError("shadow comparison counts do not match samples")
    first = data.get("firstAcceptedBatch")
    if not isinstance(first, list) or len(first) > MAX_BODIES:
        raise ReportError("first accepted shadow batch is invalid")
    if (accepted > 0) != (len(first) > 0):
        raise ReportError("first accepted body count does not match accepted samples")
    for body in first:
        if not isinstance(body, dict):
            raise ReportError("first accepted body is invalid")
        integer(body.get("id"), "first accepted body id", 0, MAX_BODIES - 1)
        finite(body.get("mass"), "first accepted body mass", 0, 1e15)
    first_tick = data.get("firstAcceptedTick")
    if accepted:
        integer(first_tick, "first accepted tick", 0, 2 ** 63 - 1)
    return {
        "schema": schema, "status": status, "submitted": submitted, "accepted": accepted, "stale": stale,
        "compared": compared, "comparisonSkipped": comparison_skipped,
        "abandoned": submitted - accepted - stale, "sampleCount": len(samples),
        "wallSeconds": wall, "sampleBodyCounts": distribution([float(x) for x in body_counts]),
        "firstAcceptedBodyCount": len(first), "firstAcceptedTick": first_tick if accepted else None,
        "massUnits": "native Rigidbody.mass units as recorded; no kilogram conversion asserted",
        "physicalInput": parse_physical_input(data, first, samples, first_tick),
        "timingsMilliseconds": {key: distribution(values) for key, values in timings.items()},
    }


def parse_physical_input(data, first, samples, first_tick):
    schemas = ("physicalInputSchema", "referenceFrameSchema", "aggregateForceStatus")
    if not any(key in data for key in schemas):
        return None
    if (data.get("physicalInputSchema") != "ksp-continuum-rigidbody-input/v1"
            or data.get("referenceFrameSchema") != "ksp-continuum-unity-frame-context/v1"
            or data.get("aggregateForceStatus") != "unavailable-not-captured"):
        raise ReportError("unsupported or incomplete physical input contract")

    def vector(value, length, label, minimum=-1e100):
        if not isinstance(value, list) or len(value) != length:
            raise ReportError("physical " + label + " has the wrong shape")
        return [finite(item, "physical " + label, minimum, 1e100) for item in value]

    ids = set()
    sleeping = constrained = zero_inertia = 0
    for body in first:
        identity = body["id"]
        if identity in ids or body["mass"] <= 0:
            raise ReportError("physical body IDs must be unique and mass positive")
        ids.add(identity)
        for key in ("position", "velocity", "angularVelocity", "centerOfMass", "worldCenterOfMass",
                    "predictedPosition", "predictedVelocity"):
            vector(body.get(key), 3, key)
        for key in ("rotation", "inertiaTensorRotation"):
            quaternion = vector(body.get(key), 4, key)
            if abs(sum(item * item for item in quaternion) - 1) > .001:
                raise ReportError("physical " + key + " must be a unit quaternion")
        inertia = vector(body.get("inertiaTensor"), 3, "inertia tensor", 0)
        zero_inertia += int(any(item == 0 for item in inertia))
        constrained += int(integer(body.get("constraints"), "physical constraints", 0, 2 ** 31 - 1) != 0)
        if not isinstance(body.get("sleeping"), bool):
            raise ReportError("physical sleeping state must be boolean")
        sleeping += int(body["sleeping"])
        force = vector(body.get("force"), 3, "force")
        if any(force) or body.get("forceSource") != "synthetic-zero-not-native-measurement":
            raise ReportError("physical force must retain synthetic zero provenance")
    accepted_samples = []
    for sample in samples:
        if sample.get("referenceFrame") != "unity-world-at-capture":
            raise ReportError("unsupported physical reference frame")
        vector(sample.get("rawKrakensbaneFrameVelocity"), 3, "frame velocity")
        integer(sample.get("physicsEpoch"), "physical epoch", 0, 2 ** 63 - 1)
        integer(sample.get("floatingOriginEventCount"), "physical origin count", 0, 2 ** 63 - 1)
        integer(sample.get("tick"), "physical sample tick", 0, 2 ** 63 - 1)
        if sample["status"] == "accepted":
            accepted_samples.append(sample)
    if first and (not accepted_samples or accepted_samples[0]["tick"] != first_tick
                  or accepted_samples[0]["bodies"] != len(first)):
        raise ReportError("physical snapshot does not match first accepted sample")
    return {
        "schema": data["physicalInputSchema"], "frameSchema": data["referenceFrameSchema"],
        "aggregateForceStatus": data["aggregateForceStatus"],
        "capturedBodies": len(first), "sleepingBodies": sleeping,
        "constrainedBodies": constrained, "zeroInertiaBodies": zero_inertia,
        "frameSamples": len(samples), "referenceFrame": "unity-world-at-capture",
    }


def physical_input_html(coverage):
    if coverage is None:
        content = "<p>No versioned physical input recorded. Body and frame coverage are unqualified.</p>"
    elif coverage["capturedBodies"] == 0:
        content = ("<p>Versioned physical input contract recorded, but no accepted body snapshot. "
                   "Body coverage is unqualified. Native aggregate force and torque are not captured.</p>")
    else:
        content = ("<p>Validated receipt fields for {capturedBodies} bodies and {frameSamples} frame samples. "
                   "Sleeping bodies: {sleepingBodies}; constrained bodies: {constrainedBodies}; "
                   "bodies with a zero principal inertia component: {zeroInertiaBodies}.</p>"
                   "<p>Pose, angular velocity, centers of mass and principal inertia are recorded. "
                   "Frame: Unity world at capture, with raw frame velocity and epoch counters. "
                   "This is not a complete inertial transform or replay checkpoint.</p>"
                   "<p>Native aggregate force and torque are not captured; worker force is synthetic zero. "
                   "Zero inertia components are preserved without assigning a physical meaning.</p>").format(**coverage)
    return "<h3>Physical input coverage</h3>" + content


def parse_shutdown(text, capture_status):
    fields, handlers = {}, []
    for line in text.splitlines():
        if "=" not in line:
            raise ReportError("invalid shutdown receipt line")
        key, value = line.split("=", 1)
        if key == "handler":
            parts = value.split(":")
            if (len(parts) != 2 or not SAFE_NAME.fullmatch(parts[0])
                    or parts[1] not in ("interrupted", "already-terminal", "inactive", "error")):
                raise ReportError("invalid shutdown handler")
            handlers.append({"id": parts[0], "status": parts[1]})
        elif key in fields:
            raise ReportError("duplicate shutdown field")
        else:
            fields[key] = value
    if set(fields) != {"schema", "status", "requestedBy", "captureStatus", "handlers", "errors"}:
        raise ReportError("invalid shutdown receipt fields")
    if (fields["schema"] != "ksp-continuum-shutdown/v1" or fields["requestedBy"] != "qualification"
            or fields["captureStatus"] != capture_status):
        raise ReportError("shutdown receipt does not match qualification")
    if not re.fullmatch(r"[0-9]{1,2}", fields["handlers"]) or not re.fullmatch(r"[0-9]{1,2}", fields["errors"]):
        raise ReportError("invalid shutdown counts")
    count = integer(int(fields["handlers"]), "shutdown handlers", 0, 32)
    errors = integer(int(fields["errors"]), "shutdown errors", 0, 32)
    if count != len(handlers) or len({item["id"] for item in handlers}) != count:
        raise ReportError("shutdown handler count or identities disagree")
    if errors != sum(item["status"] == "error" for item in handlers):
        raise ReportError("shutdown error count disagrees")
    if fields["status"] != ("error" if errors else "complete"):
        raise ReportError("shutdown status disagrees with errors")
    return {"status": fields["status"], "handlers": handlers, "errors": errors}


def collect(source):
    if source.is_symlink() or not source.is_dir():
        raise ReportError("source must be a non-symbolic directory")
    if not SAFE_NAME.fullmatch(source.name):
        raise ReportError("source directory name is unsafe")
    reader = SourceReader(source)
    status, completed_windows = parse_status(reader.text("status.txt"))
    reader.text("scope.txt")
    phases = []
    for index, phase in enumerate(PHASES):
        names = (phase + "-start.txt", phase + "-markers.json", phase + "-shadow.json")
        present = [(source / name).exists() or (source / name).is_symlink() for name in names]
        if any(present) and not all(present):
            raise ReportError("incomplete source triplet for " + phase)
        if not all(present):
            if index < completed_windows:
                raise ReportError("completed window is missing receipts: " + phase)
            continue
        if index >= completed_windows:
            raise ReportError("phase receipts exceed completed window count: " + phase)
        phases.append({
            "id": phase, "label": phase.capitalize(),
            "startContext": parse_start(reader.text(names[0]), phase),
            "profiler": parse_profiler(reader.json(names[1]), phase),
            "shadow": parse_shadow(reader.json(names[2]), phase),
        })
    if len(phases) != completed_windows:
        raise ReportError("completed window count does not match phase receipts")
    if status == "complete" and completed_windows != len(PHASES):
        raise ReportError("complete qualification requires all phases")
    shutdown_path = source / "shutdown.txt"
    shutdown = (parse_shutdown(reader.text("shutdown.txt"), status)
                if shutdown_path.exists() or shutdown_path.is_symlink() else None)
    return {
        "schema": "ksp-continuum-qualification-summary/v1",
        "sourceSession": source.name, "status": status, "completedWindows": completed_windows,
        "shutdown": shutdown,
        "missingPhases": list(PHASES[completed_windows:]),
        "scope": {
            "phaseLabels": "Each phase name classifies its start context only; frame contexts report the observations within that window.",
            "profiler": "Callback intervals and previous-frame marker observations; marker scopes may overlap and are not whole-frame attribution.",
            "shadow": "Read-only zero-force transport probe; stock remains authoritative.",
        },
        "claims": {"wholeFrameAttribution": False, "stockPhysicsComparison": False,
                   "physicsSpeedup": False, "deterministicReplay": False},
        "phases": phases, "sources": sorted(reader.sources, key=lambda item: item["path"]),
    }


def fmt(value):
    return "—" if value is None else format(value, ".6g")


def dist_text(value):
    if value is None:
        return "No observations"
    return "{} samples · mean {} · p95 {} · min–max {}–{}".format(
        value["count"], fmt(value["mean"]), fmt(value["p95"]),
        fmt(value["minimum"]), fmt(value["maximum"]))


def structural_text(value):
    if value is None:
        return "No structural census recorded"
    labels = (("rigidbodies", "rigidbodies"), ("joints", "joints"),
              ("colliders", "colliders"), ("loadedVessels", "loaded vessels"))
    items = []
    for key, label in labels:
        item = value[key]
        observed = ("{}-{}".format(item["minimum"], item["maximum"])
                    if item["minimum"] is not None else "unavailable")
        items.append("{}: {} ({} observed, {} unavailable)".format(
            label, observed, item["observedFrames"], item["unavailableFrames"]))
    return "; ".join(items)


def render(summary):
    shutdown = summary.get("shutdown")
    if shutdown is None:
        shutdown_html = "<p>No shutdown receipt recorded. Cleanup is unqualified.</p>"
    else:
        shutdown_html = ("<p>Shutdown status: <strong>{}</strong>. Handler errors: {}.</p>"
                         "<ul>{}</ul><p>These callback receipts do not independently prove restored game state.</p>").format(
            html.escape(shutdown["status"].title()), shutdown["errors"],
            "".join("<li>{}: {}</li>".format(html.escape(item["id"]), html.escape(item["status"]))
                    for item in shutdown["handlers"]))
    shutdown_html = "<section><h2>Shutdown and mission ownership</h2>" + shutdown_html + "</section>"
    phase_sections = []
    for phase in summary["phases"]:
        start = phase["startContext"]
        profiler = phase["profiler"]
        contexts = profiler["frameContexts"]
        markers = "".join(
            "<tr><th>{}</th><td>{}</td><td>{}/{}</td><td>{}</td><td>{}</td></tr>".format(
                html.escape(marker["name"]), html.escape(marker["status"].replace("-", " ").title()),
                marker["availableFrames"], marker["unavailableFrames"], marker["observedFrames"],
                html.escape(dist_text(marker["observedMilliseconds"])))
            for marker in profiler["markers"]
        )
        situations = ", ".join("{} × {}".format(html.escape(key), value)
                               for key, value in sorted(contexts["situations"].items())) or "None recorded"
        bodies = ", ".join("{} × {}".format(html.escape(key), value)
                           for key, value in sorted(contexts["bodies"].items())) or "None recorded"
        shadow = phase["shadow"]
        timings = "".join(
            "<li><strong>{}</strong>: {}</li>".format(html.escape(label), html.escape(dist_text(value)))
            for label, value in shadow["timingsMilliseconds"].items()
        )
        phase_sections.append("""
<section class="phase" id="phase-{id}">
  <h2>{label}</h2>
  <p class="boundary">This label describes the start context. The counts below describe all sampled frame contexts in the window.</p>
  <div class="cards">
    <article><h3>Start context</h3><dl>
      <dt>Situation</dt><dd>{situation}</dd><dt>Parts</dt><dd>{parts}</dd>
      <dt>UT</dt><dd>{ut}</dd><dt>Throttle command</dt><dd>{throttle}</dd>
    </dl></article>
    <article><h3>Observed frame contexts</h3><dl>
      <dt>Profiler status</dt><dd>{profiler_status}</dd>
      <dt>Completed / requested frames</dt><dd>{frames} / {requested}</dd>
      <dt>Intervals</dt><dd>{intervals}</dd>
      <dt>Aligned / misaligned</dt><dd>{aligned} / {misaligned}</dd>
      <dt>Throttle near-zero / positive / other / unknown</dt><dd>{tn} / {tp} / {to} / {tu}</dd>
      <dt>Packed true / false / unknown</dt><dd>{pt} / {pf} / {pu}</dd>
      <dt>Structural count ranges</dt><dd>{structural}</dd>
      <dt>Bodies</dt><dd>{bodies}</dd><dt>Situations</dt><dd>{situations}</dd>
    </dl></article>
    <article><h3>Shadow transport</h3><dl>
      <dt>Status</dt><dd>{shadow_status}</dd><dt>Submitted / accepted / stale / abandoned</dt>
      <dd>{submitted} / {accepted} / {stale} / {abandoned}</dd>
      <dt>Wall span</dt><dd>{wall} s</dd><dt>First accepted body count</dt><dd>{first_bodies}</dd>
      <dt>Mass units</dt><dd>{mass_units}</dd>
    </dl><ul>{timings}</ul></article>
  </div>
  {physical_input}
  {player_loop}
  {part_forces}
  <h3>Profiler markers</h3>
  <div class="table"><table><thead><tr><th>Marker</th><th>Status</th><th>Available / unavailable frames</th><th>Observed frames</th><th>Observed marker time (ms)</th></tr></thead><tbody>{markers}</tbody></table></div>
</section>""".format(
            id=phase["id"], label=phase["label"], situation=html.escape(start["situation"]),
            parts=start["parts"], ut=fmt(start["ut"]), throttle=fmt(start["throttleCommand"]),
            profiler_status=html.escape(profiler["status"].replace("-", " ").title()),
            frames=profiler["completedFrames"], requested=profiler["requestedFrames"],
            intervals=html.escape(dist_text(profiler["frameIntervalsMilliseconds"])),
            aligned=profiler["completedFrames"] - profiler["contextMisalignedFrames"],
            misaligned=profiler["contextMisalignedFrames"], tn=contexts["throttleCommand"]["nearZero"],
            tp=contexts["throttleCommand"]["positive"], to=contexts["throttleCommand"]["other"],
            tu=contexts["throttleCommand"]["unknown"], pt=contexts["packed"]["true"],
            pf=contexts["packed"]["false"], pu=contexts["packed"]["unknown"], bodies=bodies,
            structural=html.escape(structural_text(contexts.get("structuralCounts"))),
            situations=situations, shadow_status=html.escape(shadow["status"].title()),
            submitted=shadow["submitted"], accepted=shadow["accepted"], stale=shadow["stale"],
            abandoned=shadow["abandoned"], wall=fmt(shadow["wallSeconds"]),
            first_bodies=shadow["firstAcceptedBodyCount"], mass_units=html.escape(shadow["massUnits"]),
            player_loop=player_loop_html(profiler.get("playerLoop")),
            part_forces=part_forces_html(profiler.get("partForces")),
            timings=timings, markers=markers, physical_input=physical_input_html(shadow.get("physicalInput")),
        ))
    missing = "None" if not summary["missingPhases"] else ", ".join(summary["missingPhases"])
    source_rows = "".join(
        "<tr><td>{}</td><td>{}</td><td><code>{}</code></td></tr>".format(
            html.escape(item["path"]), item["bytes"], item["sha256"])
        for item in summary["sources"]
    )
    template = """<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Continuum qualification report</title><style>
:root{color-scheme:dark;background:#071116;color:#eef4f5;font:16px/1.5 system-ui,sans-serif}body{margin:0}main{max-width:1180px;margin:auto;padding:28px}h1{font-size:clamp(2rem,5vw,4rem);margin:.2em 0}.lede,.boundary{color:#b8c9cf}.status{display:inline-block;padding:5px 10px;border:1px solid #6d99a8;border-radius:99px}.phase{margin:32px 0;padding-top:16px;border-top:1px solid #35505a}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(300px,100%),1fr));gap:14px}.cards article{background:#10232b;border:1px solid #35505a;border-radius:12px;padding:16px}dl{display:grid;grid-template-columns:minmax(120px,.7fr) 1.3fr;gap:6px 12px}dt{color:#9fc4d2}dd{margin:0;overflow-wrap:anywhere}.table{overflow-x:auto}table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:9px;border-bottom:1px solid #35505a}code{overflow-wrap:anywhere;color:#c8e6f0}.limits{padding:16px;border-left:4px solid #d6a94d;background:#211d12}</style></head><body><main>
<p>KSP Continuum · local evidence summary</p><h1>Flight qualification</h1>
<p class="status"><!--STATUS--></p><p class="lede">Session <!--SESSION-->. Completed windows: <!--COMPLETED-->/3. Missing phases: <!--MISSING-->.</p>
<div class="limits"><strong>Evidence boundary.</strong> Phase names classify only each window’s start context. Profiler markers may overlap and are not whole-frame attribution. Zero-force transport checks data movement and its own analytic oracle; it is not a stock-physics comparison, deterministic replay result, or speedup claim.</div>
<!--PHASES-->
<!--SHUTDOWN-->
<section><h2>Portable source manifest</h2><p>Hashes bind the fixed input files without embedding their local directory.</p><div class="table"><table><thead><tr><th>Relative file</th><th>Bytes</th><th>SHA-256</th></tr></thead><tbody>{sources}</tbody></table></div></section>
</main></body></html>"""
    return (template.replace("<!--STATUS-->", html.escape(summary["status"].title()))
            .replace("<!--SESSION-->", html.escape(summary["sourceSession"]))
            .replace("<!--COMPLETED-->", str(summary["completedWindows"]))
            .replace("<!--MISSING-->", html.escape(missing))
            .replace("<!--PHASES-->", "".join(phase_sections))
            .replace("<!--SHUTDOWN-->", shutdown_html)
            .replace("{sources}", source_rows))


def write_output(output, summary, page):
    try:
        output.mkdir(parents=False)
    except FileExistsError as error:
        raise ReportError("output already exists") from error
    try:
        with (output / "summary.json").open("x", encoding="utf-8") as stream:
            json.dump(summary, stream, indent=2, sort_keys=True, allow_nan=False)
            stream.write("\n")
        with (output / "index.html").open("x", encoding="utf-8") as stream:
            stream.write(page)
    except Exception:
        shutil.rmtree(output)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        summary = collect(args.source)
        page = render(summary)
        write_output(args.output, summary, page)
        print("Wrote {} qualification windows to {}".format(len(summary["phases"]), args.output))
    except (ReportError, OSError, UnicodeError) as error:
        print("qualification_report: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
