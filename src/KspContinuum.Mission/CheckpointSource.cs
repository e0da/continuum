using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KspContinuum.Mission
{
    public sealed class CheckpointSource
    {
        readonly byte[] bytes;
        readonly string path;
        public string ParentAttemptId { get; private set; }
        public string Checkpoint { get; private set; }
        public string Sha256 { get; private set; }
        public double UniversalTime { get; private set; }

        CheckpointSource(string path, byte[] bytes) { this.path = path; this.bytes = bytes; }

        public static CheckpointSource Inspect(string root, string save, string checkpoint, string hash)
        {
            if (save == null || !Regex.IsMatch(save, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z") || save.Contains("..") ||
                checkpoint == null || !Regex.IsMatch(checkpoint, "\\Aminmus-orbit-[0-9a-f]{32}\\z") ||
                hash == null || !Regex.IsMatch(hash, "\\A[0-9a-fA-F]{64}\\z"))
                throw new InvalidOperationException("Invalid checkpoint source selector or SHA-256.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string path = Path.Combine(root, "saves", save, checkpoint + ".sfs");
            RejectLinks(root, path);
            byte[] bytes = Read(path, 8 * 1024 * 1024);
            string digest = Digest(bytes);
            if (!string.Equals(digest, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Checkpoint SHA-256 mismatch.");
            string receipts = Path.Combine(root, "GameData", "KspContinuum", "PluginData");
            string[] folders = Directory.GetDirectories(receipts, "mission-*");
            if (folders.Length > 1000) throw new InvalidOperationException("Too many mission receipts.");
            string parent = null; double ut = double.NaN; int matches = 0;
            foreach (string folder in folders)
            {
                string receipt = Path.Combine(folder, "mission.txt");
                if (!File.Exists(receipt)) continue;
                RejectLinks(root, receipt);
                string[] lines = Encoding.UTF8.GetString(Read(receipt, 128 * 1024)).Split('\n');
                if (Value(lines, "save") != save) continue;
                matches++;
                parent = Value(lines, "attemptId");
                if (!SurveyPolicy.ValidAttemptId(parent) || Value(lines, "missionId") != "CSP-0002" || Value(lines, "vehicleDesignId") != "CV-0001-R01")
                    throw new InvalidOperationException("Checkpoint parent receipt is not the qualified stock survey mission.");
                string milestone = Path.Combine(folder, "milestones.csv");
                RejectLinks(root, milestone);
                string expected = "saves/" + save + "/" + checkpoint + ".sfs";
                var entries = Encoding.UTF8.GetString(Read(milestone, 128 * 1024)).Split('\n').Select(line => line.TrimEnd('\r').Split(',')).Where(fields => fields.Length == 3 && fields[2] == expected).ToArray();
                if (entries.Length != 1 || entries[0][1] != "Capture" || !double.TryParse(entries[0][0], NumberStyles.Float, CultureInfo.InvariantCulture, out ut) || !SurveyPolicy.Finite(ut) || ut < 0)
                    throw new InvalidOperationException("Checkpoint must match one pre-control Capture milestone.");
            }
            if (matches != 1) throw new InvalidOperationException("Checkpoint requires exactly one matching parent mission receipt.");
            return new CheckpointSource(path, bytes) { ParentAttemptId = parent, Checkpoint = checkpoint, Sha256 = digest, UniversalTime = ut };
        }

        static string Value(string[] lines, string key)
        {
            string prefix = key + "=";
            string[] values = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (values.Length > 1) throw new InvalidOperationException("Duplicate checkpoint identity field: " + key);
            return values.Length == 0 ? null : values[0].Substring(prefix.Length).TrimEnd('\r');
        }

        static byte[] Read(string path, int limit)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > limit) throw new InvalidOperationException("Checkpoint evidence is missing or exceeds its size bound.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > limit) throw new InvalidOperationException("Checkpoint evidence exceeds its size bound.");
                var bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new InvalidOperationException("Checkpoint evidence changed during reading.");
                    offset += read;
                }
                return bytes;
            }
        }

        static void RejectLinks(string root, string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Symbolic checkpoint sources are not supported.");
                if (current == root) return;
            }
            throw new InvalidOperationException("Checkpoint escaped the instance root.");
        }

        public void CopyTo(string destination)
        {
            if (Directory.Exists(destination) || File.Exists(destination)) throw new InvalidOperationException("Checkpoint destination already exists.");
            VerifyUnchanged();
            Directory.CreateDirectory(destination);
            string copy = Path.Combine(destination, "checkpoint-source.sfs");
            using (var stream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
            if (FileDigest(copy) != Sha256) throw new InvalidOperationException("Checkpoint copy SHA-256 mismatch.");
            VerifyUnchanged();
        }

        public void VerifyUnchanged() { if (FileDigest(path) != Sha256) throw new InvalidOperationException("Original checkpoint changed."); }
        public static string FileDigest(string path) { return Digest(Read(path, 8 * 1024 * 1024)); }
        static string Digest(byte[] value) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
    }
}
