using System;
using KspContinuum;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message) { checks++; try { action(); } catch (InvalidOperationException) { return; } throw new Exception(message); }
    static StructuralTrace Oscillator(int count)
    {
        const double a = 1.96, b = -0.97;
        var samples = new StructuralTraceSample[count];
        double previous = 0, current = 1;
        for (int i = 0; i < count; i++)
        {
            samples[i] = new StructuralTraceSample { physicsEpoch = i + 1, relativeDisplacement = current,
                relativeVelocity = i == 0 ? 0 : (current - previous) / .02 };
            double next = a * current + b * previous; previous = current; current = next;
        }
        return new StructuralTrace { evidence = "portable-helper-fixture", topology = "two-bodies/one-joint", stepSeconds = .02, samples = samples };
    }
    static int Main()
    {
        var fit = StructuralResponse.FitAndPredict(Oscillator(120), 60);
        Check(Math.Abs(fit.recurrenceCurrent - 1.96) < 1e-12, "current coefficient");
        Check(Math.Abs(fit.recurrencePrevious + .97) < 1e-12, "previous coefficient");
        Check(fit.heldOutSamples == 60 && fit.normalizedRms < 1e-10, "held-out recursive prediction");
        var changed = Oscillator(120);
        for (int i = 60; i < changed.samples.Length; i++) changed.samples[i].relativeDisplacement *= 1.2;
        Check(StructuralResponse.FitAndPredict(changed, 60).normalizedRms > .05, "held-out change remains visible");
        var duplicate = Oscillator(10); duplicate.samples[5].physicsEpoch = duplicate.samples[4].physicsEpoch;
        Reject(() => StructuralResponse.Validate(duplicate), "duplicate epoch accepted");
        var skipped = Oscillator(10); skipped.samples[5].physicsEpoch++;
        for (int i = 6; i < skipped.samples.Length; i++) skipped.samples[i].physicsEpoch++;
        Reject(() => StructuralResponse.Validate(skipped), "skipped epoch accepted");
        var nan = Oscillator(10); nan.samples[3].relativeVelocity = double.NaN;
        Reject(() => StructuralResponse.Validate(nan), "nonfinite trace accepted");
        var flat = Oscillator(10); foreach (var sample in flat.samples) sample.relativeDisplacement = 0;
        Reject(() => StructuralResponse.FitAndPredict(flat, 5), "unidentifiable motion accepted");
        Console.WriteLine("structural response checks: " + checks); return 0;
    }
}
