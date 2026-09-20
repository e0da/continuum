using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KspContinuum
{
    public static class TimelineCsv
    {
        public const string Schema = "ksp-continuum-input-timeline/v1";
        public const int MaxFileBytes = 16 * 1024 * 1024;
        public const int MaxLineLength = 1024;
        public const int MaxLines = 100000;
        public const int MaxTracks = 64;
        public const int MaxKeysPerTrack = 32768;
        public const int MaxTotalKeys = 65536;
        public const int MaxEvents = 8192;
        public const int MaxIdentifierLength = 64;

        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string Serialize(InputTimeline timeline)
        {
            if (timeline == null) throw new ArgumentException("A timeline is required.", "timeline");
            var output = new StringBuilder();
            Line(output, "schema," + Schema);
            Line(output, "duration," + Number(timeline.Duration));
            Line(output, "track,name,min,max");
            foreach (var track in timeline.Tracks)
                Line(output, "track," + track.Name + "," + Number(track.Min) + "," + Number(track.Max));
            Line(output, "key,track,time,value,mode,control1,control2");
            foreach (var track in timeline.Tracks)
            {
                foreach (var key in track.Keys)
                {
                    Line(output, "key," + track.Name + "," + Number(key.Time) + "," + Number(key.Value) + "," +
                        Mode(key.Mode) + "," + Number(key.Control1) + "," + Number(key.Control2));
                }
            }
            Line(output, "event,time,name,value");
            foreach (var item in timeline.Events)
                Line(output, "event," + Number(item.Time) + "," + item.Name + "," +
                    item.Value.ToString(CultureInfo.InvariantCulture));
            string text = output.ToString();
            ValidateTextBounds(text);
            return text;
        }

        public static InputTimeline Parse(string text)
        {
            if (text == null) throw new ArgumentException("Timeline text is required.", "text");
            ValidateTextBounds(text);
            var lines = Lines(text);
            int index = 0;
            Expect(lines, ref index, "schema," + Schema);

            string[] durationFields = Fields(Current(lines, index++), 2);
            if (durationFields[0] != "duration") throw Invalid("Expected the duration row.");
            double duration = ParseDouble(durationFields[1], "duration");

            Expect(lines, ref index, "track,name,min,max");
            var trackSpecs = new List<TrackSpec>();
            var keyLists = new Dictionary<string, List<TimelineKey>>(StringComparer.Ordinal);
            while (index < lines.Count && lines[index].StartsWith("track,", StringComparison.Ordinal))
            {
                string[] fields = Fields(lines[index++], 4);
                TimelineValidation.Identifier(fields[1], "Track name");
                if (keyLists.ContainsKey(fields[1])) throw Invalid("Duplicate track name '" + fields[1] + "'.");
                if (trackSpecs.Count == MaxTracks) throw Invalid("Timeline has too many tracks.");
                trackSpecs.Add(new TrackSpec(fields[1], ParseDouble(fields[2], "track minimum"),
                    ParseDouble(fields[3], "track maximum")));
                keyLists.Add(fields[1], new List<TimelineKey>());
            }
            if (trackSpecs.Count == 0) throw Invalid("Timeline requires at least one track row.");

            Expect(lines, ref index, "key,track,time,value,mode,control1,control2");
            int totalKeys = 0;
            while (index < lines.Count && lines[index].StartsWith("key,", StringComparison.Ordinal))
            {
                string[] fields = Fields(lines[index++], 7);
                List<TimelineKey> keys;
                if (!keyLists.TryGetValue(fields[1], out keys))
                    throw Invalid("Key references unknown track '" + fields[1] + "'.");
                if (keys.Count == MaxKeysPerTrack || totalKeys == MaxTotalKeys)
                    throw Invalid("Timeline has too many keys.");
                keys.Add(new TimelineKey(ParseDouble(fields[2], "key time"), ParseDouble(fields[3], "key value"),
                    ParseMode(fields[4]), ParseDouble(fields[5], "key control1"),
                    ParseDouble(fields[6], "key control2")));
                totalKeys++;
            }

            Expect(lines, ref index, "event,time,name,value");
            var events = new List<TimelineEvent>();
            while (index < lines.Count)
            {
                string[] fields = Fields(lines[index++], 4);
                if (fields[0] != "event") throw Invalid("Expected an event row.");
                if (events.Count == MaxEvents) throw Invalid("Timeline has too many events.");
                events.Add(new TimelineEvent(ParseDouble(fields[1], "event time"), fields[2],
                    ParseInt(fields[3], "event value")));
            }

            var tracks = new List<ScalarTrack>(trackSpecs.Count);
            foreach (var spec in trackSpecs)
                tracks.Add(new ScalarTrack(spec.Name, spec.Min, spec.Max, duration, keyLists[spec.Name]));
            return new InputTimeline(duration, tracks, events);
        }

        public static InputTimeline Parse(Stream stream)
        {
            if (stream == null) throw new ArgumentException("Timeline stream is required.", "stream");
            if (!stream.CanRead) throw new ArgumentException("Timeline stream must be readable.", "stream");
            var buffer = new byte[8192];
            using (var bytes = new MemoryStream())
            {
                while (true)
                {
                    int remaining = MaxFileBytes + 1 - (int)bytes.Length;
                    if (remaining <= 0) throw Invalid("Timeline exceeds the byte limit.");
                    int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                    if (read == 0) break;
                    bytes.Write(buffer, 0, read);
                }
                if (bytes.Length > MaxFileBytes) throw Invalid("Timeline exceeds the byte limit.");
                try
                {
                    return Parse(StrictUtf8.GetString(bytes.ToArray()));
                }
                catch (DecoderFallbackException exception)
                {
                    throw new ArgumentException("Timeline must be valid UTF-8.", "stream", exception);
                }
            }
        }

        static void Line(StringBuilder output, string value)
        {
            if (value.Length > MaxLineLength) throw Invalid("Timeline line exceeds the length limit.");
            output.Append(value).Append('\n');
        }

        static string Number(double value)
        {
            TimelineValidation.Finite(value, "Timeline number");
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        static string Mode(TimelineMode mode)
        {
            if (mode == TimelineMode.Step) return "step";
            if (mode == TimelineMode.Linear) return "linear";
            if (mode == TimelineMode.CubicBezier) return "cubic-bezier";
            throw Invalid("Unknown timeline interpolation mode.");
        }

        static TimelineMode ParseMode(string value)
        {
            if (value == "step") return TimelineMode.Step;
            if (value == "linear") return TimelineMode.Linear;
            if (value == "cubic-bezier") return TimelineMode.CubicBezier;
            throw Invalid("Unknown timeline interpolation mode '" + value + "'.");
        }

        static double ParseDouble(string value, string label)
        {
            RejectWhitespace(value, label);
            double result;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
                double.IsNaN(result) || double.IsInfinity(result))
                throw Invalid("Invalid " + label + ".");
            return result;
        }

        static int ParseInt(string value, string label)
        {
            RejectWhitespace(value, label);
            int result;
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
                throw Invalid("Invalid " + label + ".");
            return result;
        }

        static void RejectWhitespace(string value, string label)
        {
            for (int i = 0; i < value.Length; i++)
                if (char.IsWhiteSpace(value[i])) throw Invalid("Invalid whitespace in " + label + ".");
        }

        static string[] Fields(string line, int expected)
        {
            string[] fields = line.Split(',');
            if (fields.Length != expected) throw Invalid("Timeline row has the wrong number of fields.");
            return fields;
        }

        static void Expect(IList<string> lines, ref int index, string expected)
        {
            if (Current(lines, index) != expected) throw Invalid("Expected header '" + expected + "'.");
            index++;
        }

        static string Current(IList<string> lines, int index)
        {
            if (index >= lines.Count) throw Invalid("Timeline ended before all required headers.");
            return lines[index];
        }

        static List<string> Lines(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i != text.Length && text[i] != '\n') continue;
                int length = i - start;
                if (i < text.Length && length > 0 && text[i - 1] == '\r') length--;
                string line = text.Substring(start, length);
                if (line.IndexOf('\r') >= 0) throw Invalid("Timeline contains an invalid carriage return.");
                if (line.Length > MaxLineLength) throw Invalid("Timeline line exceeds the length limit.");
                bool finalEmpty = i == text.Length && line.Length == 0;
                if (!finalEmpty)
                {
                    if (line.Length == 0) throw Invalid("Timeline contains a blank line.");
                    if (lines.Count == MaxLines) throw Invalid("Timeline exceeds the line limit.");
                    lines.Add(line);
                }
                start = i + 1;
            }
            return lines;
        }

        static void ValidateTextBounds(string text)
        {
            try
            {
                if (StrictUtf8.GetByteCount(text) > MaxFileBytes) throw Invalid("Timeline exceeds the byte limit.");
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException("Timeline text contains invalid Unicode.", "text", exception);
            }
        }

        static ArgumentException Invalid(string message)
        {
            return new ArgumentException(message);
        }

        struct TrackSpec
        {
            public readonly string Name;
            public readonly double Min;
            public readonly double Max;
            public TrackSpec(string name, double min, double max)
            {
                Name = name;
                Min = min;
                Max = max;
            }
        }
    }
}
