#!/usr/bin/env python3
"""Build a bounded, relocatable mission chronicle from explicit local artifacts."""

import argparse
import csv
import hashlib
import html
import json
import math
import os
import re
import shutil
import struct
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from string import Template


TEMPLATE_VERSION = "mission-v1"
METADATA_SCHEMA = "ksp-continuum-chronicle-metadata/v1"
MANIFEST_SCHEMA = "ksp-continuum-chronicle-manifest/v1"
ROOT = Path(__file__).resolve().parents[1]
TEMPLATE_PATH = ROOT / "templates" / "chronicle" / (TEMPLATE_VERSION + ".html")
MAX_TEXT_BYTES = 16 * 1024 * 1024
MAX_METADATA_BYTES = 32 * 1024
MAX_ROWS = 100000
MAX_INPUT_FILES = 512
MAX_INPUT_BYTES = 256 * 1024 * 1024
MAX_SEGMENT_SAMPLES = 2000
MAX_SCREENSHOTS = 64
MAX_MEDIA_BYTES = 16 * 1024 * 1024
SAFE_ID = re.compile(r"^[A-Z0-9]+(?:-[A-Z0-9]+)*$")
SAFE_PNG = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,126}\.png$")
SAFE_MISSION_SESSION = re.compile(r"^mission-[A-Za-z0-9][A-Za-z0-9._-]{0,126}$")
SAFE_INPUT_SESSION = re.compile(r"^inputs-[A-Za-z0-9][A-Za-z0-9._-]{0,126}$")
SAFE_CRAFT = re.compile(r"^Ships/(?:VAB|SPH)/[A-Za-z0-9][A-Za-z0-9 ._()+&'-]{0,190}\.craft$")
SEGMENT_CSV = re.compile(r"^segment-[0-9]{5}\.csv$")
EXPECTED_TELEMETRY_HEADER = [
    "wall_s", "ut_s", "phase", "body", "situation", "altitude_m", "apoapsis_m",
    "periapsis_m", "surface_speed_mps", "throttle", "stage", "parts", "packed", "autopilot",
]
TEMPLATE_FIELDS = {
    "template_version", "page_title", "attempt_alias", "mission_name", "objective",
    "configuration_context", "configuration_rows", "timeline_rows", "media_cards",
    "measurement_cards", "outcome", "anomalies", "next_experiment",
    "provenance_summary", "generated_utc",
}


class ChronicleError(Exception):
    pass


def bounded_bytes(path, maximum=MAX_TEXT_BYTES):
    if path.is_symlink():
        raise ChronicleError("source file may not be a symbolic link: " + path.name)
    if not path.is_file():
        raise ChronicleError("required file is missing: " + path.name)
    size = path.stat().st_size
    if size > maximum:
        raise ChronicleError("file exceeds size limit: " + path.name)
    return path.read_bytes()


def bounded_text(path, maximum=MAX_TEXT_BYTES):
    try:
        return bounded_bytes(path, maximum).decode("utf-8")
    except UnicodeDecodeError as error:
        raise ChronicleError("file is not valid UTF-8: " + path.name) from error


def finite_number(value, label):
    try:
        number = float(value)
    except (TypeError, ValueError) as error:
        raise ChronicleError("invalid number in " + label) from error
    if not math.isfinite(number):
        raise ChronicleError("nonfinite number in " + label)
    return number


def safe_text(value, label, maximum):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ChronicleError("invalid " + label)
    return value.strip()


def parse_metadata(path):
    try:
        data = json.loads(bounded_text(path, MAX_METADATA_BYTES))
    except json.JSONDecodeError as error:
        raise ChronicleError("metadata is not valid JSON") from error
    if not isinstance(data, dict):
        raise ChronicleError("metadata must be a JSON object")
    allowed = {
        "schema", "mission_id", "name", "attempt_id", "vehicle_design_id", "site_id",
        "parent_checkpoint", "objective", "configuration", "anomalies", "next_experiment",
    }
    unknown = set(data) - allowed
    if unknown:
        raise ChronicleError("unknown metadata field: " + sorted(unknown)[0])
    if data.get("schema") != METADATA_SCHEMA:
        raise ChronicleError("unsupported metadata schema")
    for key in ("mission_id", "attempt_id", "vehicle_design_id"):
        value = safe_text(data.get(key), key, 64)
        if not SAFE_ID.fullmatch(value):
            raise ChronicleError("invalid " + key)
        data[key] = value
    if not re.fullmatch(re.escape(data["mission_id"]) + r"-A[0-9]{3,6}", data["attempt_id"]):
        raise ChronicleError("attempt_id must belong to mission_id")
    for key in ("site_id", "parent_checkpoint"):
        value = data.get(key)
        if value is not None:
            value = safe_text(value, key, 64)
            if not SAFE_ID.fullmatch(value):
                raise ChronicleError("invalid " + key)
            data[key] = value
    data["name"] = safe_text(data.get("name"), "name", 160)
    for key in ("objective", "configuration", "next_experiment"):
        data[key] = safe_text(data.get(key), key, 2000)
    anomalies = data.get("anomalies")
    if not isinstance(anomalies, list) or len(anomalies) > 20:
        raise ChronicleError("anomalies must be a bounded JSON array")
    data["anomalies"] = [safe_text(value, "anomaly", 500) for value in anomalies]
    data["attempt_alias"] = data["attempt_id"]
    return data


def parse_key_values(path):
    values = {}
    for line in bounded_text(path, 128 * 1024).splitlines():
        if not line:
            continue
        if "=" not in line:
            raise ChronicleError(path.name + " contains a malformed line")
        key, value = line.split("=", 1)
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9]*", key):
            raise ChronicleError(path.name + " contains an invalid key")
        if len(value) > 4096:
            raise ChronicleError(path.name + " contains an oversized value")
        values[key] = value
    return values


def validate_receipt_identity(metadata, mission_values):
    fields = (
        ("mission_id", "missionId"),
        ("attempt_id", "attemptId"),
        ("vehicle_design_id", "vehicleDesignId"),
        ("site_id", "siteId"),
    )
    for metadata_key, receipt_key in fields:
        if receipt_key in mission_values and mission_values[receipt_key] != metadata.get(metadata_key):
            raise ChronicleError(metadata_key + " does not match mission receipt " + receipt_key)


def portable_basename(value):
    normalized = value.replace("\\", "/").rstrip("/")
    return normalized.rsplit("/", 1)[-1] if normalized else ""


def validate_input_association(inputs, mission_values):
    if inputs is None:
        return
    declared = mission_values.get("inputDirectory")
    if declared is None:
        raise ChronicleError("mission receipt does not declare inputDirectory")
    if portable_basename(declared) != inputs.name:
        raise ChronicleError("input directory does not match mission receipt inputDirectory")


def validate_source_session_names(mission, inputs):
    if not SAFE_MISSION_SESSION.fullmatch(mission.name):
        raise ChronicleError("invalid mission session directory name")
    if inputs is not None and not SAFE_INPUT_SESSION.fullmatch(inputs.name):
        raise ChronicleError("invalid input session directory name")


def receipt_display(value, fallback):
    if value is None or value == "":
        return fallback
    unix_path = re.search(r"(?:^|[^A-Za-z0-9])/(?!/)[^\s'\"]+", value)
    windows_path = re.search(r"(?:^|[^A-Za-z0-9])[A-Za-z]:[\\/][^\s'\"]+", value)
    unc_path = re.search(r"(?:^|[^A-Za-z0-9])(?:\\\\|//)[^\s'\"]+", value)
    if unix_path or windows_path or unc_path:
        return "Receipt contained a redacted local path; consult the hashed mission receipt locally."
    return value


def receipt_status(mission_values):
    value = mission_values.get("status")
    return value if value in ("running", "passed", "failed") else "unknown"


def craft_display(mission_values):
    value = mission_values.get("craft")
    if value is None:
        return "Not recorded"
    if not SAFE_CRAFT.fullmatch(value):
        return "Unrecognized craft receipt value; consult the hashed mission receipt locally."
    return value


def parse_telemetry(path):
    rows = []
    reader = csv.DictReader(bounded_text(path).splitlines())
    if reader.fieldnames != EXPECTED_TELEMETRY_HEADER:
        raise ChronicleError("mission.csv header does not match the supported schema")
    for index, row in enumerate(reader, 1):
        if index > MAX_ROWS:
            raise ChronicleError("mission.csv exceeds row limit")
        if None in row or any(value is None for value in row.values()):
            raise ChronicleError("mission.csv contains a malformed row")
        phase = safe_text(row["phase"], "telemetry phase", 64)
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", phase):
            raise ChronicleError("invalid telemetry phase")
        rows.append({
            "wall": finite_number(row["wall_s"], "mission.csv wall_s"),
            "ut": finite_number(row["ut_s"], "mission.csv ut_s"),
            "phase": phase,
            "body": row["body"], "situation": row["situation"], "altitude": row["altitude_m"],
            "speed": row["surface_speed_mps"], "throttle": row["throttle"],
            "stage": row["stage"], "parts": row["parts"], "packed": row["packed"],
            "autopilot": row["autopilot"],
        })
    if not rows:
        raise ChronicleError("mission.csv contains no telemetry rows")
    return rows


def phase_summaries(rows):
    boundaries = []
    previous_phase = None
    previous_wall = previous_ut = None
    for row in rows:
        if previous_wall is not None and (row["wall"] < previous_wall or row["ut"] < previous_ut):
            raise ChronicleError("mission.csv time moved backward")
        previous_wall, previous_ut = row["wall"], row["ut"]
        if row["phase"] != previous_phase:
            boundaries.append(row)
            previous_phase = row["phase"]
    if boundaries[-1] is not rows[-1]:
        boundaries.append(rows[-1])
    summaries = []
    for current, following in zip(boundaries, boundaries[1:]):
        summaries.append({
            "phase": current["phase"], "start_ut": current["ut"],
            "wall_duration": following["wall"] - current["wall"],
            "ut_duration": following["ut"] - current["ut"],
        })
    return summaries


def parse_events(path):
    events = []
    for index, line in enumerate(bounded_text(path, 1024 * 1024).splitlines(), 1):
        if index > 256:
            raise ChronicleError("events.txt exceeds event limit")
        fields = line.split(" ", 1)
        if len(fields) != 2:
            raise ChronicleError("events.txt contains a malformed line")
        phase = safe_text(fields[1], "event phase", 64)
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", phase):
            raise ChronicleError("events.txt contains an invalid phase")
        events.append((finite_number(fields[0], "events.txt"), phase))
    return events


def safe_screenshot_name(name):
    if Path(name).name != name or not SAFE_PNG.fullmatch(name):
        raise ChronicleError("unsafe screenshot filename: " + name)
    return name


def png_dimensions(path):
    if path.is_symlink():
        raise ChronicleError("screenshot may not be a symbolic link: " + path.name)
    data = bounded_bytes(path, MAX_MEDIA_BYTES)
    if (len(data) < 45 or not data.startswith(b"\x89PNG\r\n\x1a\n")
            or data[12:16] != b"IHDR"
            or not data.endswith(b"\x00\x00\x00\x00IEND\xaeB\x60\x82")):
        return None
    width, height = struct.unpack(">II", data[16:24])
    if width < 1 or height < 1 or width > 32768 or height > 32768:
        return None
    return width, height


def parse_screenshots(mission):
    path = mission / "screenshots.csv"
    if not path.exists():
        return []
    captures = {}
    order = []
    for index, row in enumerate(csv.reader(bounded_text(path, 1024 * 1024).splitlines()), 1):
        if index > MAX_SCREENSHOTS * 3:
            raise ChronicleError("screenshots.csv exceeds row limit")
        if len(row) not in (3, 5):
            raise ChronicleError("screenshots.csv contains a malformed row")
        timestamp, status, filename = row[:3]
        finite_number(timestamp, "screenshots.csv")
        safe_screenshot_name(filename)
        if status not in ("requested", "png-written", "png-below-required-resolution", "unconfirmed"):
            raise ChronicleError("screenshots.csv contains an unknown status")
        reported_dimensions = None
        if len(row) == 5:
            try:
                reported_dimensions = int(row[3]), int(row[4])
            except ValueError as error:
                raise ChronicleError("screenshots.csv contains invalid dimensions") from error
            width, height = reported_dimensions
            if width < 0 or height < 0 or width > 32768 or height > 32768 or ((width == 0) != (height == 0)):
                raise ChronicleError("screenshots.csv dimensions are out of range")
        elif status in ("png-below-required-resolution",):
            raise ChronicleError("resolution status requires screenshot dimensions")
        if filename not in captures:
            if len(captures) == MAX_SCREENSHOTS:
                raise ChronicleError("screenshots.csv exceeds capture limit")
            captures[filename] = {"filename": filename, "requested_ut": timestamp, "statuses": [], "reported_dimensions": None}
            order.append(filename)
        if reported_dimensions is not None and reported_dimensions != (0, 0):
            previous = captures[filename]["reported_dimensions"]
            if previous is not None and previous != reported_dimensions:
                raise ChronicleError("screenshots.csv contains conflicting dimensions")
            captures[filename]["reported_dimensions"] = reported_dimensions
        captures[filename]["statuses"].append(status)
    result = []
    mission_resolved = mission.resolve()
    for filename in order:
        item = captures[filename]
        source = mission / filename
        completed = [status for status in item["statuses"] if status in ("png-written", "png-below-required-resolution")]
        if len(set(completed)) > 1:
            raise ChronicleError("screenshots.csv contains conflicting completion statuses: " + filename)
        if completed:
            if source.exists():
                if source.resolve().parent != mission_resolved:
                    raise ChronicleError("screenshot escapes mission directory: " + filename)
                dimensions = png_dimensions(source)
                if dimensions is None:
                    raise ChronicleError("confirmed screenshot is not a complete PNG: " + filename)
                if item["reported_dimensions"] is not None and item["reported_dimensions"] != dimensions:
                    raise ChronicleError("reported dimensions do not match PNG: " + filename)
                item["state"] = "below-required-resolution" if completed[0] == "png-below-required-resolution" else "confirmed"
                item["source"] = source
                item["width"], item["height"] = dimensions
            else:
                item["state"] = "missing"
        elif "unconfirmed" in item["statuses"]:
            item["state"] = "unconfirmed"
        else:
            item["state"] = "requested"
        result.append(item)
    return result


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            block = stream.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def source_entry(path, logical):
    if path.is_symlink():
        raise ChronicleError("source file may not be a symbolic link: " + path.name)
    return {"path": logical, "bytes": path.stat().st_size, "sha256": sha256(path)}


def collect_input_summary(inputs, sources):
    if inputs is None:
        return {"included": False, "segments": 0, "samples": 0, "keys": 0, "bytes": 0, "events": 0, "observations": 0}
    files = sorted(path for path in inputs.iterdir() if path.is_file())
    if len(files) > MAX_INPUT_FILES:
        raise ChronicleError("input directory exceeds file limit")
    if any(path.is_symlink() for path in files):
        raise ChronicleError("input directory contains a symbolic link")
    if sum(path.stat().st_size for path in files) > MAX_INPUT_BYTES:
        raise ChronicleError("input directory exceeds total byte limit")
    summary = {"included": True, "segments": 0, "samples": 0, "keys": 0, "bytes": 0, "events": 0, "observations": 0}
    segment_names = []
    for path in files:
        if path.stat().st_size > MAX_TEXT_BYTES:
            raise ChronicleError("input file exceeds size limit: " + path.name)
        if SEGMENT_CSV.fullmatch(path.name):
            segment_names.append(path.stem)
            summary["segments"] += 1
            summary["bytes"] += path.stat().st_size
            with path.open("rb") as stream:
                first = stream.readline()
                if first.rstrip(b"\r\n") != b"schema,ksp-continuum-input-timeline/v1":
                    raise ChronicleError("input segment has an unsupported schema: " + path.name)
                for line in stream:
                    if line.startswith(b"key,"):
                        summary["keys"] += 1
        elif path.name == "events.csv":
            summary["events"] = max(0, sum(1 for _ in path.open("rb")) - 1)
        elif path.name == "observations.csv":
            summary["observations"] = max(0, sum(1 for _ in path.open("rb")) - 1)
        sources.append(source_entry(path, "inputs/" + path.name))
    for stem in segment_names:
        sidecar = inputs / (stem + ".txt")
        if not sidecar.is_file():
            raise ChronicleError("input segment sidecar is missing: " + sidecar.name)
        values = parse_key_values(sidecar)
        try:
            samples = int(values["samples"])
        except (KeyError, ValueError) as error:
            raise ChronicleError("input segment sidecar has invalid samples: " + sidecar.name) from error
        if samples < 1 or samples > MAX_SEGMENT_SAMPLES:
            raise ChronicleError("input segment sidecar samples are out of range: " + sidecar.name)
        summary["samples"] += samples
    return summary


def esc(value):
    return html.escape(str(value), quote=True)


def duration(value):
    return "{:,.2f} s".format(value)


def card(label, value):
    return '<div class="card"><div class="label">{}</div><div class="value">{}</div></div>'.format(esc(label), esc(value))


def render_page(metadata, mission_values, rows, events, captures, input_summary, source_sessions, generated_utc):
    config = [
        ("Mission ID", metadata["mission_id"]), ("Attempt", metadata["attempt_id"]),
        ("Vehicle design", metadata["vehicle_design_id"]),
        ("Craft", craft_display(mission_values)),
        ("MechJeb assembly", receipt_display(mission_values.get("mechjebAssemblyVersion"), "Not recorded")),
        ("MechJeb file", receipt_display(mission_values.get("mechjebFileVersion"), "Not recorded")),
    ]
    if metadata.get("site_id"):
        config.append(("Site", metadata["site_id"]))
    if metadata.get("parent_checkpoint"):
        config.append(("Parent checkpoint", metadata["parent_checkpoint"]))
    config_html = "".join(card(label, value) for label, value in config)

    event_start = {phase: ut for ut, phase in events}
    timeline_html = "".join(
        "<tr><td>{}</td><td>{}</td><td>{}</td><td>{}</td></tr>".format(
            esc(item["phase"]), esc("{:,.3f}".format(event_start.get(item["phase"], item["start_ut"]))),
            esc(duration(item["wall_duration"])), esc(duration(item["ut_duration"])),
        ) for item in phase_summaries(rows)
    )

    media_html = []
    for item in captures:
        name = item["filename"]
        label = name[:-4].rsplit("-", 1)[0].replace("-", " ").title()
        caption = "{} screenshot at UT {}".format(label, item["requested_ut"])
        if item["state"] in ("confirmed", "below-required-resolution"):
            status_class = "ok" if item["state"] == "confirmed" else "warn"
            status_text = "Confirmed PNG" if item["state"] == "confirmed" else "Below required 1920 × 1080 capture resolution"
            body = '<a href="media/{}" aria-label="Open full-size {} screenshot"><img src="media/{}" alt="{}" loading="lazy"></a><figcaption><span class="{}">{} · {} × {}</span><br>{}</figcaption>'.format(
                esc(name), esc(label), esc(name), esc(caption), status_class, status_text,
                esc(item["width"]), esc(item["height"]), esc(name))
        elif item["state"] == "missing":
            body = '<figcaption><span class="warn">Confirmed by receipt; source PNG missing</span><br>{}</figcaption>'.format(esc(name))
        elif item["state"] == "unconfirmed":
            body = '<figcaption><span class="warn">Capture remained unconfirmed</span><br>{}</figcaption>'.format(esc(name))
        else:
            body = '<figcaption><span class="warn">Capture was requested; no terminal status recorded</span><br>{}</figcaption>'.format(esc(name))
        media_html.append('<figure class="card media">{}</figure>'.format(body))
    if not media_html:
        media_html.append('<div class="card"><span class="warn">No screenshot receipt was present.</span></div>')

    final = rows[-1]
    measurement_values = [
        ("Telemetry rows", len(rows)), ("Observed wall span", duration(final["wall"] - rows[0]["wall"])),
        ("Observed UT span", duration(final["ut"] - rows[0]["ut"])),
    ]
    if input_summary["included"]:
        measurement_values.extend([
            ("Input segments", input_summary["segments"]), ("Control samples", input_summary["samples"]),
            ("Input key rows", input_summary["keys"]), ("Input bytes", "{:,}".format(input_summary["bytes"])),
            ("Observation rows", input_summary["observations"]), ("Diagnostic events", input_summary["events"]),
        ])
    else:
        measurement_values.append(("Input evidence", "Input evidence was not supplied for this report"))
    measurement_values.append(("Copied screenshots", sum(
        item["state"] in ("confirmed", "below-required-resolution") for item in captures)))
    measurements_html = "".join(card(label, value) for label, value in measurement_values)
    outcome_items = [
        ("Receipt status", receipt_status(mission_values)),
        ("Receipt reason", receipt_display(mission_values.get("reason"), "Not recorded")),
        ("Terminal phase", final["phase"]), ("Body / situation", (final["body"] or "unknown") + " / " + (final["situation"] or "unknown")),
        ("Surface speed", (final["speed"] or "unknown") + " m/s"), ("Throttle", final["throttle"] or "unknown"),
        ("Stage / parts", (final["stage"] or "unknown") + " / " + (final["parts"] or "unknown")),
    ]
    outcome_html = '<div class="grid">' + "".join(card(label, value) for label, value in outcome_items) + "</div>"
    anomalies_html = "<p>No editorial anomalies were supplied.</p>" if not metadata["anomalies"] else "<ul>" + "".join("<li>{}</li>".format(esc(value)) for value in metadata["anomalies"]) + "</ul>"
    input_provenance = ("receipt-associated input session " + source_sessions["inputs"]
                        if input_summary["included"] else "input evidence not supplied")
    provenance = "Source sessions: mission {}, {}. Source hashes use logical paths only; private absolute paths and save contents are excluded.".format(
        source_sessions["mission"], input_provenance)

    values = {
        "template_version": TEMPLATE_VERSION, "page_title": esc(metadata["attempt_alias"] + " — " + metadata["name"]),
        "attempt_alias": esc(metadata["attempt_alias"]), "mission_name": esc(metadata["name"]),
        "objective": esc(metadata["objective"]), "configuration_context": esc(metadata["configuration"]),
        "configuration_rows": config_html, "timeline_rows": timeline_html,
        "media_cards": "".join(media_html), "measurement_cards": measurements_html,
        "outcome": outcome_html, "anomalies": anomalies_html,
        "next_experiment": esc(metadata["next_experiment"]), "provenance_summary": esc(provenance),
        "generated_utc": esc(generated_utc),
    }
    template_text = bounded_text(TEMPLATE_PATH, 256 * 1024)
    found = set(match.group(1) or match.group(2) for match in re.finditer(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)", template_text))
    if found != TEMPLATE_FIELDS or set(values) != TEMPLATE_FIELDS:
        raise ChronicleError("mission template placeholders do not match the supported contract")
    return Template(template_text).substitute(values)


def generate(mission, inputs, output, metadata_path):
    mission = mission.resolve()
    inputs = inputs.resolve() if inputs is not None else None
    output = output.resolve()
    if not mission.is_dir():
        raise ChronicleError("mission directory does not exist")
    if inputs is not None and not inputs.is_dir():
        raise ChronicleError("input directory does not exist")
    if output.exists():
        raise ChronicleError("output already exists: " + str(output))
    if mission == output or mission in output.parents or (inputs is not None and (inputs == output or inputs in output.parents)):
        raise ChronicleError("output may not be inside a source directory")
    if not output.parent.is_dir():
        raise ChronicleError("output parent directory does not exist")
    validate_source_session_names(mission, inputs)

    metadata = parse_metadata(metadata_path)
    mission_values = parse_key_values(mission / "mission.txt")
    validate_receipt_identity(metadata, mission_values)
    validate_input_association(inputs, mission_values)
    rows = parse_telemetry(mission / "mission.csv")
    events = parse_events(mission / "events.txt")
    captures = parse_screenshots(mission)
    sources = []
    for name in ("mission.txt", "mission.csv", "events.txt", "screenshots.csv", "milestones.csv", "survey.csv", "terrain.csv"):
        path = mission / name
        if path.is_file():
            sources.append(source_entry(path, "mission/" + name))
    for item in captures:
        if item["state"] in ("confirmed", "below-required-resolution"):
            sources.append(source_entry(item["source"], "mission/media/" + item["filename"]))
    source_sessions = {"mission": mission.name}
    if inputs is not None:
        source_sessions["inputs"] = inputs.name
    input_summary = collect_input_summary(inputs, sources)
    generated_utc = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    page = render_page(metadata, mission_values, rows, events, captures, input_summary, source_sessions, generated_utc)
    manifest = {
        "schema": MANIFEST_SCHEMA, "templateVersion": TEMPLATE_VERSION,
        "generatedUtc": generated_utc, "missionId": metadata["mission_id"],
        "attemptId": metadata["attempt_id"], "attemptAlias": metadata["attempt_alias"],
        "title": metadata["name"], "outcome": receipt_status(mission_values),
        "report": "index.html",
        "templateSha256": sha256(TEMPLATE_PATH),
        "generatorSha256": sha256(Path(__file__).resolve()),
        "vehicleDesignId": metadata["vehicle_design_id"], "siteId": metadata.get("site_id"),
        "parentCheckpoint": metadata.get("parent_checkpoint"), "sourceSessions": source_sessions,
        "inputEvidence": ({"status": "included", "session": inputs.name}
                          if inputs is not None else {"status": "not-supplied"}),
        "sources": sorted(sources, key=lambda item: item["path"]),
        "media": [dict(
            {"filename": item["filename"], "status": item["state"]},
            **({"width": item["width"], "height": item["height"]}
               if item["state"] in ("confirmed", "below-required-resolution") else {})
        ) for item in captures],
    }

    staging = Path(tempfile.mkdtemp(prefix=".chronicle-", dir=str(output.parent)))
    try:
        (staging / "index.html").write_text(page, encoding="utf-8")
        confirmed = [item for item in captures if item["state"] in ("confirmed", "below-required-resolution")]
        if confirmed:
            media = staging / "media"
            media.mkdir()
            for item in confirmed:
                shutil.copyfile(item["source"], media / item["filename"])
        (staging / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        os.rename(staging, output)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True)
        raise
    return metadata["attempt_alias"]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mission", required=True, type=Path, help="Explicit mission-* artifact directory")
    parser.add_argument("--inputs", type=Path, help="Explicit inputs-* artifact directory")
    parser.add_argument("--output", required=True, type=Path, help="New output directory; must not exist")
    parser.add_argument("--metadata", required=True, type=Path, help="Bounded editorial metadata JSON")
    args = parser.parse_args(argv)
    try:
        alias = generate(args.mission, args.inputs, args.output, args.metadata)
        print("Generated {} at {}".format(alias, args.output))
        return 0
    except (ChronicleError, OSError) as error:
        print("chronicle: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
