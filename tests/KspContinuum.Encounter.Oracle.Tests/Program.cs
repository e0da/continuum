using System;
using System.Linq;
using KspContinuum;

static class Program
{
    static int assertions;
    static readonly EncounterBudget Budget = new EncounterBudget(timeToleranceSeconds: 1e-5);
    static void Check(bool value, string reason)
    {
        assertions++;
        if (!value) throw new Exception("Independent encounter oracle: " + reason);
    }
    static EncounterMotion M(int id, Vec position, Vec velocity, double radius = 1, double horizon = 20,
        double error = 0, double velocityError = 0, double? acceleration = 0, double epoch = 0,
        Vec? nominalAcceleration = null)
    {
        return new EncounterMotion(id, 1, epoch, "independent-inertial", position, velocity,
            radius, horizon, horizon, error, velocityError, acceleration, nominalAcceleration);
    }
    static EncounterPlan Plan(EncounterMotion a, EncounterMotion b, double horizon = 20)
    {
        return EncounterPlanner.Plan(new[] { a, b }, horizon, Budget);
    }
    static void Contact(string name, EncounterMotion a, EncounterMotion b, double expected, double horizon = 20)
    {
        var plan = Plan(a, b, horizon);
        Check(plan.Status == EncounterPlanStatus.Complete, name + " completed");
        Check(plan.Candidates.Count == 1, name + " retained");
        var interval = plan.Candidates.Single();
        Check(interval.UpperSeconds-interval.LowerSeconds <= Budget.TimeToleranceSeconds, name + " requested interval width");
        Check(interval.LowerSeconds <= expected, name + " cannot advance beyond analytic contact");
        Check(expected - interval.LowerSeconds <= 2 * Budget.TimeToleranceSeconds, name + " conservative boundary is localized");
        Check(plan.Advances.All(x => x.SafeAdvanceSeconds <= expected), name + " all affected advances bounded");
        var reverse = Plan(b, a, horizon);
        Check(reverse.Candidates.Single().LowerSeconds == interval.LowerSeconds &&
              reverse.Candidates.Single().UpperSeconds == interval.UpperSeconds, name + " pair-order invariance");
    }
    static void Clear(string name, EncounterMotion a, EncounterMotion b, double horizon = 20)
    {
        var plan = Plan(a, b, horizon);
        Check(plan.Status == EncounterPlanStatus.Complete && plan.Candidates.Count == 0, name + " clear");
        Check(plan.Advances.All(x => x.SafeAdvanceSeconds == horizon), name + " full horizon");
    }
    static void CurvedContact(string name, EncounterMotion a, EncounterMotion b, double expected)
    {
        var plan = Plan(a, b);
        Check(plan.Status == EncounterPlanStatus.Complete, name + " completed");
        Check(plan.Candidates.Count == 1, name + " retained");
        var interval = plan.Candidates.Single();
        Check(interval.UpperSeconds-interval.LowerSeconds <= Budget.TimeToleranceSeconds, name + " requested bracket width");
        Check(interval.LowerSeconds <= expected, name + " no advance beyond analytic contact");
        // Possible brackets need not contain true contact: interval dependency can cause early refinement.
        Check(expected-interval.LowerSeconds <= .1, name + " bounded early refinement allowance");
        Check(plan.Advances.All(x => x.SafeAdvanceSeconds <= expected), name + " affected advances bounded");
        var reversed = Plan(b, a);
        Check(reversed.Candidates.Single().LowerSeconds == interval.LowerSeconds &&
              reversed.Candidates.Single().UpperSeconds == interval.UpperSeconds, name + " pair-order invariance");
    }
    static void CurvedCases()
    {
        // With s=t-10, relative position is (1.5s,s²,0). At s=±2 its norm is exactly 5.
        // Both endpoints and their chord lie at y=100; the initial tangent line also misses radius 5.
        var stationary = M(1, new Vec(), new Vec(), radius: 2);
        var parabola = M(2, new Vec(-15, 100, 0), new Vec(1.5, -20, 0), radius: 3,
            nominalAcceleration: new Vec(0, 2, 0));
        CurvedContact("curve defeats chord and linear prediction", stationary, parabola, 8);
        CurvedContact("acceleration from rest enters linear-disjoint bounds", M(1, new Vec(), new Vec()),
            M(2, new Vec(10, 0, 0), new Vec(), nominalAcceleration: new Vec(-4, 0, 0)), 2);
        CurvedContact("curved tangent", M(1, new Vec(), new Vec()),
            M(2, new Vec(-10, 102, 0), new Vec(1, -20, 0), nominalAcceleration: new Vec(0, 2, 0)), 10);
        // For t<8, |x|>2; for t>=8, y=t²/8>=8. The straight initial prediction falsely collides.
        Clear("curved path bends away from linear collision", M(1, new Vec(), new Vec()),
            M(2, new Vec(-10, 0, 0), new Vec(1, 0, 0), nominalAcceleration: new Vec(0, .25, 0)));
        Clear("curved near-tangent miss", M(1, new Vec(), new Vec()),
            M(2, new Vec(-10, 102.001, 0), new Vec(1, -20, 0), nominalAcceleration: new Vec(0, 2, 0)));

        double shift = Math.Pow(2, 40);
        CurvedContact("translated curved path", M(1, new Vec(shift, -shift, shift), new Vec(), radius: 2),
            M(2, new Vec(shift-15, -shift+100, shift), new Vec(1.5, -20, 0), radius: 3,
                nominalAcceleration: new Vec(0, 2, 0)), 8);
        CurvedContact("curvature in third coordinate", stationary,
            M(2, new Vec(-15, 0, 100), new Vec(1.5, 0, -20), radius: 3, nominalAcceleration: new Vec(0, 0, 2)), 8);
        Contact("common nominal acceleration cancels", M(1, new Vec(), new Vec(), nominalAcceleration: new Vec(1, 2, 3)),
            M(2, new Vec(12, 0, 0), new Vec(-1, 0, 0), nominalAcceleration: new Vec(1, 2, 3)), 10);

        // Nominal relative x=10-t². Residual acceleration magnitude <=4 permits x=10-3t².
        CurvedContact("nominal and residual acceleration remain distinct", M(1, new Vec(), new Vec()),
            M(2, new Vec(10, 0, 0), new Vec(), acceleration: 4, nominalAcceleration: new Vec(-2, 0, 0)), Math.Sqrt(8.0/3));
        var unknown = Plan(stationary, M(2, new Vec(-15, 100, 0), new Vec(1.5, -20, 0), radius: 3,
            acceleration: null, nominalAcceleration: new Vec(0, 2, 0)));
        Check(unknown.Status == EncounterPlanStatus.UnknownBounds && unknown.Advances.All(x => x.SafeAdvanceSeconds == 0),
            "nominal acceleration does not make an unknown residual bound known");
        var overflow = Plan(stationary, M(2, new Vec(10, 0, 0), new Vec(), nominalAcceleration: new Vec(double.MaxValue, 0, 0)));
        Check(overflow.Status == EncounterPlanStatus.NumericalUncertainty && overflow.Advances.All(x => x.SafeAdvanceSeconds == 0),
            "overflowing nominal trajectory cannot publish clear");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            bool rejected = false;
            try { M(1, new Vec(), new Vec(), nominalAcceleration: new Vec(0, invalid, 0)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "nonfinite nominal acceleration rejected");
        }
    }
    static void Main()
    {
        CurvedCases();
        Contact("shifted narrow crossing", M(1, new Vec(-25006.25, 0, 0), new Vec(5000, 0, 0)),
            M(2, new Vec(25006.25, 0, 0), new Vec(-5000, 0, 0)), 5.00105, 10);
        Contact("rotated opposing craft", M(1, new Vec(-15000, -20000, 0), new Vec(3000, 4000, 0)),
            M(2, new Vec(15000, 20000, 0), new Vec(-3000, -4000, 0)), 4.9998, 10);
        Contact("three dimensional crossing", M(1, new Vec(), new Vec()),
            M(2, new Vec(-10, 2, 2), new Vec(1, 0, 0), radius: 2), 9);
        Contact("initial separating overlap", M(1, new Vec(), new Vec()),
            M(2, new Vec(1.5, 0, 0), new Vec(10, 0, 0)), 0);
        Contact("initial separating touch", M(1, new Vec(), new Vec()),
            M(2, new Vec(2, 0, 0), new Vec(1, 0, 0)), 0);
        Contact("closed horizon endpoint", M(1, new Vec(), new Vec()),
            M(2, new Vec(12, 0, 0), new Vec(-1, 0, 0)), 10, 10);
        Contact("zero horizon overlap", M(1, new Vec(), new Vec()), M(2, new Vec(1, 0, 0), new Vec()), 0, 0);
        Clear("before horizon contact", M(1, new Vec(), new Vec()),
            M(2, new Vec(12, 0, 0), new Vec(-1, 0, 0)), 9.999);
        Clear("equal velocities", M(1, new Vec(), new Vec(7, -2, 1)),
            M(2, new Vec(3, 0, 0), new Vec(7, -2, 1)));
        Clear("moving apart", M(1, new Vec(), new Vec()), M(2, new Vec(3, 0, 0), new Vec(1, 0, 0)));
        Clear("near grazing miss", M(1, new Vec(), new Vec()), M(2, new Vec(-10, 2.000001, 0), new Vec(1, 0, 0)));
        Contact("tangent", M(1, new Vec(), new Vec()), M(2, new Vec(-10, 2, 0), new Vec(1, 0, 0)), 10);

        double translation = Math.Pow(2, 40);
        Contact("common large translation", M(1, new Vec(translation-25000, -translation, translation), new Vec(5000, 0, 0)),
            M(2, new Vec(translation+25000, -translation, translation), new Vec(-5000, 0, 0)), 4.9998, 10);
        Contact("common large epoch", M(1, new Vec(), new Vec(), epoch: translation),
            M(2, new Vec(12, 0, 0), new Vec(-1, 0, 0), epoch: translation), 10);

        Contact("acceleration uncertainty", M(1, new Vec(), new Vec(), acceleration: 4),
            M(2, new Vec(10, 0, 0), new Vec()), 2);
        Contact("velocity uncertainty", M(1, new Vec(), new Vec(), velocityError: 2),
            M(2, new Vec(10, 0, 0), new Vec()), 4);
        Contact("position uncertainty", M(1, new Vec(), new Vec(), error: 3),
            M(2, new Vec(10, 0, 0), new Vec(-1, 0, 0)), 5);
        Contact("combined uncertainty", M(1, new Vec(), new Vec(), error: 3, velocityError: 2, acceleration: 4),
            M(2, new Vec(10, 0, 0), new Vec()), (Math.Sqrt(11)-1)/2);

        // Integer geometry gives half-chord 1 without reusing the planner's interval arithmetic.
        for (int speed = 1; speed <= 16; speed++)
            Contact("analytic 3D family " + speed, M(1, new Vec(), new Vec()),
                M(2, new Vec(-10*speed, 2, 2), new Vec(speed, 0, 0), radius: 2), 10-1.0/speed);

        var cancellation = Plan(M(1, new Vec(), new Vec(), horizon: 1e12),
            M(2, new Vec(1e12, 0, 0), new Vec(-1, 0, 0), horizon: 1e12), 1e12);
        Check(cancellation.Advances.All(x => x.SafeAdvanceSeconds <= 999999999998d), "large separation cannot return naive quadratic's late result");
        Check(cancellation.Status != EncounterPlanStatus.Complete || cancellation.Candidates.Count == 1,
            "large separation is uncertain or retains interaction");
        Check(cancellation.Status != EncounterPlanStatus.Complete || cancellation.Candidates.All(x => x.UpperSeconds-x.LowerSeconds <= Budget.TimeToleranceSeconds),
            "complete localization must meet requested precision");
        var slow = Plan(M(1, new Vec(), new Vec(), horizon: 1e13),
            M(2, new Vec(10, 0, 0), new Vec(-1e-12, 0, 0), horizon: 1e13), 1e13);
        Check(slow.Advances.All(x => x.SafeAdvanceSeconds <= 8e12), "tiny speed is not zero over a long horizon");
        var overflow = Plan(M(1, new Vec(), new Vec(), radius: double.MaxValue), M(2, new Vec(10, 0, 0), new Vec()));
        Check(overflow.Status == EncounterPlanStatus.NumericalUncertainty && overflow.Advances.All(x => x.SafeAdvanceSeconds == 0),
            "overflow cannot publish clear advancement");
        var unknown = Plan(M(1, new Vec(), new Vec(), acceleration: null), M(2, new Vec(1e6, 0, 0), new Vec()));
        Check(unknown.Status == EncounterPlanStatus.UnknownBounds && unknown.Advances.All(x => x.SafeAdvanceSeconds == 0),
            "unknown acceleration is not zero");
        Console.WriteLine("Independent encounter oracle: " + assertions + " assertions passed.");
    }
}
