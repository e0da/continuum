using System;

namespace KspContinuum.Mission
{
    public sealed class MissionTermination
    {
        public string Status { get; private set; }
        public bool TryBegin(string status)
        {
            if (status != "passed" && status != "failed" && status != "interrupted") throw new ArgumentException("Invalid mission outcome.");
            if (Status != null) return false;
            Status = status; return true;
        }
        public void FailFinalization()
        {
            if (Status == "passed") Status = "failed";
        }
    }
}
