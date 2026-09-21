#!/usr/bin/env python3
"""Compare two KSP Continuum input timeline segments on an explicit sample domain."""

import argparse
from bisect import bisect_right
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import re
import sys


SCHEMA = "ksp-continuum-input-timeline/v1"
REPORT_SCHEMA = "ksp-continuum-input-comparison/v1"
MAX_FILE_BYTES = 16 * 1024 * 1024
MAX_LINE_LENGTH = 1024
MAX_LINES = 100000
MAX_TRACKS = 64
MAX_KEYS_PER_TRACK = 32768
MAX_TOTAL_KEYS = 65536
MAX_EVENTS = 8192
MAX_DECLARED_SAMPLES = 100000
MAX_EVALUATED_SAMPLES = 250000
SAFE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_.-]{1,64}$")
INTEGER = re.compile(r"^[+-]?[0-9]+$")
NUMBER = re.compile(r"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$")
MODES = {"step", "linear", "cubic-bezier"}


class ComparisonError(Exception):
    pass


@dataclass(frozen=True)
class Key:
    time: float
    value: float
    mode: str
    control1: float
    control2: float


@dataclass(frozen=True)
class Track:
    name: str
    minimum: float
    maximum: float
    keys: tuple
    times: tuple

    def evaluate(self, time):
        index = bisect_right(self.times, time) - 1
        if index == len(self.keys) - 1:
            return self.keys[index].value
        start = self.keys[index]
        end = self.keys[index + 1]
        if start.mode == "step":
            return start.value
        u = (time - start.time) / (end.time - start.time)
        if start.mode == "linear":
            return interpolate(start.value, end.value, u)
        first = interpolate(start.value, start.control1, u)
        second = interpolate(start.control1, start.control2, u)
        third = interpolate(start.control2, end.value, u)
        return interpolate(interpolate(first, second, u), interpolate(second, third, u), u)


@dataclass(frozen=True)
class Timeline:
    duration: float
    tracks: dict
    sha256: str
    filename: str


class RmsAccumulator:
    def __init__(self):
        self.count = 0
        self.scale = 0.0
        self.scaled_squares = 1.0

    def add(self, value):
        self.count += 1
        if value == 0:
            return
        if self.scale < value:
            self.scaled_squares = 1.0 + self.scaled_squares * (self.scale / value) ** 2
            self.scale = value
        else:
            self.scaled_squares += (value / self.scale) ** 2

    def value(self):
        return 0.0 if self.scale == 0 else self.scale * math.sqrt(self.scaled_squares / self.count)


def interpolate(start, end, u):
    if start == end:
        return start
    if (start < 0) != (end < 0):
        return (start * (1 - u)) + (end * u)
    return start + ((end - start) * u)


def finite_number(value, label):
    if not isinstance(value, str) or not NUMBER.fullmatch(value):
        raise ComparisonError("invalid " + label)
    try:
        result = float(value)
    except ValueError as error:
        raise ComparisonError("invalid " + label) from error
    if not math.isfinite(result):
        raise ComparisonError("invalid " + label)
    return result


def identifier(value, label):
    if not SAFE_IDENTIFIER.fullmatch(value):
        raise ComparisonError("invalid " + label)
    return value


def fields(line, expected):
    result = line.split(",")
    if len(result) != expected:
        raise ComparisonError("timeline row has the wrong number of fields")
    return result


def read_lines(path):
    if path.is_symlink():
        raise ComparisonError("timeline source may not be a symbolic link")
    if not path.is_file():
        raise ComparisonError("timeline source does not exist: " + path.name)
    with path.open("rb") as stream:
        data = stream.read(MAX_FILE_BYTES + 1)
    if len(data) > MAX_FILE_BYTES:
        raise ComparisonError("timeline exceeds the byte limit")
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ComparisonError("timeline must be valid UTF-8") from error
    text = text.replace("\r\n", "\n")
    if "\r" in text:
        raise ComparisonError("timeline contains an invalid carriage return")
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    if not lines or any(line == "" for line in lines):
        raise ComparisonError("timeline contains a blank line")
    if len(lines) > MAX_LINES:
        raise ComparisonError("timeline exceeds the line limit")
    if any(len(line) > MAX_LINE_LENGTH for line in lines):
        raise ComparisonError("timeline line exceeds the length limit")
    return data, lines


def parse_timeline(path):
    path = Path(path)
    data, lines = read_lines(path)
    index = 0

    def expect(value):
        nonlocal index
        if index >= len(lines) or lines[index] != value:
            raise ComparisonError("expected header '" + value + "'")
        index += 1

    expect("schema," + SCHEMA)
    if index >= len(lines):
        raise ComparisonError("timeline ended before duration")
    duration_row = fields(lines[index], 2)
    index += 1
    if duration_row[0] != "duration":
        raise ComparisonError("expected the duration row")
    duration = finite_number(duration_row[1], "duration")
    if duration <= 0:
        raise ComparisonError("duration must be positive")
    expect("track,name,min,max")
    specifications = []
    while index < len(lines) and lines[index].startswith("track,"):
        row = fields(lines[index], 4)
        index += 1
        name = identifier(row[1], "track name")
        if any(item[0] == name for item in specifications):
            raise ComparisonError("duplicate track name " + name)
        if len(specifications) == MAX_TRACKS:
            raise ComparisonError("timeline has too many tracks")
        minimum = finite_number(row[2], "track minimum")
        maximum = finite_number(row[3], "track maximum")
        if minimum > maximum:
            raise ComparisonError("track minimum exceeds maximum")
        specifications.append((name, minimum, maximum))
    if not specifications:
        raise ComparisonError("timeline requires at least one track")
    expect("key,track,time,value,mode,control1,control2")
    key_lists = {name: [] for name, _minimum, _maximum in specifications}
    total_keys = 0
    while index < len(lines) and lines[index].startswith("key,"):
        row = fields(lines[index], 7)
        index += 1
        name = row[1]
        if name not in key_lists:
            raise ComparisonError("key references unknown track " + name)
        if len(key_lists[name]) == MAX_KEYS_PER_TRACK or total_keys == MAX_TOTAL_KEYS:
            raise ComparisonError("timeline has too many keys")
        mode = row[4]
        if mode not in MODES:
            raise ComparisonError("unknown interpolation mode " + mode)
        key_lists[name].append(Key(
            finite_number(row[2], "key time"), finite_number(row[3], "key value"), mode,
            finite_number(row[5], "key control1"), finite_number(row[6], "key control2")))
        total_keys += 1
    expect("event,time,name,value")
    events = 0
    while index < len(lines):
        row = fields(lines[index], 4)
        index += 1
        if row[0] != "event":
            raise ComparisonError("expected an event row")
        events += 1
        if events > MAX_EVENTS:
            raise ComparisonError("timeline has too many events")
        event_time = finite_number(row[1], "event time")
        if event_time < 0 or event_time > duration:
            raise ComparisonError("event time is outside timeline duration")
        identifier(row[2], "event name")
        if not INTEGER.fullmatch(row[3]) or not -(2 ** 31) <= int(row[3]) <= 2 ** 31 - 1:
            raise ComparisonError("invalid event value")

    tracks = {}
    for name, minimum, maximum in specifications:
        keys = key_lists[name]
        if not keys or keys[0].time != 0:
            raise ComparisonError("track " + name + " must start with a key at time zero")
        previous = None
        for key in keys:
            if key.time < 0 or key.time > duration or (previous is not None and key.time <= previous):
                raise ComparisonError("invalid key time order for track " + name)
            if any(value < minimum or value > maximum
                   for value in (key.value, key.control1, key.control2)):
                raise ComparisonError("key value is outside range for track " + name)
            previous = key.time
        tracks[name] = Track(name, minimum, maximum, tuple(keys), tuple(key.time for key in keys))
    return Timeline(duration, tracks, hashlib.sha256(data).hexdigest(), path.name)


def regular_grid(start, end, samples):
    return [start if index == 0 else end if index == samples - 1
            else start + ((end - start) * (index / (samples - 1)))
            for index in range(samples)]


def compare(left, right, start, end, samples, tolerance):
    if left.duration != right.duration:
        raise ComparisonError("timeline durations differ")
    left_names = set(left.tracks)
    right_names = set(right.tracks)
    if left_names != right_names:
        missing_left = sorted(right_names - left_names)
        missing_right = sorted(left_names - right_names)
        raise ComparisonError(
            "channel sets differ; missing from left: {}; missing from right: {}".format(
                ",".join(missing_left) or "none", ",".join(missing_right) or "none"))
    for name in sorted(left_names):
        left_track = left.tracks[name]
        right_track = right.tracks[name]
        if (left_track.minimum, left_track.maximum) != (right_track.minimum, right_track.maximum):
            raise ComparisonError("range differs for channel " + name)
    if start < 0 or end <= start or end > left.duration:
        raise ComparisonError("aligned domain must be within timeline duration and have positive width")
    if samples < 2 or samples > MAX_DECLARED_SAMPLES:
        raise ComparisonError("samples must be between 2 and %d" % MAX_DECLARED_SAMPLES)
    if tolerance < 0 or not math.isfinite(tolerance):
        raise ComparisonError("tolerance must be a nonnegative finite number")

    declared = regular_grid(start, end, samples)
    times = set(declared)
    for timeline in (left, right):
        for track in timeline.tracks.values():
            times.update(key.time for key in track.keys if start <= key.time <= end)
    times = sorted(times)
    if len(times) > MAX_EVALUATED_SAMPLES:
        raise ComparisonError("declared grid plus breakpoints exceeds the sample limit")

    channel_reports = []
    overall_rms = RmsAccumulator()
    overall_max = 0.0
    overall_first = None
    for name in sorted(left_names):
        channel_rms = RmsAccumulator()
        channel_max = 0.0
        first = None
        divergent = 0
        for time in times:
            deviation = abs(left.tracks[name].evaluate(time) - right.tracks[name].evaluate(time))
            if not math.isfinite(deviation):
                raise ComparisonError("deviation exceeds finite numeric range for channel " + name)
            channel_rms.add(deviation)
            overall_rms.add(deviation)
            if deviation > channel_max:
                channel_max = deviation
            if deviation > overall_max:
                overall_max = deviation
            if deviation > tolerance:
                divergent += 1
                if first is None:
                    first = time
                candidate = (time, name)
                if overall_first is None or candidate < overall_first:
                    overall_first = candidate
        channel_reports.append({
            "name": name,
            "maxAbsDeviation": channel_max,
            "rmsDeviation": channel_rms.value(),
            "firstDivergenceTime": first,
            "divergentSamples": divergent,
            "withinTolerance": divergent == 0,
        })
    return {
        "schema": REPORT_SCHEMA,
        "left": {"filename": left.filename, "sha256": left.sha256},
        "right": {"filename": right.filename, "sha256": right.sha256},
        "duration": left.duration,
        "domain": {"start": start, "end": end},
        "tolerance": tolerance,
        "declaredGridSamples": samples,
        "breakpointSamplesAdded": len(times) - len(set(declared)),
        "evaluatedSamples": len(times),
        "sampledNotContinuous": True,
        "eventsCompared": False,
        "channels": channel_reports,
        "overall": {
            "maxAbsDeviation": overall_max,
            "rmsDeviation": overall_rms.value(),
            "firstDivergence": (None if overall_first is None else
                                {"time": overall_first[0], "channel": overall_first[1]}),
            "channelsWithinTolerance": sum(item["withinTolerance"] for item in channel_reports),
            "channelsCompared": len(channel_reports),
            "withinTolerance": overall_first is None,
        },
    }


def argument_number(value):
    if not NUMBER.fullmatch(value):
        raise argparse.ArgumentTypeError("must be an invariant-culture number")
    try:
        result = float(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("must be a number") from error
    if not math.isfinite(result):
        raise argparse.ArgumentTypeError("must be finite")
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("left", type=Path)
    parser.add_argument("right", type=Path)
    parser.add_argument("--start", required=True, type=argument_number)
    parser.add_argument("--end", required=True, type=argument_number)
    parser.add_argument("--samples", required=True, type=int,
                        help="Regular grid points including both endpoints")
    parser.add_argument("--tolerance", required=True, type=argument_number)
    parser.add_argument("--output", type=Path, help="New JSON output path; must not exist")
    args = parser.parse_args(argv)
    try:
        if args.output is not None:
            if not args.output.parent.is_dir():
                raise ComparisonError("output parent directory does not exist")
        report = compare(parse_timeline(args.left), parse_timeline(args.right),
                         args.start, args.end, args.samples, args.tolerance)
        text = json.dumps(report, indent=2, sort_keys=True, allow_nan=False) + "\n"
        if args.output is None:
            sys.stdout.write(text)
        else:
            try:
                with args.output.open("x", encoding="utf-8") as stream:
                    stream.write(text)
            except FileExistsError as error:
                raise ComparisonError("output already exists: " + str(args.output)) from error
            print("Wrote sampled input comparison to " + str(args.output))
        return 0
    except (ComparisonError, OSError) as error:
        print("compare_inputs: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
