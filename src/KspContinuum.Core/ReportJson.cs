using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace KspContinuum
{
    public static class ReportJson
    {
        public static string Encode(object report)
        {
            var output = new StringBuilder();
            Write(output, report);
            return output.ToString();
        }

        static void Write(StringBuilder output, object value)
        {
            if (value == null) { output.Append("null"); return; }
            if (value is string)
            {
                output.Append('"');
                foreach (char c in (string)value)
                {
                    if (c == '"' || c == '\\') output.Append('\\').Append(c);
                    else if (c < 32 || char.IsSurrogate(c))
                        output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                }
                output.Append('"'); return;
            }
            if (value is bool) { output.Append((bool)value ? "true" : "false"); return; }
            if (value is int || value is long || value is float || value is double)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new ArgumentException("Report contains a nonfinite number.");
                output.Append(((IFormattable)value).ToString(value is float || value is double ? "R" : null,
                    CultureInfo.InvariantCulture)); return;
            }
            if (value is Array)
            {
                output.Append('['); bool first = true;
                foreach (var item in (Array)value)
                {
                    if (!first) output.Append(','); first = false; Write(output, item);
                }
                output.Append(']'); return;
            }
            var type = value.GetType();
            if (type != typeof(BenchReport) && type != typeof(Sample) && type != typeof(VesselReport) &&
                type != typeof(PartReport) && type != typeof(ProbeReport) && type != typeof(MarkerReport) &&
                type != typeof(ProfileFrame) && type != typeof(ProfileDistribution) && type != typeof(ProfileMarkerSummary) &&
                type != typeof(LoopTimingReport) && type != typeof(LoopTimingScope) && type != typeof(LoopTimingSample) &&
                type != typeof(ShadowReport) && type != typeof(ShadowSample) && type != typeof(ShadowBody))
                throw new ArgumentException("Unsupported report type: " + type.FullName);
            output.Append('{'); bool firstField = true;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!firstField) output.Append(','); firstField = false;
                Write(output, field.Name); output.Append(':'); Write(output, field.GetValue(value));
            }
            output.Append('}');
        }
    }
}
