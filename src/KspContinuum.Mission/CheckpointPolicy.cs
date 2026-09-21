namespace KspContinuum.Mission
{
    public static class CheckpointPolicy
    {
        public static bool Qualifies(string body, int stage, int parts, double sma, double eccentricity, double periapsis, double positionError, double velocityError, bool topology, bool resources)
        {
            return body == "Minmus" && stage == 2 && parts == 17 && topology && resources &&
                SurveyPolicy.Finite(sma) && sma > 0 && SurveyPolicy.Finite(eccentricity) && eccentricity >= 0 && eccentricity < 1 &&
                SurveyPolicy.Finite(periapsis) && periapsis > 5000 && SurveyPolicy.Finite(positionError) && positionError >= 0 && positionError <= 1 &&
                SurveyPolicy.Finite(velocityError) && velocityError >= 0 && velocityError <= 0.01;
        }

        public static bool ResourceMatches(string name, double expected, double actual, double maximum)
        {
            if (!SurveyPolicy.Finite(expected) || !SurveyPolicy.Finite(actual) || !SurveyPolicy.Finite(maximum) || expected < 0 || actual < 0 || maximum < 0 || actual > maximum + 1e-5 || expected > maximum + 1e-5) return false;
            return name == "ElectricCharge" ? actual >= 0.9 * expected && System.Math.Abs(expected - actual) <= 0.1 * maximum :
                System.Math.Abs(expected - actual) <= 1e-5 + 1e-8 * expected;
        }
    }
}
