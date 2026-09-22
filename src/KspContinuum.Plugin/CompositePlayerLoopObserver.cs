using System;

namespace KspContinuum
{
    internal sealed class CompositePlayerLoopObserver : IPlayerLoopBracketObserver
    {
        readonly IPlayerLoopBracketObserver[] observers;
        public CompositePlayerLoopObserver(params IPlayerLoopBracketObserver[] observers)
        { this.observers = observers ?? throw new ArgumentNullException("observers"); }
        public void Before(string scope, int frame, double fixedTimeSeconds)
        { foreach (IPlayerLoopBracketObserver observer in observers) if (observer != null) observer.Before(scope, frame, fixedTimeSeconds); }
        public void After(string scope, int frame, double fixedTimeSeconds)
        { foreach (IPlayerLoopBracketObserver observer in observers) if (observer != null) observer.After(scope, frame, fixedTimeSeconds); }
        public void Fault(string scope, Exception error)
        { foreach (IPlayerLoopBracketObserver observer in observers) if (observer != null) observer.Fault(scope, error); }
    }
}
