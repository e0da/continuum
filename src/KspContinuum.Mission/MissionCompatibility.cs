namespace KspContinuum.Mission
{
    public static class MissionCompatibility
    {
        public static bool ShouldExit(bool batchMode, bool explicitExit) { return batchMode || explicitExit; }

        public static bool IsSupported(string assemblyVersion, string fileVersion)
        {
            return assemblyVersion == "2.15.0.0" && fileVersion == "2.15.3.0";
        }
    }
}
