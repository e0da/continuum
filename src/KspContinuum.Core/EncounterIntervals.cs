using System;
namespace KspContinuum
{
    // Outward one-ULP padding assumes ordinary IEEE-754 binary64 operations. This is not a certified interval package.
    internal struct EncounterInterval
    {
        internal readonly double Lo, Hi;
        internal EncounterInterval(double lo,double hi)
        {
            if(!Finite(lo)||!Finite(hi)||lo>hi) throw new ArithmeticException("Unrepresentable encounter interval.");
            Lo=lo; Hi=hi;
        }
        internal static bool Finite(double x) { return !double.IsNaN(x)&&!double.IsInfinity(x); }
        internal static EncounterInterval Point(double x) { return new EncounterInterval(x,x); }
        internal static double Up(double x)
        {
            if(!Finite(x)) throw new ArithmeticException("Nonfinite encounter arithmetic.");
            if(x==0) return double.Epsilon;
            long bits=BitConverter.DoubleToInt64Bits(x);
            double result=BitConverter.Int64BitsToDouble(x>0?bits+1:bits-1);
            if(!Finite(result)) throw new ArithmeticException("Encounter bound overflow.");
            return result;
        }
        internal static double Down(double x) { return -Up(-x); }
        internal static EncounterInterval Add(EncounterInterval a,EncounterInterval b)
        { return new EncounterInterval(Down(a.Lo+b.Lo),Up(a.Hi+b.Hi)); }
        internal static EncounterInterval Subtract(EncounterInterval a,EncounterInterval b)
        { return new EncounterInterval(Down(a.Lo-b.Hi),Up(a.Hi-b.Lo)); }
        internal static EncounterInterval Multiply(EncounterInterval a,EncounterInterval b)
        {
            double x=a.Lo*b.Lo,y=a.Lo*b.Hi,z=a.Hi*b.Lo,w=a.Hi*b.Hi;
            if(!Finite(x)||!Finite(y)||!Finite(z)||!Finite(w)) throw new ArithmeticException("Encounter product overflow.");
            return new EncounterInterval(Down(Math.Min(Math.Min(x,y),Math.Min(z,w))),Up(Math.Max(Math.Max(x,y),Math.Max(z,w))));
        }
        internal static EncounterInterval Square(EncounterInterval x)
        {
            double lo=x.Lo<=0&&x.Hi>=0?0:Math.Min(x.Lo*x.Lo,x.Hi*x.Hi),hi=Math.Max(x.Lo*x.Lo,x.Hi*x.Hi);
            return new EncounterInterval(Math.Max(0,Down(lo)),Up(hi));
        }
    }
}
