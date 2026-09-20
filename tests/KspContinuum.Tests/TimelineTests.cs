using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KspContinuum;

public static class TimelineTests
{
    static int count;

    public static int Run()
    {
        count = 0;
        EvaluatesStepLinearAndBezierTracks();
        RejectsInvalidTimelineData();
        DefensivelyCopiesInputs();
        RoundTripsUnderCommaDecimalCulture();
        OrdersAndDeliversDuplicateEventsExactlyOnce();
        RejectsMalformedAndUnboundedText();
        return count;
    }

    static TimelineKey Key(double time, double value, TimelineMode mode)
    {
        return new TimelineKey(time, value, mode);
    }

    static ScalarTrack Track(string name, double duration, params TimelineKey[] keys)
    {
        return new ScalarTrack(name, 0, 1, duration, keys);
    }

    static InputTimeline Timeline(double duration, ScalarTrack[] tracks, params TimelineEvent[] events)
    {
        return new InputTimeline(duration, tracks, events);
    }

    static void EvaluatesStepLinearAndBezierTracks()
    {
        var linear = Track("throttle", 2, Key(0, 0, TimelineMode.Linear), Key(2, 1, TimelineMode.Step));
        Near(0, linear.Evaluate(0));
        Near(0.5, linear.Evaluate(1));
        Near(1, linear.Evaluate(2));

        var step = Track("gear", 2, Key(0, 0, TimelineMode.Step), Key(1, 1, TimelineMode.Step));
        Near(0, step.Evaluate(0.999));
        Near(1, step.Evaluate(1));
        Near(1, step.Evaluate(2));

        var bezier = Track("pitch", 4,
            new TimelineKey(0, 0, TimelineMode.CubicBezier, 0, 0),
            Key(2, 1, TimelineMode.Linear),
            Key(3, 0.25, TimelineMode.Step));
        Near(0, bezier.Evaluate(0));
        Near(0.125, bezier.Evaluate(1));
        Near(1, bezier.Evaluate(2));
        Near(0.25, bezier.Evaluate(3));
        Near(0.25, bezier.Evaluate(4));

        var extreme = new ScalarTrack("extreme", -double.MaxValue, double.MaxValue, 1, new[] {
            new TimelineKey(0, -double.MaxValue, TimelineMode.Linear),
            new TimelineKey(1, double.MaxValue, TimelineMode.Step)
        });
        Near(0, extreme.Evaluate(0.5));

        var positiveCubic = new ScalarTrack("positive", double.MaxValue, double.MaxValue, 1, new[] {
            new TimelineKey(0, double.MaxValue, TimelineMode.CubicBezier, double.MaxValue, double.MaxValue),
            new TimelineKey(1, double.MaxValue, TimelineMode.Step)
        });
        SameBits(double.MaxValue, positiveCubic.Evaluate(0.061));
        var negativeCubic = new ScalarTrack("negative", -double.MaxValue, -double.MaxValue, 1, new[] {
            new TimelineKey(0, -double.MaxValue, TimelineMode.CubicBezier, -double.MaxValue, -double.MaxValue),
            new TimelineKey(1, -double.MaxValue, TimelineMode.Step)
        });
        SameBits(-double.MaxValue, negativeCubic.Evaluate(0.061));
        var crossSignCubic = new ScalarTrack("cross", -double.MaxValue, double.MaxValue, 1, new[] {
            new TimelineKey(0, -double.MaxValue, TimelineMode.CubicBezier, double.MaxValue, -double.MaxValue),
            new TimelineKey(1, double.MaxValue, TimelineMode.Step)
        });
        FiniteInRange(crossSignCubic.Evaluate(0.061), -double.MaxValue, double.MaxValue);
        Near(0, crossSignCubic.Evaluate(0.5));

        Reject(() => linear.Evaluate(-0.001));
        Reject(() => linear.Evaluate(2.001));
        Reject(() => linear.Evaluate(double.NaN));
    }

    static void RejectsInvalidTimelineData()
    {
        Reject(() => new InputTimeline(0, new ScalarTrack[0], new TimelineEvent[0]));
        Reject(() => new InputTimeline(double.NaN, new ScalarTrack[0], new TimelineEvent[0]));
        Reject(() => new InputTimeline(double.PositiveInfinity, new ScalarTrack[0], new TimelineEvent[0]));
        Reject(() => new ScalarTrack("bad,name", 0, 1, 1, new[] { Key(0, 0, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("", 0, 1, 1, new[] { Key(0, 0, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", 1, 0, 1, new[] { Key(0, 0, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", double.NaN, 1, 1, new[] { Key(0, 0, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", 0, 1, 1, new TimelineKey[0]));
        Reject(() => new ScalarTrack("x", 0, 1, 1, new[] { Key(0.1, 0, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", 0, 1, 1, new[] {
            Key(0, 0, TimelineMode.Step), Key(0, 1, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", 0, 1, 1, new[] {
            Key(0, 0, TimelineMode.Step), Key(0.75, 1, TimelineMode.Step), Key(0.5, 1, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", 0, 1, 1, new[] {
            Key(0, 0, TimelineMode.Step), Key(1.01, 1, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", 0, 1, 1, new[] { Key(0, -0.01, TimelineMode.Step) }));
        Reject(() => new ScalarTrack("x", 0, 1, 1, new[] {
            new TimelineKey(0, 0, TimelineMode.CubicBezier, -0.01, 0.5) }));
        Reject(() => new TimelineKey(0, 0, (TimelineMode)999));
        Reject(() => new TimelineKey(double.NaN, 0, TimelineMode.Step));
        Reject(() => new TimelineKey(0, double.PositiveInfinity, TimelineMode.Step));

        var x = Track("x", 1, Key(0, 0, TimelineMode.Step));
        Reject(() => Timeline(2, new[] { x }));
        Reject(() => Timeline(1, new[] { x, Track("x", 1, Key(0, 1, TimelineMode.Step)) }));
        Reject(() => Timeline(1, new[] { x }, new TimelineEvent(1.01, "stage", 1)));
        Reject(() => new TimelineEvent(-0.01, "stage", 1));
        Reject(() => new TimelineEvent(0, "stage\nnow", 1));

        var tooManyTracks = new List<ScalarTrack>();
        for (int i = 0; i <= TimelineCsv.MaxTracks; i++)
            tooManyTracks.Add(Track("x" + i.ToString(CultureInfo.InvariantCulture), 1, Key(0, 0, TimelineMode.Step)));
        Reject(() => Timeline(1, tooManyTracks.ToArray()));
    }

    static void DefensivelyCopiesInputs()
    {
        var keys = new[] { Key(0, 0, TimelineMode.Linear), Key(1, 1, TimelineMode.Step) };
        var track = new ScalarTrack("x", 0, 1, 1, keys);
        keys[0] = Key(0, 1, TimelineMode.Step);
        Near(0.5, track.Evaluate(0.5));

        var tracks = new[] { track };
        var events = new[] { new TimelineEvent(0.5, "marker", 7) };
        var timeline = Timeline(1, tracks, events);
        tracks[0] = Track("other", 1, Key(0, 0, TimelineMode.Step));
        events[0] = new TimelineEvent(0.25, "changed", 9);
        Equal("x", timeline.Tracks[0].Name);
        Equal("marker", timeline.Events[0].Name);

        var mutableKeys = track.Keys as IList<TimelineKey>;
        if (mutableKeys == null || !mutableKeys.IsReadOnly) Fail("Track keys are mutable");
        Count();
        var mutableTracks = timeline.Tracks as IList<ScalarTrack>;
        if (mutableTracks == null || !mutableTracks.IsReadOnly) Fail("Timeline tracks are mutable");
        Count();
        var mutableEvents = timeline.Events as IList<TimelineEvent>;
        if (mutableEvents == null || !mutableEvents.IsReadOnly) Fail("Timeline events are mutable");
        Count();
    }

    static void RoundTripsUnderCommaDecimalCulture()
    {
        const double duration = 1.2345678901234567;
        var original = Timeline(duration, new[] {
            new ScalarTrack("plugin14float.pitch", -1, 1, duration, new[] {
                new TimelineKey(0, -0.12345678901234566, TimelineMode.CubicBezier,
                    0.23456789012345678, -0.34567890123456789),
                new TimelineKey(duration, 0.98765432109876539, TimelineMode.Step)
            })
        }, new TimelineEvent(0, "loaded", int.MinValue), new TimelineEvent(duration, "stage", int.MaxValue));

        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string text = TimelineCsv.Serialize(original);
            if (text.IndexOf("1,234", StringComparison.Ordinal) >= 0) Fail("CSV used current decimal culture");
            Count();
            var parsed = TimelineCsv.Parse(text);
            SameBits(original.Duration, parsed.Duration);
            SameBits(original.Tracks[0].Keys[0].Value, parsed.Tracks[0].Keys[0].Value);
            SameBits(original.Tracks[0].Keys[0].Control1, parsed.Tracks[0].Keys[0].Control1);
            SameBits(original.Tracks[0].Keys[0].Control2, parsed.Tracks[0].Keys[0].Control2);
            Equal(text, TimelineCsv.Serialize(parsed));
            Near(original.Tracks[0].Evaluate(0.5), parsed.Tracks[0].Evaluate(0.5));

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                Equal(text, TimelineCsv.Serialize(TimelineCsv.Parse(stream)));
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    static void OrdersAndDeliversDuplicateEventsExactlyOnce()
    {
        var track = Track("x", 3, Key(0, 0, TimelineMode.Step));
        var timeline = Timeline(3, new[] { track },
            new TimelineEvent(2, "late", 4),
            new TimelineEvent(0, "start", 1),
            new TimelineEvent(1, "same", 2),
            new TimelineEvent(1, "same", 2),
            new TimelineEvent(1, "other", 3));

        Near(0, timeline.Events[0].Time);
        Equal("same", timeline.Events[1].Name);
        Equal("same", timeline.Events[2].Name);
        Equal("other", timeline.Events[3].Name);
        Equal("late", timeline.Events[4].Name);

        var cursor = timeline.CreateCursor();
        if (cursor.Time.HasValue) Fail("New cursor already has a time");
        Count();
        var atZero = cursor.Advance(0);
        Equal(1, atZero.Count);
        Equal("start", atZero[0].Name);
        Equal(0, cursor.Advance(0).Count);
        var atOne = cursor.Advance(1);
        Equal(3, atOne.Count);
        Equal("same", atOne[0].Name);
        Equal("same", atOne[1].Name);
        Equal("other", atOne[2].Name);
        Equal(0, cursor.Advance(1.5).Count);
        var atEnd = cursor.Advance(3);
        Equal(1, atEnd.Count);
        Equal("late", atEnd[0].Name);
        Equal(0, cursor.Advance(3).Count);
        Reject(() => cursor.Advance(2.999));
        Reject(() => timeline.CreateCursor().Advance(3.001));
    }

    static void RejectsMalformedAndUnboundedText()
    {
        Reject(() => TimelineCsv.Parse((string)null));
        Reject(() => TimelineCsv.Parse("schema,ksp-continuum-input-timeline/v2\n"));
        Reject(() => TimelineCsv.Parse("schema,ksp-continuum-input-timeline/v1\nduration,1\nwrong,name,min,max\n"));
        Reject(() => TimelineCsv.Parse("schema,ksp-continuum-input-timeline/v1\nduration,1\ntrack,name,min,max\n" +
            "track,x,0,1\nkey,track,time,value,mode,control1,control2\nkey,x,0,0,mystery,0,0\n" +
            "event,time,name,value\n"));
        Reject(() => TimelineCsv.Parse("schema,ksp-continuum-input-timeline/v1\nduration,1\ntrack,name,min,max\n" +
            "track,x,0,1\nkey,track,time,value,mode,control1,control2\nkey,x,0,0,step,0,0,extra\n" +
            "event,time,name,value\n"));
        Reject(() => TimelineCsv.Parse(new string('a', TimelineCsv.MaxLineLength + 1)));
        Reject(() => TimelineCsv.Parse(new string('a', TimelineCsv.MaxFileBytes + 1)));

        var minimal = Timeline(1, new[] { Track("x", 1, Key(0, 0, TimelineMode.Step)) });
        string text = TimelineCsv.Serialize(minimal);
        Reject(() => TimelineCsv.Parse(text.Substring(0, text.Length - 1) + "\r"));
        Equal(text, TimelineCsv.Serialize(TimelineCsv.Parse(text.Replace("\n", "\r\n"))));
        Equal("schema,ksp-continuum-input-timeline/v1", FirstLine(text));
        if (!text.EndsWith("\n", StringComparison.Ordinal)) Fail("CSV lacks final newline");
        Count();
    }

    static string FirstLine(string value)
    {
        int end = value.IndexOf('\n');
        return end < 0 ? value : value.Substring(0, end);
    }

    static void SameBits(double expected, double actual)
    {
        if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual))
            Fail("Double did not round trip: " + expected.ToString("R", CultureInfo.InvariantCulture));
        Count();
    }

    static void FiniteInRange(double actual, double min, double max)
    {
        if (double.IsNaN(actual) || double.IsInfinity(actual) || actual < min || actual > max)
            Fail("Expected finite in-range value, got " + actual.ToString("R", CultureInfo.InvariantCulture));
        Count();
    }

    static void Near(double expected, double actual)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > 1e-12 * Math.Max(1, Math.Abs(expected)))
            Fail("Expected " + expected.ToString("R", CultureInfo.InvariantCulture) +
                ", got " + actual.ToString("R", CultureInfo.InvariantCulture));
        Count();
    }

    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Fail("Expected " + expected + ", got " + actual);
        Count();
    }

    static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            Count();
            return;
        }
        Fail("Invalid input accepted");
    }

    static void Count()
    {
        count++;
    }

    static void Fail(string message)
    {
        throw new Exception(message);
    }
}
