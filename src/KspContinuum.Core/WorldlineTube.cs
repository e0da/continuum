using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public enum WorldlineTubeStatus { Clear, Candidate, Unknown, Expired }

    public struct TubeOrientation
    {
        public readonly double X, Y, Z, W;
        public TubeOrientation(double x, double y, double z, double w)
        {
            AssemblyModel.Finite(x); AssemblyModel.Finite(y); AssemblyModel.Finite(z); AssemblyModel.Finite(w);
            double norm2 = x*x + y*y + z*z + w*w;
            if (norm2 < .999999 || norm2 > 1.000001) throw new ArgumentException("Tube orientation must be a unit quaternion.");
            X=x; Y=y; Z=z; W=w;
        }
    }

    // A tube is a bounded claim about where one physical support may exist, not a propagated truth state.
    public sealed class WorldlineTube
    {
        public int Id { get; private set; }
        public long Revision { get; private set; }
        public double Epoch { get; private set; }
        public string Frame { get; private set; }
        public Vec Position { get; private set; }
        public Vec Velocity { get; private set; }
        public Vec NominalAcceleration { get; private set; }
        public Vec SupportHalfExtents { get; private set; }
        public TubeOrientation Orientation { get; private set; }
        public double OrientationUncertaintyRadians { get; private set; }
        public double PositionUncertainty { get; private set; }
        public double VelocityUncertainty { get; private set; }
        public double? AccelerationUncertainty { get; private set; }
        public double ValidForSeconds { get; private set; }
        public bool ValidityPredicatesSatisfied { get; private set; }

        public WorldlineTube(int id,long revision,double epoch,string frame,Vec position,Vec velocity,Vec nominalAcceleration,
            Vec supportHalfExtents,TubeOrientation orientation,double orientationUncertaintyRadians,double positionUncertainty,
            double velocityUncertainty,double? accelerationUncertainty,double validForSeconds,bool validityPredicatesSatisfied=true)
        {
            if(id<0||revision<0||string.IsNullOrEmpty(frame)||frame.Length>256) throw new ArgumentException("Invalid tube identity.");
            Finite(epoch); Vector(position); Vector(velocity); Vector(nominalAcceleration); Vector(supportHalfExtents);
            Nonnegative(supportHalfExtents.X); Nonnegative(supportHalfExtents.Y); Nonnegative(supportHalfExtents.Z);
            Nonnegative(orientationUncertaintyRadians); if(orientationUncertaintyRadians>Math.PI) throw new ArgumentException("Orientation uncertainty exceeds pi.");
            Nonnegative(positionUncertainty); Nonnegative(velocityUncertainty); Nonnegative(validForSeconds);
            if(accelerationUncertainty.HasValue) Nonnegative(accelerationUncertainty.Value);
            Id=id;Revision=revision;Epoch=epoch;Frame=frame;Position=position;Velocity=velocity;NominalAcceleration=nominalAcceleration;
            SupportHalfExtents=supportHalfExtents;Orientation=orientation;OrientationUncertaintyRadians=orientationUncertaintyRadians;
            PositionUncertainty=positionUncertainty;VelocityUncertainty=velocityUncertainty;AccelerationUncertainty=accelerationUncertainty;
            ValidForSeconds=validForSeconds;ValidityPredicatesSatisfied=validityPredicatesSatisfied;
        }
        internal double SupportRadius { get { return Math.Sqrt(SupportHalfExtents.X*SupportHalfExtents.X+SupportHalfExtents.Y*SupportHalfExtents.Y+SupportHalfExtents.Z*SupportHalfExtents.Z); } }
        static void Vector(Vec value) { Finite(value.X);Finite(value.Y);Finite(value.Z); }
        static void Finite(double value) { AssemblyModel.Finite(value); }
        static void Nonnegative(double value) { Finite(value);if(value<0)throw new ArgumentException("Tube bounds must be nonnegative."); }
    }

    public sealed class WorldlineTubeScreen
    {
        public WorldlineTubeStatus Status { get; internal set; }
        public string Detail { get; internal set; }
        public double HorizonSeconds { get; internal set; }
        public int PairTests { get; internal set; }
        public int IntervalTests { get; internal set; }
        public double? CandidateLowerSeconds { get; internal set; }
        public double? CandidateUpperSeconds { get; internal set; }
    }

    public static class WorldlineTubeScreener
    {
        public static WorldlineTubeScreen Screen(WorldlineTube first,WorldlineTube second,double requestedSeconds,EncounterBudget budget)
        {
            if(first==null||second==null||budget==null||first.Id==second.Id) throw new ArgumentException("Two distinct tubes and a budget are required.");
            AssemblyModel.Finite(requestedSeconds);if(requestedSeconds<0)throw new ArgumentException("Negative horizon.");
            if(first.Epoch!=second.Epoch||first.Frame!=second.Frame)
                return Result(WorldlineTubeStatus.Unknown,"A common physical epoch and frame are required.",requestedSeconds);
            if(!first.ValidityPredicatesSatisfied||!second.ValidityPredicatesSatisfied||!first.AccelerationUncertainty.HasValue||!second.AccelerationUncertainty.HasValue)
                return Result(WorldlineTubeStatus.Unknown,"A validity predicate or approximation bound is unknown.",requestedSeconds);
            if(requestedSeconds>first.ValidForSeconds||requestedSeconds>second.ValidForSeconds)
                return Result(WorldlineTubeStatus.Expired,"The requested horizon exceeds a tube expiry.",requestedSeconds);
            var motions=new [] { Motion(first,requestedSeconds),Motion(second,requestedSeconds) };
            EncounterPlan plan=EncounterPlanner.Plan(motions,requestedSeconds,budget);
            var answer=Result(plan.Status==EncounterPlanStatus.Complete?(plan.Candidates.Count==0?WorldlineTubeStatus.Clear:WorldlineTubeStatus.Candidate):WorldlineTubeStatus.Unknown,
                plan.Detail,requestedSeconds);
            answer.PairTests=plan.WorkUsed.PairTests;answer.IntervalTests=plan.WorkUsed.IntervalTests;
            if(plan.Candidates.Count!=0){answer.CandidateLowerSeconds=plan.Candidates[0].LowerSeconds;answer.CandidateUpperSeconds=plan.Candidates[0].UpperSeconds;}
            return answer;
        }
        static EncounterMotion Motion(WorldlineTube tube,double horizon)
        {
            return new EncounterMotion(tube.Id,tube.Revision,tube.Epoch,tube.Frame,tube.Position,tube.Velocity,tube.SupportRadius,
                tube.ValidForSeconds,horizon,tube.PositionUncertainty,tube.VelocityUncertainty,tube.AccelerationUncertainty,tube.NominalAcceleration);
        }
        static WorldlineTubeScreen Result(WorldlineTubeStatus status,string detail,double horizon)
        { return new WorldlineTubeScreen {Status=status,Detail=detail,HorizonSeconds=horizon}; }
    }
}
