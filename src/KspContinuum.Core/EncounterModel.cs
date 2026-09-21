using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
namespace KspContinuum
{
    public enum EncounterPlanStatus { Complete, UnknownBounds, BudgetExhausted, NumericalUncertainty }
    public sealed class EncounterMotion
    {
        public int Id { get; private set; }
        public long Generation { get; private set; }
        public double Epoch { get; private set; }
        public string Frame { get; private set; }
        public Vec Position { get; private set; }
        public Vec Velocity { get; private set; }
        public double Radius { get; private set; }
        public double ValidForSeconds { get; private set; }
        public double LookaheadSeconds { get; private set; }
        public double PositionError { get; private set; }
        public double VelocityError { get; private set; }
        public double? AccelerationBound { get; private set; }
        public EncounterMotion(int id, long generation, double epoch, string frame, Vec position, Vec velocity, double radius,
            double validForSeconds, double lookaheadSeconds, double positionError = 0, double velocityError = 0, double? accelerationBound = null)
        {
            if(id<0||generation<0||string.IsNullOrEmpty(frame)||frame.Length>256) throw new ArgumentException("Invalid encounter identity.");
            AssemblyModel.Finite(epoch);
            Validate(position); Validate(velocity);
            Nonnegative(radius); Nonnegative(validForSeconds); Nonnegative(lookaheadSeconds);
            Nonnegative(positionError); Nonnegative(velocityError);
            if(accelerationBound.HasValue) Nonnegative(accelerationBound.Value);
            Id=id; Generation=generation; Epoch=epoch; Frame=frame; Position=position; Velocity=velocity; Radius=radius;
            ValidForSeconds=validForSeconds; LookaheadSeconds=lookaheadSeconds; PositionError=positionError;
            VelocityError=velocityError; AccelerationBound=accelerationBound;
        }
        static void Validate(Vec v) { AssemblyModel.Finite(v.X); AssemblyModel.Finite(v.Y); AssemblyModel.Finite(v.Z); }
        static void Nonnegative(double value) { AssemblyModel.Finite(value); if(value<0) throw new ArgumentException("Bounds must be nonnegative."); }
    }
    public sealed class EncounterBudget
    {
        public int MaxPairTests { get; private set; }
        public int MaxCandidates { get; private set; }
        public int MaxIntervalTests { get; private set; }
        public double TimeToleranceSeconds { get; private set; }
        public EncounterBudget(int maxPairTests=100000,int maxCandidates=10000,int maxIntervalTests=200000,double timeToleranceSeconds=.0001)
        {
            if(maxPairTests<0||maxPairTests>10000000||maxCandidates<0||maxCandidates>1000000||maxIntervalTests<0||maxIntervalTests>10000000)
                throw new ArgumentException("Encounter work budget is outside supported bounds.");
            AssemblyModel.Positive(timeToleranceSeconds);
            MaxPairTests=maxPairTests; MaxCandidates=maxCandidates; MaxIntervalTests=maxIntervalTests; TimeToleranceSeconds=timeToleranceSeconds; }
    }
    public sealed class EncounterCandidate
    {
        public int FirstId {get;internal set;} public int SecondId {get;internal set;}
        public long FirstGeneration {get;internal set;} public long SecondGeneration {get;internal set;}
        public double LowerSeconds {get;internal set;} public double UpperSeconds {get;internal set;}
    }
    public sealed class EncounterAdvance
    {
        public int ObjectId {get;internal set;} public int GroupId {get;internal set;} public double SafeAdvanceSeconds {get;internal set;}
    }
    public sealed class EncounterGroup
    {
        public int Id {get;internal set;} public IReadOnlyList<int> ObjectIds {get;internal set;} public double SafeAdvanceSeconds {get;internal set;}
    }
    public sealed class EncounterWork
    {
        public int SweepAxis {get;internal set;} = -1;
        public int PairTests {get;internal set;} public int BroadphaseCandidates {get;internal set;} public int IntervalTests {get;internal set;}
    }
    public sealed class EncounterPlan
    {
        public EncounterPlanStatus Status {get;internal set;}
        public string Detail {get;internal set;}
        public Guid SchedulerId {get;internal set;} public long Generation {get;internal set;}
        public double Epoch {get;internal set;} public string Frame {get;internal set;} public double HorizonSeconds {get;internal set;}
        public IReadOnlyList<EncounterCandidate> Candidates {get;internal set;}
        public IReadOnlyList<EncounterAdvance> Advances {get;internal set;}
        public IReadOnlyList<EncounterGroup> Groups {get;internal set;}
        public EncounterWork WorkUsed {get;internal set;}
    }
}
