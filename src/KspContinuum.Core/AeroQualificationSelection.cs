using System;
using System.IO;

namespace KspContinuum
{
    public sealed class AeroQualificationSelection
    {
        public string Save { get; private set; }
        public string Checkpoint { get; private set; }
        AeroQualificationSelection(string save, string checkpoint) { Save = save; Checkpoint = checkpoint; }

        public static AeroQualificationSelection Parse(string[] arguments)
        {
            if (arguments == null) throw new ArgumentNullException("arguments");
            return new AeroQualificationSelection(Leaf(Argument(arguments, "--continuum-aero-save"), "save"),
                Leaf(Argument(arguments, "--continuum-aero-checkpoint"), "checkpoint"));
        }

        static string Argument(string[] arguments, string name)
        {
            int index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                Array.LastIndexOf(arguments, name) != index) throw new ArgumentException("Missing or duplicate argument: " + name);
            return arguments[index + 1];
        }

        static string Leaf(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value == "." || value == ".." || value.Contains("..") ||
                Path.IsPathRooted(value) || Path.GetFileName(value) != value)
                throw new ArgumentException("Aerodynamic qualification " + field + " must be a safe leaf name.");
            foreach (char character in value)
                if (char.IsControl(character) || character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar)
                    throw new ArgumentException("Aerodynamic qualification " + field + " must be a safe leaf name.");
            return value;
        }
    }
}
