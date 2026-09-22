using System.Text.Json;
using KspContinuum;

namespace KspContinuum.Tools;

internal static class ProfileCompareCommand
{
    static readonly JsonSerializerOptions Json = new(Tooling.Json) { IncludeFields = true };

    public static int Run(string[] args)
    {
        var options = new Arguments(args);
        var stock = Read(options.Required("--stock")); var candidate = Read(options.Required("--candidate"));
        var expectation = new ProfileComparisonExpectation {
            substitutionId = options.Required("--substitution-id"), substitutionStatus = options.Required("--substitution-status"),
            stockRigidbodies = Integer(options.Required("--stock-rigidbodies")), stockJoints = Integer(options.Required("--stock-joints")),
            candidateRigidbodies = Integer(options.Required("--candidate-rigidbodies")), candidateJoints = Integer(options.Required("--candidate-joints")) };
        Console.WriteLine(JsonSerializer.Serialize(ProfileComparisonSummary.Compare(stock, candidate, expectation), Json));
        return 0;
    }

    static ProbeReport Read(string path)
    {
        var json = Tooling.ReadBounded(path);
        var report = JsonSerializer.Deserialize<ProbeReport>(json, Json);
        if (report == null || report.schema != "ksp-continuum-markers/v2") throw new ArgumentException("Expected a markers/v2 profile: " + path);
        return report;
    }

    static int Integer(string value)
    {
        if (!int.TryParse(value, out var result)) throw new ToolException("expected structural deltas must be integers");
        return result;
    }
}
