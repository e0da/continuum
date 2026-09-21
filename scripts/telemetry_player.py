#!/usr/bin/env python3
"""Make a standalone viewer of exact recorded mission observations."""
import argparse
import csv
import hashlib
import html
import io
import json
from pathlib import Path
import sys

from chronicle import ChronicleError, EXPECTED_TELEMETRY_HEADER, MAX_ROWS, MAX_TEXT_BYTES, finite_number

ROOT = Path(__file__).resolve().parents[1]
FIELDS = {'wall_s': 'wall', 'ut_s': 'ut', 'altitude_m': 'altitude',
          'surface_speed_mps': 'speed', 'throttle': 'throttle', 'stage': 'stage', 'parts': 'parts'}
MAX_OUTPUT_BYTES = 128 * 1024 * 1024


def capture(path):
    if path.is_symlink():
        raise ChronicleError('Source may not be a symbolic link')
    with path.open('rb') as stream:
        raw = stream.read(MAX_TEXT_BYTES + 1)
    if len(raw) > MAX_TEXT_BYTES:
        raise ChronicleError('Source exceeds byte limit')
    reader = csv.DictReader(io.StringIO(raw.decode('utf-8'), newline=''))
    if reader.fieldnames != EXPECTED_TELEMETRY_HEADER:
        raise ChronicleError('Unsupported mission telemetry header')
    rows = []
    for row in reader:
        if len(rows) == MAX_ROWS or None in row or any(v is None or len(v) > 4096 for v in row.values()):
            raise ChronicleError('Malformed or oversized telemetry')
        item = {k: row[k] for k in ('phase', 'body', 'situation', 'autopilot')}
        if not item['phase']:
            raise ChronicleError('Missing phase')
        for field, key in FIELDS.items():
            value = None if row[field] == '' else finite_number(row[field], field)
            if value is not None and abs(value) > 1e15:
                raise ChronicleError('Value exceeds viewer numeric range: ' + field)
            item[key] = value
        if item['wall'] is None or item['ut'] is None or item['wall'] < 0 or item['ut'] < 0:
            raise ChronicleError('Missing or negative clock')
        if rows and (item['wall'] < rows[-1]['wall'] or item['ut'] < rows[-1]['ut']):
            raise ChronicleError('Telemetry clock moved backward')
        if item['throttle'] is not None and not 0 <= item['throttle'] <= 1:
            raise ChronicleError('Throttle is outside 0..1')
        for field in ('parts', 'stage'):
            minimum = -1 if field == 'stage' else 0
            if item[field] is not None and (item[field] < minimum or not item[field].is_integer()):
                raise ChronicleError('Invalid ' + field)
        if row['packed'] not in ('', 'True', 'False'):
            raise ChronicleError('Invalid packed state')
        item['packed'] = None if not row['packed'] else row['packed'] == 'True'
        rows.append(item)
    if not rows:
        raise ChronicleError('No observations')
    return {'schema': 'ksp-continuum-telemetry-playback/v1', 'sourceSha256': hashlib.sha256(raw).hexdigest(),
            'rows': rows, 'scope': 'recorded observations only; no state reconstruction or resimulation'}


def render(data, title, back_to_report=False):
    maximum_title = 227 if back_to_report else 160
    if not isinstance(title, str) or not title.strip() or len(title) > maximum_title:
        raise ChronicleError('Title must contain 1..{} characters'.format(maximum_title))
    encoded = (json.dumps(data, allow_nan=False, separators=(',', ':'))
               .replace('&', '\\u0026').replace('<', '\\u003c').replace('>', '\\u003e'))
    template = (ROOT / 'templates/telemetry-player.html').read_text()
    if template.count('<!--DATA-->') != 1 or template.count('<!--TITLE-->') < 1:
        raise ChronicleError('Telemetry player template placeholders are invalid')
    page = template.replace('<!--TITLE-->', html.escape(title)).replace('<!--DATA-->', encoded)
    if back_to_report:
        backlink = '<nav aria-label="Mission navigation"><a href="index.html">Back to mission report</a></nav>'
        if page.count('<body>') != 1:
            raise ChronicleError('Telemetry player template has no unique body')
        page = page.replace('<body>', '<body>' + backlink, 1)
    if len(page.encode('utf-8')) > MAX_OUTPUT_BYTES:
        raise ChronicleError('Generated telemetry playback exceeds size limit')
    return page


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('--title', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        data = capture(args.source)
        page = render(data, args.title)
        with args.output.open('x', encoding='utf-8') as output:
            output.write(page)
        print('Wrote ' + str(len(data['rows'])) + ' recorded observations to ' + str(args.output))
    except (ChronicleError, OSError, UnicodeError, csv.Error) as error:
        print('telemetry_player: ' + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
