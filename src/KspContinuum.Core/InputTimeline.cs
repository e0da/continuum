using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public enum TimelineMode
    {
        Step,
        Linear,
        CubicBezier
    }

    public sealed class TimelineKey
    {
        public TimelineKey(double time, double value, TimelineMode mode)
            : this(time, value, mode, value, value)
        {
        }

        public TimelineKey(double time, double value, TimelineMode mode, double control1, double control2)
        {
            TimelineValidation.NonnegativeFinite(time, "Key time");
            TimelineValidation.Finite(value, "Key value");
            TimelineValidation.Finite(control1, "Key control1");
            TimelineValidation.Finite(control2, "Key control2");
            if (mode != TimelineMode.Step && mode != TimelineMode.Linear && mode != TimelineMode.CubicBezier)
                throw new ArgumentException("Unknown timeline interpolation mode.", "mode");
            Time = time;
            Value = value;
            Mode = mode;
            Control1 = control1;
            Control2 = control2;
        }

        public double Time { get; private set; }
        public double Value { get; private set; }
        public TimelineMode Mode { get; private set; }
        public double Control1 { get; private set; }
        public double Control2 { get; private set; }
    }

    public sealed class ScalarTrack
    {
        readonly ReadOnlyCollection<TimelineKey> keys;

        public ScalarTrack(string name, double min, double max, double duration, IEnumerable<TimelineKey> keys)
        {
            TimelineValidation.Identifier(name, "Track name");
            TimelineValidation.Finite(min, "Track minimum");
            TimelineValidation.Finite(max, "Track maximum");
            TimelineValidation.PositiveFinite(duration, "Track duration");
            if (min > max) throw new ArgumentException("Track minimum must not exceed its maximum.", "min");
            if (keys == null) throw new ArgumentException("Track keys are required.", "keys");

            var copy = new List<TimelineKey>();
            foreach (var key in keys)
            {
                if (copy.Count == TimelineCsv.MaxKeysPerTrack)
                    throw new ArgumentException("Track has too many keys.", "keys");
                if (key == null) throw new ArgumentException("Track keys cannot contain null.", "keys");
                if (key.Value < min || key.Value > max || key.Control1 < min || key.Control1 > max ||
                    key.Control2 < min || key.Control2 > max)
                    throw new ArgumentException("Key values and controls must be within the track range.", "keys");
                if (copy.Count == 0)
                {
                    if (key.Time != 0) throw new ArgumentException("The first key must be at time zero.", "keys");
                }
                else if (key.Time <= copy[copy.Count - 1].Time)
                {
                    throw new ArgumentException("Key times must be strictly increasing.", "keys");
                }
                if (key.Time > duration) throw new ArgumentException("A key exceeds the track duration.", "keys");
                copy.Add(key);
            }
            if (copy.Count == 0) throw new ArgumentException("A track requires at least one key.", "keys");

            Name = name;
            Min = min;
            Max = max;
            Duration = duration;
            this.keys = new ReadOnlyCollection<TimelineKey>(copy);
        }

        public string Name { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }
        public double Duration { get; private set; }
        public IReadOnlyList<TimelineKey> Keys { get { return keys; } }

        public double Evaluate(double time)
        {
            TimelineValidation.Finite(time, "Evaluation time");
            if (time < 0 || time > Duration)
                throw new ArgumentException("Evaluation time must be within the track duration.", "time");

            int low = 0;
            int high = keys.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (keys[middle].Time <= time) low = middle + 1;
                else high = middle - 1;
            }
            int index = high;
            if (index == keys.Count - 1) return keys[index].Value;

            var start = keys[index];
            var end = keys[index + 1];
            if (start.Mode == TimelineMode.Step) return start.Value;
            double u = (time - start.Time) / (end.Time - start.Time);
            if (start.Mode == TimelineMode.Linear)
                return Interpolate(start.Value, end.Value, u);

            double first = Interpolate(start.Value, start.Control1, u);
            double second = Interpolate(start.Control1, start.Control2, u);
            double third = Interpolate(start.Control2, end.Value, u);
            return Interpolate(Interpolate(first, second, u), Interpolate(second, third, u), u);
        }

        static double Interpolate(double start, double end, double u)
        {
            if (start == end) return start;
            if ((start < 0) != (end < 0)) return (start * (1 - u)) + (end * u);
            return start + ((end - start) * u);
        }
    }

    public sealed class TimelineEvent
    {
        public TimelineEvent(double time, string name, int value)
        {
            TimelineValidation.NonnegativeFinite(time, "Event time");
            TimelineValidation.Identifier(name, "Event name");
            Time = time;
            Name = name;
            Value = value;
        }

        public double Time { get; private set; }
        public string Name { get; private set; }
        public int Value { get; private set; }
    }

    public sealed class InputTimeline
    {
        readonly ReadOnlyCollection<ScalarTrack> tracks;
        readonly ReadOnlyCollection<TimelineEvent> events;
        readonly Dictionary<string, ScalarTrack> tracksByName;

        public InputTimeline(double duration, IEnumerable<ScalarTrack> tracks, IEnumerable<TimelineEvent> events)
        {
            TimelineValidation.PositiveFinite(duration, "Timeline duration");
            if (tracks == null) throw new ArgumentException("Timeline tracks are required.", "tracks");
            if (events == null) throw new ArgumentException("Timeline events are required.", "events");

            var trackCopy = new List<ScalarTrack>();
            tracksByName = new Dictionary<string, ScalarTrack>(StringComparer.Ordinal);
            int totalKeys = 0;
            foreach (var track in tracks)
            {
                if (trackCopy.Count == TimelineCsv.MaxTracks)
                    throw new ArgumentException("Timeline has too many tracks.", "tracks");
                if (track == null) throw new ArgumentException("Timeline tracks cannot contain null.", "tracks");
                if (track.Duration != duration)
                    throw new ArgumentException("Every track duration must match the timeline duration.", "tracks");
                if (tracksByName.ContainsKey(track.Name))
                    throw new ArgumentException("Timeline track names must be unique.", "tracks");
                totalKeys += track.Keys.Count;
                if (totalKeys > TimelineCsv.MaxTotalKeys)
                    throw new ArgumentException("Timeline has too many keys.", "tracks");
                tracksByName.Add(track.Name, track);
                trackCopy.Add(track);
            }
            if (trackCopy.Count == 0) throw new ArgumentException("Timeline requires at least one track.", "tracks");

            var indexedEvents = new List<IndexedEvent>();
            int eventIndex = 0;
            foreach (var item in events)
            {
                if (indexedEvents.Count == TimelineCsv.MaxEvents)
                    throw new ArgumentException("Timeline has too many events.", "events");
                if (item == null) throw new ArgumentException("Timeline events cannot contain null.", "events");
                if (item.Time > duration) throw new ArgumentException("An event exceeds the timeline duration.", "events");
                indexedEvents.Add(new IndexedEvent(item, eventIndex++));
            }
            indexedEvents.Sort(CompareEvents);
            var eventCopy = new List<TimelineEvent>(indexedEvents.Count);
            foreach (var item in indexedEvents) eventCopy.Add(item.Value);

            Duration = duration;
            this.tracks = new ReadOnlyCollection<ScalarTrack>(trackCopy);
            this.events = new ReadOnlyCollection<TimelineEvent>(eventCopy);
        }

        public double Duration { get; private set; }
        public IReadOnlyList<ScalarTrack> Tracks { get { return tracks; } }
        public IReadOnlyList<TimelineEvent> Events { get { return events; } }

        public ScalarTrack GetTrack(string name)
        {
            TimelineValidation.Identifier(name, "Track name");
            ScalarTrack result;
            if (!tracksByName.TryGetValue(name, out result))
                throw new ArgumentException("Timeline does not contain track '" + name + "'.", "name");
            return result;
        }

        public TimelinePlaybackCursor CreateCursor()
        {
            return new TimelinePlaybackCursor(this);
        }

        static int CompareEvents(IndexedEvent left, IndexedEvent right)
        {
            int time = left.Value.Time.CompareTo(right.Value.Time);
            return time != 0 ? time : left.Index.CompareTo(right.Index);
        }

        struct IndexedEvent
        {
            public readonly TimelineEvent Value;
            public readonly int Index;
            public IndexedEvent(TimelineEvent value, int index)
            {
                Value = value;
                Index = index;
            }
        }
    }

    public sealed class TimelinePlaybackCursor
    {
        readonly InputTimeline timeline;
        int nextEvent;
        bool hasTime;
        double time;

        public TimelinePlaybackCursor(InputTimeline timeline)
        {
            if (timeline == null) throw new ArgumentException("A timeline is required.", "timeline");
            this.timeline = timeline;
        }

        public double? Time { get { return hasTime ? (double?)time : null; } }

        public IReadOnlyList<TimelineEvent> Advance(double newTime)
        {
            TimelineValidation.Finite(newTime, "Playback time");
            if (newTime < 0 || newTime > timeline.Duration)
                throw new ArgumentException("Playback time must be within the timeline duration.", "newTime");
            if (hasTime && newTime < time)
                throw new ArgumentException("A playback cursor cannot move backward.", "newTime");

            var delivered = new List<TimelineEvent>();
            while (nextEvent < timeline.Events.Count && timeline.Events[nextEvent].Time <= newTime)
                delivered.Add(timeline.Events[nextEvent++]);
            time = newTime;
            hasTime = true;
            return new ReadOnlyCollection<TimelineEvent>(delivered);
        }
    }

    static class TimelineValidation
    {
        public static void Finite(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException(label + " must be finite.");
        }

        public static void NonnegativeFinite(double value, string label)
        {
            Finite(value, label);
            if (value < 0) throw new ArgumentException(label + " must be nonnegative.");
        }

        public static void PositiveFinite(double value, string label)
        {
            Finite(value, label);
            if (value <= 0) throw new ArgumentException(label + " must be positive.");
        }

        public static void Identifier(string value, string label)
        {
            if (string.IsNullOrEmpty(value) || value.Length > TimelineCsv.MaxIdentifierLength)
                throw new ArgumentException(label + " must contain 1 to " + TimelineCsv.MaxIdentifierLength + " characters.");
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool safe = character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
                    character >= '0' && character <= '9' || character == '_' || character == '.' || character == '-';
                if (!safe) throw new ArgumentException(label + " contains an unsafe character.");
            }
        }
    }
}
