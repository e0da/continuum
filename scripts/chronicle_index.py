#!/usr/bin/env python3
"""Build a relocatable index of generated mission chronicles without replacing history."""
import argparse
from datetime import datetime
import html
import json
import os
from pathlib import Path
import re
from string import Template
import tempfile
from urllib.parse import quote

from chronicle import bounded_text, ChronicleError, MANIFEST_SCHEMA, parse_telemetry_playback, safe_text


def generate(archive, filename, refresh=False):
    if not archive.is_dir():
        raise ChronicleError("archive directory does not exist")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*\.html", filename):
        raise ChronicleError("output must be an HTML filename inside the archive")
    output = archive / filename
    if output.is_symlink() or (output.exists() and not refresh):
        raise ChronicleError("output already exists; choose a new index filename")
    attempts = {}
    for manifest_path in sorted(archive.glob("*/manifest.json")):
        folder = manifest_path.parent
        if folder.is_symlink() or manifest_path.is_symlink():
            raise ChronicleError("report must be inside the archive, not a symbolic link")
        data = json.loads(bounded_text(manifest_path, 256 * 1024))
        if not isinstance(data, dict) or data.get("schema") != MANIFEST_SCHEMA:
            raise ChronicleError("unsupported report manifest")
        if data.get("report") != "index.html":
            raise ChronicleError("report must name the local index.html entrypoint")
        report = folder / "index.html"
        if not report.is_file() or report.is_symlink():
            raise ChronicleError("report page is missing or is a symbolic link")
        playback = parse_telemetry_playback(data, folder)
        title, attempt, outcome = [html.escape(safe_text(data.get(key), key, 500))
                                   for key in ("title", "attemptId", "outcome")]
        link = quote(folder.name, safe="") + "/index.html"
        generated = safe_text(data.get("generatedUtc"), "generatedUtc", 64)
        stamp = datetime.fromisoformat(generated.replace("Z", "+00:00"))
        if stamp.tzinfo is None:
            raise ChronicleError("report generation time must include timezone")
        playback_link = (quote(folder.name, safe="") + "/" + playback["report"]
                         if playback is not None else None)
        attempts.setdefault(attempt, []).append((stamp, link, title, outcome, playback_link))
    cards = []
    for attempt, versions in sorted(attempts.items(), reverse=True):
        versions.sort(reverse=True)
        stamp, link, title, outcome, playback_link = versions[0]
        history = ""
        if len(versions) > 1:
            history = '<details><summary>Earlier renderings</summary><ul>' + "".join(
                '<li><a href="{}">{}</a> · {}{}</li>'.format(
                    url, name, date.isoformat(),
                    ' · <a href="{}">telemetry</a>'.format(player) if player else "")
                for date, url, name, _, player in versions[1:]) + '</ul></details>'
        playback_html = ('<p><a href="{}">Recorded telemetry playback</a></p>'.format(playback_link)
                         if playback_link else "")
        cards.append('<article><p class="identity">{}</p><h2><a href="{}">{}</a></h2>'
                     '<p>Recorded outcome: <strong>{}</strong></p>{}{}</article>'.format(
                         attempt, link, title, outcome, playback_html, history))
    if not cards:
        raise ChronicleError("archive has no generated reports")
    template = Path(__file__).resolve().parents[1] / "templates/space-program-v1.html"
    page = Template(bounded_text(template, 128 * 1024)).substitute(reports="\n".join(cards))
    if refresh:
        descriptor, temporary = tempfile.mkstemp(prefix=".index-", dir=archive)
        try:
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                stream.write(page)
            os.replace(temporary, output)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
    else:
        with output.open("x", encoding="utf-8") as stream:
            stream.write(page)
    return len(cards)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--output", required=True, help="New HTML filename inside the archive")
    parser.add_argument("--refresh", action="store_true", help="Replace the derived index; never modify report directories")
    args = parser.parse_args()
    try:
        count = generate(args.archive, args.output, args.refresh)
    except (ChronicleError, OSError, ValueError, KeyError) as error:
        parser.exit(1, "Chronicle index: " + str(error) + "\n")
    print("Indexed {} mission reports.".format(count))


if __name__ == "__main__":
    main()
