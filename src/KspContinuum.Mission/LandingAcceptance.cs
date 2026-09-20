namespace KspContinuum.Mission
{
    public sealed class LandingAcceptance
    {
        double since = double.NaN;
        double previous = double.NaN;

        public bool Observe(double time, bool qualifies)
        {
            if (double.IsNaN(time) || double.IsInfinity(time))
            {
                since = previous = double.NaN;
                return false;
            }
            if (!qualifies || (!double.IsNaN(previous) && time < previous)) since = double.NaN;
            previous = time;
            if (!qualifies) return false;
            if (double.IsNaN(since)) since = time;
            return time - since >= 30;
        }

        public static bool Qualifies(string body, bool landed, bool commandSurvives, double throttle, double speed, bool normalRate)
        {
            return body == "Minmus" && landed && commandSurvives && throttle == 0 && speed >= 0 && speed < 0.2 && normalRate;
        }
    }
}
