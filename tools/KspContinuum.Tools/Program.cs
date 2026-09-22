namespace KspContinuum.Tools;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, Func<string[], int>> Commands =
        new Dictionary<string, Func<string[], int>>(StringComparer.Ordinal)
        {
            ["aero-compare"] = AeroCompareCommand.Run,
            ["chronicle"] = ChronicleCommand.Run,
            ["chronicle-index"] = ChronicleIndexCommand.Run,
            ["compare-inputs"] = CompareInputsCommand.Run,
            ["lifecycle-report"] = LifecycleReportCommand.Run,
            ["package"] = PackageCommand.Run,
            ["qualification-report"] = QualificationReportCommand.Run,
            ["shadow-report"] = ShadowReportCommand.Run,
            ["space-program"] = SpaceProgramCommand.Run,
            ["telemetry-player"] = TelemetryPlayerCommand.Run,
        };

    public static int Main(string[] args)
    {
        if (args.Length == 0 || !Commands.TryGetValue(args[0], out var command))
        {
            Console.Error.WriteLine("usage: continuum-tools <command> [options]");
            Console.Error.WriteLine("commands: " + string.Join(", ", Commands.Keys));
            return 2;
        }

        try { return command(args[1..]); }
        catch (ToolException error)
        {
            Console.Error.WriteLine(args[0] + ": " + error.Message);
            return 1;
        }
        catch (IOException error)
        {
            Console.Error.WriteLine(args[0] + ": " + error.Message);
            return 1;
        }
        catch (UnauthorizedAccessException error)
        {
            Console.Error.WriteLine(args[0] + ": " + error.Message);
            return 1;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(args[0] + ": malformed input: " + error.Message);
            return 1;
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine(args[0] + ": malformed input: " + error.Message);
            return 1;
        }
    }
}
