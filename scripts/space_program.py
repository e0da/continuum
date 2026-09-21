#!/usr/bin/env python3
"""Build a connected local space-program site from catalog data and immutable reports."""

import argparse
from datetime import datetime, timezone
import hashlib
import html
from html.parser import HTMLParser
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
from string import Template
import sys
import tempfile
from urllib.parse import quote

from chronicle import (bounded_bytes, bounded_text, ChronicleError, MANIFEST_SCHEMA, MAX_PLAYBACK_BYTES,
                       parse_telemetry_playback, safe_text, sha256)


CATALOG_SCHEMA = "ksp-continuum-space-program/v1"
SITE_SCHEMA = "ksp-continuum-space-program-site/v1"
ROOT = Path(__file__).resolve().parents[1]
TEMPLATE = ROOT / "templates" / "site" / "page-v1.html"
SAFE_ID = re.compile(r"^[A-Z0-9]+(?:-[A-Z0-9]+)*$")
SAFE_ATTEMPT_ID = re.compile(r"^[A-Z0-9]+(?:-[A-Z0-9]+)*-A[0-9]{3,6}$")
SAFE_CHECKPOINT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,126}$")
SAFE_SHA256 = re.compile(r"^[0-9A-Fa-f]{64}$")
SAFE_FOLDER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,126}$")
SAFE_MEDIA_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,126}\.png$")
MAX_CATALOG_BYTES = 256 * 1024
MAX_REPORT_BYTES = 2 * 1024 * 1024
MAX_MEDIA_BYTES = 16 * 1024 * 1024
MAX_TOTAL_MEDIA_BYTES = 256 * 1024 * 1024
MAX_ENTITIES = 256
MAX_MEDIA = 128
MAX_TOTAL_MEDIA_FILES = 1024


def esc(value):
    return html.escape(str(value), quote=True)


def load_json(path, maximum, label):
    try:
        value = json.loads(bounded_text(path, maximum))
    except json.JSONDecodeError as error:
        raise ChronicleError(label + " is not valid JSON") from error
    if not isinstance(value, dict):
        raise ChronicleError(label + " must be a JSON object")
    return value


def require_keys(value, allowed, required, label):
    unknown = set(value) - set(allowed)
    missing = set(required) - set(value)
    if unknown:
        raise ChronicleError(label + " has unknown field " + sorted(unknown)[0])
    if missing:
        raise ChronicleError(label + " is missing field " + sorted(missing)[0])


def text_field(value, key, label, maximum=1000):
    return safe_text(value.get(key), label + " " + key, maximum)


def id_field(value, key, label):
    identifier = text_field(value, key, label, 64)
    if not SAFE_ID.fullmatch(identifier):
        raise ChronicleError(label + " has invalid " + key)
    return identifier


def safe_media_path(value):
    if not isinstance(value, str) or not value or len(value) > 240 or "\\" in value:
        raise ChronicleError("unsafe media path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise ChronicleError("unsafe media path")
    if path.suffix.lower() not in (".png", ".jpg", ".jpeg", ".webp"):
        raise ChronicleError("unsafe media path")
    if not all(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,126}", part) for part in path.parts):
        raise ChronicleError("unsafe media path")
    return path


def parse_facts(values, label):
    if not isinstance(values, list) or len(values) > 32:
        raise ChronicleError(label + " facts must be a bounded array")
    result = []
    for index, item in enumerate(values):
        if not isinstance(item, dict):
            raise ChronicleError(label + " fact must be an object")
        require_keys(item, ("label", "value"), ("label", "value"), label + " fact")
        result.append({
            "label": text_field(item, "label", label + " fact", 80),
            "value": text_field(item, "value", label + " fact", 500),
        })
    return result


def parse_media(values, label):
    if not isinstance(values, list) or len(values) > MAX_MEDIA:
        raise ChronicleError(label + " media must be a bounded array")
    result = []
    for item in values:
        if not isinstance(item, dict):
            raise ChronicleError(label + " media must be an object")
        require_keys(item, ("path", "caption", "alt"), ("path", "caption", "alt"), label + " media")
        result.append({
            "path": safe_media_path(item.get("path")),
            "caption": text_field(item, "caption", label + " media", 500),
            "alt": text_field(item, "alt", label + " media", 500),
        })
    return result


def parse_entities(data, key):
    values = data.get(key)
    if not isinstance(values, list) or len(values) > MAX_ENTITIES:
        raise ChronicleError(key + " must be a bounded array")
    result = {}
    for item in values:
        if not isinstance(item, dict):
            raise ChronicleError(key + " entry must be an object")
        allowed = {"id", "name", "status", "summary", "facts", "media"}
        required = set(allowed)
        if key == "experiments":
            allowed.add("attempt_ids")
            required.add("attempt_ids")
        require_keys(item, allowed, required, key + " entry")
        identifier = id_field(item, "id", key + " entry")
        if identifier in result:
            raise ChronicleError("duplicate " + key + " id " + identifier)
        entity = {
            "id": identifier,
            "name": text_field(item, "name", identifier, 160),
            "status": text_field(item, "status", identifier, 80),
            "summary": text_field(item, "summary", identifier, 2000),
            "facts": parse_facts(item["facts"], identifier),
            "media": parse_media(item["media"], identifier),
        }
        if key == "experiments":
            attempt_ids = item["attempt_ids"]
            if not isinstance(attempt_ids, list) or len(attempt_ids) > MAX_ENTITIES:
                raise ChronicleError(identifier + " attempt_ids must be a bounded array")
            entity["attempt_ids"] = []
            for attempt_id in attempt_ids:
                if not isinstance(attempt_id, str) or not SAFE_ID.fullmatch(attempt_id):
                    raise ChronicleError(identifier + " has invalid attempt id")
                if attempt_id in entity["attempt_ids"]:
                    raise ChronicleError(identifier + " has duplicate attempt id")
                entity["attempt_ids"].append(attempt_id)
        result[identifier] = entity
    return result


def load_catalog(path):
    data = load_json(path, MAX_CATALOG_BYTES, "catalog")
    if data_has_absolute_path(data):
        raise ChronicleError("catalog contains a private absolute path")
    require_keys(data, ("schema", "program", "missions", "vehicles", "sites", "experiments"),
                 ("schema", "program", "missions", "vehicles", "sites", "experiments"), "catalog")
    if data["schema"] != CATALOG_SCHEMA:
        raise ChronicleError("unsupported catalog schema")
    program = data["program"]
    if not isinstance(program, dict):
        raise ChronicleError("program must be an object")
    require_keys(program, ("name", "tagline", "summary"), ("name", "tagline", "summary"), "program")
    return {
        "program": {key: text_field(program, key, "program", 2000 if key == "summary" else 160)
                    for key in ("name", "tagline", "summary")},
        "missions": parse_entities(data, "missions"),
        "vehicles": parse_entities(data, "vehicles"),
        "sites": parse_entities(data, "sites"),
        "experiments": parse_entities(data, "experiments"),
    }


def parse_time(value):
    stamp = datetime.fromisoformat(safe_text(value, "generatedUtc", 64).replace("Z", "+00:00"))
    if stamp.tzinfo is None:
        raise ChronicleError("report generation time must include timezone")
    return stamp


def contains_absolute_path(value):
    boundary = r"(?:^|[\s'\"(=:,\[])"
    unix_path = re.search(boundary + r"/(?![/.])[^\s'\"]+", value)
    windows_drive = re.search(boundary + r"[A-Za-z]:[\\/][^\s'\"]+", value)
    windows_unc = re.search(boundary + r"\\\\[^\\\s'\"]+\\[^\s'\"]+", value)
    windows_root = re.search(boundary + r"\\(?!\\)[^\\\s'\"]+\\[^\s'\"]+", value)
    return bool(unix_path or windows_drive or windows_unc or windows_root or "file://" in value)


def data_has_absolute_path(value):
    if isinstance(value, str):
        return contains_absolute_path(value)
    if isinstance(value, dict):
        return any(data_has_absolute_path(key) or data_has_absolute_path(item)
                   for key, item in value.items())
    if isinstance(value, list):
        return any(data_has_absolute_path(item) for item in value)
    return False


class HtmlStrings(HTMLParser):
    def __init__(self):
        HTMLParser.__init__(self, convert_charrefs=True)
        self.values = []

    def handle_data(self, data):
        self.values.append(data)

    def handle_starttag(self, tag, attrs):
        self.values.extend(value for _, value in attrs if value is not None)

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)


def html_has_absolute_path(text):
    parser = HtmlStrings()
    parser.feed(text)
    return any(contains_absolute_path(value) for value in parser.values)


def parse_report_media(data):
    values = data.get("media", [])
    if not isinstance(values, list) or len(values) > 64:
        raise ChronicleError("report media list is invalid")
    result = []
    allowed_statuses = {"confirmed", "below-required-resolution", "missing", "unconfirmed", "requested"}
    for item in values:
        if not isinstance(item, dict):
            raise ChronicleError("report media entry is invalid")
        filename = item.get("filename")
        status = item.get("status")
        if not isinstance(filename, str) or not SAFE_MEDIA_NAME.fullmatch(filename):
            raise ChronicleError("report media filename is unsafe")
        if status not in allowed_statuses:
            raise ChronicleError("report media status is invalid")
        width, height = item.get("width"), item.get("height")
        if status in ("confirmed", "below-required-resolution"):
            if (not isinstance(width, int) or isinstance(width, bool) or not isinstance(height, int)
                    or isinstance(height, bool) or width < 1 or height < 1
                    or width > 32768 or height > 32768):
                raise ChronicleError("completed report media dimensions are invalid")
        label = re.sub(r"-[0-9a-f]{32}$", "", filename[:-4], flags=re.IGNORECASE)
        label = " ".join(part for part in label.replace("_", "-").split("-") if part).title()
        result.append({"filename": filename, "status": status, "width": width,
                       "height": height, "label": label or "Mission"})
    return result


def parse_report_lineage(data, attempt_id):
    parent_attempt = data.get("parentAttemptId")
    checkpoint = data.get("parentCheckpoint")
    digest = data.get("parentCheckpointSha256")
    if parent_attempt is None and digest is None:
        return {"parent_attempt_id": None, "parent_checkpoint": None, "parent_checkpoint_sha256": None}
    if parent_attempt is None or checkpoint is None or digest is None:
        raise ChronicleError("incomplete checkpoint lineage for " + attempt_id)
    if not isinstance(parent_attempt, str) or not SAFE_ATTEMPT_ID.fullmatch(parent_attempt):
        raise ChronicleError("invalid parent attempt id for " + attempt_id)
    if parent_attempt == attempt_id:
        raise ChronicleError("checkpoint lineage may not parent itself: " + attempt_id)
    if not isinstance(checkpoint, str) or not SAFE_CHECKPOINT.fullmatch(checkpoint):
        raise ChronicleError("invalid parent checkpoint for " + attempt_id)
    if not isinstance(digest, str) or not SAFE_SHA256.fullmatch(digest):
        raise ChronicleError("invalid parent checkpoint digest for " + attempt_id)
    return {
        "parent_attempt_id": parent_attempt,
        "parent_checkpoint": checkpoint,
        "parent_checkpoint_sha256": digest.lower(),
    }


def load_reports(archive, output):
    attempts = {}
    for manifest_path in sorted(archive.glob("*/manifest.json")):
        folder = manifest_path.parent
        if folder == output or folder.is_symlink() or manifest_path.is_symlink():
            if folder == output:
                continue
            raise ChronicleError("report source may not be a symbolic link")
        if not SAFE_FOLDER.fullmatch(folder.name):
            raise ChronicleError("unsafe report folder name")
        data = load_json(manifest_path, 256 * 1024, "report manifest")
        if data.get("schema") != MANIFEST_SCHEMA or data.get("report") != "index.html":
            raise ChronicleError("unsupported report manifest")
        if data_has_absolute_path(data):
            raise ChronicleError("report manifest contains a private absolute path")
        report_path = folder / "index.html"
        if report_path.is_symlink() or not report_path.is_file():
            raise ChronicleError("report page is missing or symbolic")
        report_text = bounded_text(report_path, MAX_REPORT_BYTES)
        if html_has_absolute_path(report_text):
            raise ChronicleError("report page contains a private absolute path")
        playback = parse_telemetry_playback(data, folder)
        if playback is not None and html_has_absolute_path(bounded_text(playback["path"], MAX_PLAYBACK_BYTES)):
            raise ChronicleError("telemetry playback contains a private absolute path")
        attempt_id = id_field(data, "attemptId", "report manifest")
        report = {
            "folder": folder,
            "folder_name": folder.name,
            "html": report_text,
            "manifest_path": manifest_path,
            "manifest": data,
            "generated": parse_time(data.get("generatedUtc")),
            "mission_id": id_field(data, "missionId", "report manifest"),
            "attempt_id": attempt_id,
            "vehicle_id": id_field(data, "vehicleDesignId", "report manifest"),
            "site_id": data.get("siteId"),
            "title": text_field(data, "title", "report manifest", 160),
            "outcome": text_field(data, "outcome", "report manifest", 80),
            "media": parse_report_media(data),
            "telemetry": playback,
        }
        report.update(parse_report_lineage(data, attempt_id))
        if report["site_id"] is not None and (not isinstance(report["site_id"], str) or not SAFE_ID.fullmatch(report["site_id"])):
            raise ChronicleError("report manifest has invalid siteId")
        attempts.setdefault(report["attempt_id"], []).append(report)
    if not attempts:
        raise ChronicleError("archive has no chronicle reports")
    for attempt_id, versions in attempts.items():
        versions.sort(key=lambda item: (item["generated"], item["folder_name"]), reverse=True)
        identity = (versions[0]["mission_id"], versions[0]["vehicle_id"], versions[0]["site_id"])
        if any((item["mission_id"], item["vehicle_id"], item["site_id"]) != identity for item in versions[1:]):
            raise ChronicleError("report renderings disagree on identity for " + attempt_id)
        lineage = (versions[0]["parent_attempt_id"], versions[0]["parent_checkpoint"],
                   versions[0]["parent_checkpoint_sha256"])
        if any((item["parent_attempt_id"], item["parent_checkpoint"], item["parent_checkpoint_sha256"]) != lineage
               for item in versions[1:]):
            raise ChronicleError("report renderings disagree on lineage for " + attempt_id)
    return attempts


def validate_links(catalog, attempts):
    for attempt_id, versions in attempts.items():
        report = versions[0]
        if report["mission_id"] not in catalog["missions"]:
            raise ChronicleError("missing mission " + report["mission_id"] + " for " + attempt_id)
        if report["vehicle_id"] not in catalog["vehicles"]:
            raise ChronicleError("missing vehicle " + report["vehicle_id"] + " for " + attempt_id)
        if report["site_id"] is not None and report["site_id"] not in catalog["sites"]:
            raise ChronicleError("missing site " + report["site_id"] + " for " + attempt_id)
    for experiment in catalog["experiments"].values():
        for attempt_id in experiment["attempt_ids"]:
            if attempt_id not in attempts:
                raise ChronicleError("experiment " + experiment["id"] + " references missing attempt " + attempt_id)
    for attempt_id, versions in attempts.items():
        parent = versions[0]["parent_attempt_id"]
        if parent is not None and parent not in attempts:
            raise ChronicleError(attempt_id + " references missing parent attempt " + parent)
    states = {}

    def visit(attempt_id):
        state = states.get(attempt_id, 0)
        if state == 1:
            raise ChronicleError("checkpoint lineage cycle includes " + attempt_id)
        if state == 2:
            return
        states[attempt_id] = 1
        parent = attempts[attempt_id][0]["parent_attempt_id"]
        if parent is not None:
            visit(parent)
        states[attempt_id] = 2

    for attempt_id in attempts:
        visit(attempt_id)


def nav(prefix, program_name):
    links = (("", program_name), ("missions/index.html", "Missions"), ("attempts/index.html", "Attempts"),
             ("vehicles/index.html", "Vehicles"), ("sites/index.html", "Sites"),
             ("experiments/index.html", "Experiments"))
    return '<nav class="site-nav" aria-label="Space program">' + "".join(
        '<a href="{}{}">{}</a>'.format(prefix, path, esc(label)) for path, label in links) + "</nav>"


def render_page(program, title, kicker, heading, summary, content, prefix):
    template = bounded_text(TEMPLATE, 256 * 1024)
    return Template(template).substitute(
        page_title=esc(title + " · " + program["name"]), nav=nav(prefix, program["name"]),
        kicker=esc(kicker), heading=esc(heading), summary=esc(summary), content=content,
        program_name=esc(program["name"]),
    )


def entity_url(kind, identifier, prefix=""):
    return prefix + kind + "/" + quote(identifier, safe="") + ".html"


def card(url, identifier, name, status, summary):
    return ('<article class="card"><div class="identity">{}</div><h2><a href="{}" aria-label="{} ({})">{}</a></h2>'
            '<span class="status">{}</span><p>{}</p></article>').format(
                esc(identifier), url, esc(name), esc(identifier), esc(name), esc(status), esc(summary))


def relation_cards(title, items, kind):
    if not items:
        return ""
    cards = "".join(card("../" + kind + "/" + quote(item["id"], safe="") +
                              ("/index.html" if kind == "attempts" else ".html"),
                         item["id"], item["name"], item["status"], item["summary"])
                    for item in items)
    return '<section><h2>{}</h2><div class="grid">{}</div></section>'.format(esc(title), cards)


def facts_html(entity):
    if not entity["facts"]:
        return ""
    return '<section><h2>Facts</h2><dl>' + "".join(
        '<dt>{}</dt><dd>{}</dd>'.format(esc(item["label"]), esc(item["value"]))
        for item in entity["facts"]) + "</dl></section>"


def media_html(entity):
    if not entity["media"]:
        return ""
    figures = "".join(
        '<figure><a href="../assets/catalog/{}"><img src="../assets/catalog/{}" alt="{}"></a>'
        '<figcaption>{}</figcaption></figure>'.format(
            esc(item["path"].as_posix()), esc(item["path"].as_posix()),
            esc(item["alt"]), esc(item["caption"]))
        for item in entity["media"])
    return '<section><h2>Media</h2><div class="gallery">{}</div></section>'.format(figures)


def attempt_view(attempt_id, versions, catalog, attempts, descendants):
    latest = versions[0]
    experiments = [item for item in catalog["experiments"].values() if attempt_id in item["attempt_ids"]]
    mission = catalog["missions"][latest["mission_id"]]
    vehicle = catalog["vehicles"][latest["vehicle_id"]]
    links = [
        ("../../missions/" + quote(latest["mission_id"], safe="") + ".html",
         mission["name"] + " (" + latest["mission_id"] + ")"),
        ("../../vehicles/" + quote(latest["vehicle_id"], safe="") + ".html",
         vehicle["name"] + " (" + latest["vehicle_id"] + ")"),
    ]
    if latest["site_id"]:
        site = catalog["sites"][latest["site_id"]]
        links.append(("../../sites/" + quote(latest["site_id"], safe="") + ".html",
                      site["name"] + " (" + latest["site_id"] + ")"))
    links.extend(("../../experiments/" + quote(item["id"], safe="") + ".html",
                  item["name"] + " (" + item["id"] + ")")
                 for item in experiments)
    source = "../../../" + quote(latest["folder_name"], safe="") + "/index.html"
    report_links = " ".join('<a href="{}">{}</a>'.format(url, esc(label)) for url, label in links)
    report_links += ' <a href="{}">Original report</a>'.format(source)
    if latest["telemetry"] is not None:
        report_links += ' <a href="telemetry.html">Telemetry playback</a>'
    context = ('<aside class="program-context"><div>Recorded outcome: <strong>{}</strong></div>'
               '<div class="program-links">{}</div>').format(esc(latest["outcome"]), report_links)
    if latest["parent_attempt_id"] is not None:
        parent = attempts[latest["parent_attempt_id"]][0]
        context += ('<section class="program-lineage"><h2>Checkpoint start</h2><p>Started from '
                    '<a href="../{}/index.html">{} ({})</a> at checkpoint <code>{}</code>.</p>'
                    '<p>SHA-256 <code>{}</code></p><p>Recorded lineage evidence does not establish '
                    'deterministic replay.</p></section>').format(
                        quote(parent["attempt_id"], safe=""), esc(parent["title"]), esc(parent["attempt_id"]),
                        esc(latest["parent_checkpoint"]), esc(latest["parent_checkpoint_sha256"]))
    children = descendants.get(attempt_id, [])
    if children:
        context += '<section class="program-lineage"><h2>Checkpoint descendants</h2><ul>' + "".join(
            '<li><a href="../{}/index.html">{} ({})</a> · checkpoint <code>{}</code> · SHA-256 <code>{}</code></li>'.format(
                quote(child["attempt_id"], safe=""), esc(child["title"]), esc(child["attempt_id"]),
                esc(child["parent_checkpoint"]), esc(child["parent_checkpoint_sha256"]))
            for child in children) + '</ul><p>Recorded lineage evidence does not establish deterministic replay.</p></section>'
    if len(versions) > 1:
        context += '<details><summary>Earlier immutable renderings</summary><ul>' + "".join(
            '<li><a href="../../../{}/index.html">{} · {}</a></li>'.format(
                quote(item["folder_name"], safe=""), esc(item["title"]), esc(item["generated"].isoformat()))
            for item in versions[1:]) + "</ul></details>"
    completed_media = [item for item in latest["media"]
                       if item["status"] in ("confirmed", "below-required-resolution")]
    if completed_media:
        context += '<details><summary>Report media</summary><div class="program-gallery">' + "".join(
            '<figure><a href="media/{}"><img src="media/{}" alt="{} screenshot"></a>'
            '<figcaption>{} · {} · {} × {}</figcaption></figure>'.format(
                esc(item["filename"]), esc(item["filename"]), esc(item["label"]),
                esc(item["filename"]), esc(item["status"]), esc(item["width"]), esc(item["height"]))
            for item in completed_media) + "</div></details>"
    context += "</aside>"
    copy_footer = ('<footer class="program-copy-note">This connected view adds program navigation to a generated copy. '
                   '<a href="{}">The original report</a> and its provenance remain unchanged.</footer>').format(source)
    shell_css = ("<style id=\"space-program-shell\">.site-nav{display:flex;flex-wrap:wrap;gap:8px 18px;padding:14px;"
                 "border-bottom:1px solid #50636b;background:#091216}.site-nav a{color:#a9ddff}.site-nav a:first-child{color:#f0c35a;"
                 "font-weight:800}.program-context{margin:18px auto;padding:16px;max-width:1100px;border:1px solid #50636b;"
                 "border-radius:10px;background:#102026;color:#f2f0e6}.program-links{display:flex;flex-wrap:wrap;gap:8px 16px;"
                 "margin-top:8px}.program-context details{margin-top:10px}.program-gallery{display:grid;grid-template-columns:"
                 "repeat(auto-fit,minmax(min(280px,100%),1fr));gap:12px}.program-gallery img{display:block;width:100%;height:auto;"
                 "border-radius:8px}.program-gallery figure{margin:0}.program-gallery figcaption{overflow-wrap:anywhere}"
                 ".program-lineage{margin-top:14px;padding:14px;border:1px solid #50636b;border-radius:8px}.program-lineage h2{"
                 "margin-top:0}.program-lineage code{overflow-wrap:anywhere}.program-copy-note{margin:24px auto;padding:16px;"
                 "max-width:1100px;color:#aeb7bd}</style>")
    page = latest["html"]
    if not re.search(r"</head\s*>", page, re.IGNORECASE) or not re.search(r"<body(?:\s[^>]*)?>", page, re.IGNORECASE):
        raise ChronicleError("report page cannot accept shared navigation")
    page = re.sub(r"</head\s*>", shell_css + "</head>", page, count=1, flags=re.IGNORECASE)
    shell = nav("../../", catalog["program"]["name"]) + context
    page = re.sub(r"(<body(?:\s[^>]*)?>)", r"\1" + shell, page, count=1, flags=re.IGNORECASE)
    return re.sub(r"</body\s*>", copy_footer + "</body>", page, count=1, flags=re.IGNORECASE)


def playback_view(playback, catalog):
    content = bounded_bytes(playback["path"], MAX_PLAYBACK_BYTES)
    if hashlib.sha256(content).hexdigest() != playback["sha256"]:
        raise ChronicleError("telemetry playback changed before navigation was added")
    try:
        page = content.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ChronicleError("telemetry playback is not valid UTF-8") from error
    if not re.search(r"</head\s*>", page, re.IGNORECASE) or not re.search(r"<body(?:\s[^>]*)?>", page, re.IGNORECASE):
        raise ChronicleError("telemetry playback cannot accept shared navigation")
    shell_css = ('<style id="space-program-playback-shell">.site-nav{display:flex;flex-wrap:wrap;gap:8px 18px;'
                 'padding:14px;border:1px solid #50636b;border-radius:10px;background:#091216}'
                 '.site-nav a{color:#a9ddff}.site-nav a:first-child{color:#f0c35a;font-weight:800}</style>')
    page = re.sub(r"</head\s*>", shell_css + "</head>", page, count=1, flags=re.IGNORECASE)
    return re.sub(r"(<body(?:\s[^>]*)?>)", r"\1" + nav("../../", catalog["program"]["name"]),
                  page, count=1, flags=re.IGNORECASE)


def copy_file(source, destination, maximum, state, source_root):
    if source.is_symlink() or not source.is_file():
        raise ChronicleError("media source is missing or symbolic: " + source.name)
    if source_root.resolve() not in source.resolve().parents:
        raise ChronicleError("media source escapes archive")
    size = source.stat().st_size
    if size > maximum:
        raise ChronicleError("media source exceeds size limit: " + source.name)
    state["bytes"] += size
    state["files"] += 1
    if state["bytes"] > MAX_TOTAL_MEDIA_BYTES or state["files"] > MAX_TOTAL_MEDIA_FILES:
        raise ChronicleError("site media exceeds total limit")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)


def build_site(staging, archive, catalog, attempts, catalog_sha256):
    program = catalog["program"]
    for directory in ("missions", "attempts", "vehicles", "sites", "experiments"):
        (staging / directory).mkdir(parents=True)
    media_state = {"bytes": 0, "files": 0}
    for group in ("missions", "vehicles", "sites", "experiments"):
        for entity in catalog[group].values():
            for item in entity["media"]:
                source = archive.joinpath(*item["path"].parts)
                destination = staging / "assets" / "catalog" / item["path"]
                copy_file(source, destination, MAX_MEDIA_BYTES, media_state, archive)

    attempt_summaries = {}
    experiment_by_attempt = {}
    for experiment in catalog["experiments"].values():
        for attempt_id in experiment["attempt_ids"]:
            experiment_by_attempt.setdefault(attempt_id, []).append(experiment)
    descendants = {}
    for versions in attempts.values():
        latest = versions[0]
        if latest["parent_attempt_id"] is not None:
            descendants.setdefault(latest["parent_attempt_id"], []).append(latest)
    for children in descendants.values():
        children.sort(key=lambda item: item["attempt_id"])
    for attempt_id, versions in attempts.items():
        latest = versions[0]
        attempt_summaries[attempt_id] = {
            "id": attempt_id, "name": latest["title"], "status": latest["outcome"],
            "summary": "Latest immutable rendering from {}. {} rendering{} preserved.".format(
                latest["generated"].date().isoformat(), len(versions), "s" if len(versions) != 1 else ""),
        }
        destination = staging / "attempts" / attempt_id
        destination.mkdir()
        (destination / "index.html").write_text(
            attempt_view(attempt_id, versions, catalog, attempts, descendants), encoding="utf-8")
        shutil.copyfile(latest["manifest_path"], destination / "manifest.json")
        if latest["telemetry"] is not None:
            playback_page = playback_view(latest["telemetry"], catalog)
            playback_bytes = playback_page.encode("utf-8")
            if len(playback_bytes) > MAX_PLAYBACK_BYTES:
                raise ChronicleError("connected telemetry playback exceeds size limit")
            media_state["bytes"] += len(playback_bytes)
            media_state["files"] += 1
            if (media_state["bytes"] > MAX_TOTAL_MEDIA_BYTES
                    or media_state["files"] > MAX_TOTAL_MEDIA_FILES):
                raise ChronicleError("site media exceeds total limit")
            (destination / "telemetry.html").write_bytes(playback_bytes)
        for item in latest["media"]:
            if item["status"] not in ("confirmed", "below-required-resolution"):
                continue
            filename = item["filename"]
            copy_file(latest["folder"] / "media" / filename, destination / "media" / filename,
                      MAX_MEDIA_BYTES, media_state, archive)

    home_cards = []
    for group, label in (("missions", "Mission"), ("vehicles", "Vehicle"), ("sites", "Site"), ("experiments", "Experiment")):
        for entity in catalog[group].values():
            home_cards.append(card(entity_url(group, entity["id"]), entity["id"], entity["name"], entity["status"], entity["summary"]))
    for item in attempt_summaries.values():
        home_cards.append(card("attempts/" + quote(item["id"], safe="") + "/index.html",
                               item["id"], item["name"], item["status"], item["summary"]))
    home_content = '<section><h2>Program records</h2><div class="grid">{}</div></section>'.format("".join(home_cards))
    (staging / "index.html").write_text(render_page(program, program["name"], "Connected archive",
                                                       program["name"], program["tagline"],
                                                       '<p class="lede">{}</p>{}'.format(esc(program["summary"]), home_content), ""), encoding="utf-8")

    groups = (("missions", "Missions"), ("attempts", "Attempts"), ("vehicles", "Vehicles"),
              ("sites", "Sites"), ("experiments", "Experiments"))
    for group, heading in groups:
        values = attempt_summaries if group == "attempts" else catalog[group]
        listing = "".join(card((quote(item["id"], safe="") + ("/index.html" if group == "attempts" else ".html")),
                               item["id"], item["name"], item["status"], item["summary"])
                          for item in values.values())
        (staging / group / "index.html").write_text(render_page(
            program, heading, "Program catalog", heading,
            "Maintained records connected to immutable flight evidence.",
            '<div class="grid">{}</div>'.format(listing) if listing else '<p class="empty">No records.</p>', "../"), encoding="utf-8")

    for group, heading in (("missions", "Mission"), ("vehicles", "Vehicle"),
                           ("sites", "Site"), ("experiments", "Experiment")):
        for entity in catalog[group].values():
            if group == "experiments":
                related_ids = entity["attempt_ids"]
            else:
                key = {"missions": "mission_id", "vehicles": "vehicle_id", "sites": "site_id"}[group]
                related_ids = [attempt_id for attempt_id, versions in attempts.items() if versions[0][key] == entity["id"]]
            related_attempts = [attempt_summaries[item] for item in related_ids]
            related_missions = {attempts[item][0]["mission_id"] for item in related_ids}
            related_vehicles = {attempts[item][0]["vehicle_id"] for item in related_ids}
            related_sites = {attempts[item][0]["site_id"] for item in related_ids if attempts[item][0]["site_id"]}
            related_experiments = {item["id"] for attempt_id in related_ids for item in experiment_by_attempt.get(attempt_id, [])}
            content = facts_html(entity) + media_html(entity)
            content += relation_cards("Attempts", related_attempts, "attempts")
            if group != "missions":
                content += relation_cards("Missions", [catalog["missions"][item] for item in sorted(related_missions)], "missions")
            if group != "vehicles":
                content += relation_cards("Vehicles", [catalog["vehicles"][item] for item in sorted(related_vehicles)], "vehicles")
            if group != "sites":
                content += relation_cards("Sites", [catalog["sites"][item] for item in sorted(related_sites)], "sites")
            if group != "experiments":
                content += relation_cards("Experiments", [catalog["experiments"][item] for item in sorted(related_experiments)], "experiments")
            (staging / group / (entity["id"] + ".html")).write_text(render_page(
                program, entity["name"], heading, entity["name"], entity["summary"], content, "../"), encoding="utf-8")

    reports = []
    for attempt_id, versions in sorted(attempts.items()):
        latest = versions[0]
        report = {"attemptId": attempt_id, "sourceFolder": latest["folder_name"],
                  "generatedUtc": latest["generated"].isoformat(),
                  "parentAttemptId": latest["parent_attempt_id"]}
        if latest["telemetry"] is not None:
            report["telemetryPlayback"] = {
                "report": "attempts/" + attempt_id + "/telemetry.html",
                "sourceSha256": latest["telemetry"]["sha256"],
                "generatedSha256": sha256(staging / "attempts" / attempt_id / "telemetry.html"),
            }
        reports.append(report)
    marker = {
        "schema": SITE_SCHEMA,
        "generatedUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "generatorSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "templateSha256": hashlib.sha256(TEMPLATE.read_bytes()).hexdigest(),
        "catalogSha256": catalog_sha256,
        "attempts": len(attempts), "reports": reports,
    }
    (staging / "site-manifest.json").write_text(json.dumps(marker, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def replace_site(staging, output):
    if not output.exists():
        os.rename(staging, output)
        return
    if output.is_symlink() or not output.is_dir():
        raise ChronicleError("existing output is not a marked generated site")
    marker = output / "site-manifest.json"
    if marker.is_symlink() or not marker.is_file():
        raise ChronicleError("existing output has no generated-site marker")
    data = load_json(marker, 64 * 1024, "existing site marker")
    if data.get("schema") != SITE_SCHEMA:
        raise ChronicleError("existing output has an unsupported marker")
    backup = output.parent / ("." + output.name + "-previous")
    if backup.exists():
        raise ChronicleError("stale site backup prevents safe rebuild")
    os.rename(output, backup)
    try:
        os.rename(staging, output)
    except Exception:
        os.rename(backup, output)
        raise
    shutil.rmtree(backup)


def generate(archive, catalog_path, output):
    archive = archive.resolve()
    catalog_path = catalog_path.resolve()
    output = output.resolve()
    if not archive.is_dir():
        raise ChronicleError("archive directory does not exist")
    if output.parent != archive:
        raise ChronicleError("output must be a direct child of the archive")
    if catalog_path == output or output in catalog_path.parents:
        raise ChronicleError("catalog may not be inside generated output")
    catalog = load_catalog(catalog_path)
    catalog_sha256 = hashlib.sha256(catalog_path.read_bytes()).hexdigest()
    attempts = load_reports(archive, output)
    validate_links(catalog, attempts)
    staging = Path(tempfile.mkdtemp(prefix=".space-program-site-", dir=str(archive)))
    try:
        build_site(staging, archive, catalog, attempts, catalog_sha256)
        replace_site(staging, output)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True)
        raise
    return len(attempts)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        count = generate(args.archive, args.catalog, args.output)
        print("Built connected site for {} attempts at {}".format(count, args.output))
        return 0
    except (ChronicleError, OSError, ValueError, KeyError) as error:
        print("space-program: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
