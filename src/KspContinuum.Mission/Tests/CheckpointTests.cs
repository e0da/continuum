using System;
using System.IO;
using System.Security.Cryptography;
using KspContinuum.Mission;

static class CheckpointTests
{
    static string Digest(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    internal static void Run(Action<bool> check)
    {
        check(CheckpointPolicy.Qualifies("Minmus", 2, 17, 87000, .001, 26000, .1, .001, true, true));
        check(!CheckpointPolicy.Qualifies("Kerbin", 2, 17, 87000, .001, 26000, .1, .001, true, true));
        check(!CheckpointPolicy.Qualifies("Minmus", 2, 17, double.NaN, .001, 26000, .1, .001, true, true));
        check(!CheckpointPolicy.Qualifies("Minmus", 2, 17, 87000, .001, 4900, .1, .001, true, true));
        check(!CheckpointPolicy.Qualifies("Minmus", 2, 17, 87000, .001, 26000, 2, .001, true, true));
        check(!CheckpointPolicy.Qualifies("Minmus", 2, 17, 87000, .001, 26000, .1, .001, false, true));
        check(!CheckpointPolicy.Qualifies("Minmus", 2, 17, 87000, .001, 26000, .1, .001, true, false));
        check(CheckpointPolicy.ResourceMatches("LiquidFuel", 500, 500, 720));
        check(!CheckpointPolicy.ResourceMatches("LiquidFuel", 500, 499, 720));
        check(CheckpointPolicy.ResourceMatches("ElectricCharge", 150, 140, 150));
        check(!CheckpointPolicy.ResourceMatches("ElectricCharge", 150, double.NaN, 150));
        check(!CheckpointPolicy.ResourceMatches("ElectricCharge", 150, 0, 150));
        check(!CheckpointPolicy.ResourceMatches("ElectricCharge", 150, 134, 150));
        check(!CheckpointPolicy.ResourceMatches("ElectricCharge", 50, 80, 150));
        string root = Path.Combine(Path.GetTempPath(), "continuum-checkpoint-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string save = "CSP-0002-A002-source", checkpoint = "minmus-orbit-" + new string('a', 32);
            string sourceFolder = Path.Combine(root, "saves", save);
            string receiptFolder = Path.Combine(root, "GameData", "KspContinuum", "PluginData", "mission-source");
            Directory.CreateDirectory(sourceFolder); Directory.CreateDirectory(receiptFolder);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("GAME\n{\n}\n");
            string sourcePath = Path.Combine(sourceFolder, checkpoint + ".sfs");
            File.WriteAllBytes(sourcePath, bytes);
            File.WriteAllText(Path.Combine(receiptFolder, "mission.txt"), "save=" + save + "\nmissionId=CSP-0002\nattemptId=CSP-0002-A002\nvehicleDesignId=CV-0001-R01\n");
            File.WriteAllText(Path.Combine(receiptFolder, "milestones.csv"), "123,Capture,saves/" + save + "/" + checkpoint + ".sfs\n");
            CheckpointSource source = CheckpointSource.Inspect(root + Path.DirectorySeparatorChar, save, checkpoint, Digest(bytes));
            check(source.ParentAttemptId == "CSP-0002-A002" && source.Checkpoint == checkpoint && source.UniversalTime == 123);
            string destination = Path.Combine(root, "saves", "new-attempt");
            source.CopyTo(destination);
            check(Digest(File.ReadAllBytes(Path.Combine(destination, "checkpoint-source.sfs"))) == Digest(bytes));
            check(Digest(File.ReadAllBytes(sourcePath)) == Digest(bytes));
            check(Reject(() => source.CopyTo(destination)));
            check(Reject(() => CheckpointSource.Inspect(root, "../outside", checkpoint, Digest(bytes))));
            check(Reject(() => CheckpointSource.Inspect(root, save, "../outside", Digest(bytes))));
            check(Reject(() => CheckpointSource.Inspect(root, save, checkpoint, new string('0', 64))));
            File.WriteAllText(Path.Combine(receiptFolder, "milestones.csv"), "123,Landing,saves/" + save + "/" + checkpoint + ".sfs\n");
            check(Reject(() => CheckpointSource.Inspect(root, save, checkpoint, Digest(bytes))));
            File.WriteAllText(sourcePath, "changed");
            check(Reject(() => source.VerifyUnchanged()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
