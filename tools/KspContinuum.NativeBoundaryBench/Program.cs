using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

const int Samples = 101;
const int Warmups = 12;
const double Dt = 1.0 / 60.0;
int[] counts = [16, 64, 256, 1024, 4096, 16384];
uint[] stepCounts = [1, 4];

if (args.Length != 2 || args[0] != "--output")
    throw new ArgumentException("usage: --output NEW_REPORT.json");
if (Native.ContinuumBoundaryAbiVersion() != 1)
    throw new InvalidOperationException("native ABI version mismatch");
if (Marshal.SizeOf<BodyF64>() != 80 || Marshal.SizeOf<BodyF32>() != 40)
    throw new InvalidOperationException("managed ABI layout mismatch");

var noops = Measure(1001, () => GC.KeepAlive(Native.ContinuumBoundaryNoop(0x123456789abcdef0)));
var rows = new List<Row>();
foreach (int count in counts)
foreach (uint steps in stepCounts)
{
    Console.Error.WriteLine($"measuring {count} bodies x {steps} step(s)");
    BodyD[] canonical = Fixture(count);
    BodyF64[] f64 = PackF64(canonical);
    BodyF32[] f32 = PackF32(canonical);

    var managed64 = (BodyF64[])f64.Clone();
    var native64 = (BodyF64[])f64.Clone();
    var managed32 = (BodyF32[])f32.Clone();
    var native32 = (BodyF32[])f32.Clone();
    Timing managedF64 = Summarize(Measure(Samples, () => IntegrateF64(managed64, Dt, steps)), count);
    Timing nativeF64 = Summarize(Measure(Samples, () => Native.Integrate(native64, Dt, steps)), count);
    Timing managedF32 = Summarize(Measure(Samples, () => IntegrateF32(managed32, (float)Dt, steps)), count);
    Timing nativeF32 = Summarize(Measure(Samples, () => Native.Integrate(native32, (float)Dt, steps)), count);

    var packTarget64 = new BodyF64[count];
    var packTarget32 = new BodyF32[count];
    Timing pack64 = Summarize(Measure(Samples, () => CopyToF64(canonical, packTarget64)), count);
    Timing publish64 = Summarize(Measure(Samples, () => PublishF64(native64, canonical)), count);
    Timing pack32 = Summarize(Measure(Samples, () => CopyToF32(canonical, packTarget32)), count);
    Timing publish32 = Summarize(Measure(Samples, () => PublishF32(native32, canonical)), count);

    BodyD[] transactionState64 = Fixture(count);
    BodyF64[] transactionBuffer64 = new BodyF64[count];
    Timing endToEnd64 = Summarize(Measure(Samples, () =>
    {
        CopyToF64(transactionState64, transactionBuffer64);
        Native.Integrate(transactionBuffer64, Dt, steps);
        PublishF64(transactionBuffer64, transactionState64);
    }), count);
    BodyD[] transactionState32 = Fixture(count);
    BodyF32[] transactionBuffer32 = new BodyF32[count];
    Timing endToEnd32 = Summarize(Measure(Samples, () =>
    {
        CopyToF32(transactionState32, transactionBuffer32);
        Native.Integrate(transactionBuffer32, (float)Dt, steps);
        PublishF32(transactionBuffer32, transactionState32);
    }), count);

    BodyF64[] expected64 = PackF64(Fixture(count));
    BodyF64[] observed64 = (BodyF64[])expected64.Clone();
    IntegrateF64(expected64, Dt, steps);
    Native.Integrate(observed64, Dt, steps);
    BodyF32[] expected32 = PackF32(Fixture(count));
    BodyF32[] observed32 = (BodyF32[])expected32.Clone();
    IntegrateF32(expected32, (float)Dt, steps);
    Native.Integrate(observed32, (float)Dt, steps);
    if (!expected64.AsSpan().SequenceEqual(observed64) || !expected32.AsSpan().SequenceEqual(observed32))
        throw new InvalidOperationException($"native result mismatch for {count} x {steps}");

    double f32Error = MaxPositionError(expected64, observed32);
    rows.Add(new Row(count, steps, managedF64, nativeF64, pack64, publish64, endToEnd64,
        managedF64.MedianNs / nativeF64.MedianNs, managedF64.MedianNs / endToEnd64.MedianNs,
        managedF32, nativeF32, pack32, publish32, endToEnd32,
        managedF32.MedianNs / nativeF32.MedianNs, managedF32.MedianNs / endToEnd32.MedianNs, f32Error));
}

var report = new Report(
    "continuum-native-boundary-bench/v2",
    RuntimeInformation.ProcessArchitecture.ToString(),
    RuntimeInformation.OSDescription,
    RuntimeInformation.FrameworkDescription,
    Samples,
    new Timing(Summarize(noops, 1).MedianNs, Summarize(noops, 1).P95Ns, Summarize(noops, 1).NsPerBodyAtMedian),
    "synchronous P/Invoke into an x86_64 Rust cdylib; pinned blittable arrays are mutated in place; return is the synchronization boundary",
    "end-to-end is directly timed over one pack-call-publish transaction; component diagnostics are separate; f64 preserves canonical precision and f32 converts both ways",
    rows);
using var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write);
JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
stream.WriteByte((byte)'\n');

static BodyD[] Fixture(int count) => Enumerable.Range(0, count).Select(i => new BodyD(
    i * 0.25, i % 31, i * -0.125, 0.5 + i * 0.00001, -0.25, i % 7 * 0.01,
    (i % 13 - 6) * 0.2, -9.81, i % 5 * 0.1, 1.0 / (1 + i % 97))).ToArray();
static BodyF64[] PackF64(BodyD[] source) { var result=new BodyF64[source.Length];CopyToF64(source,result);return result; }
static BodyF32[] PackF32(BodyD[] source) { var result=new BodyF32[source.Length];CopyToF32(source,result);return result; }
static void CopyToF64(BodyD[] source,BodyF64[] destination) { for(int i=0;i<source.Length;i++) destination[i]=new(source[i].Px,source[i].Py,source[i].Pz,source[i].Vx,source[i].Vy,source[i].Vz,source[i].Fx,source[i].Fy,source[i].Fz,source[i].InverseMass); }
static void CopyToF32(BodyD[] source,BodyF32[] destination) { for(int i=0;i<source.Length;i++) destination[i]=new((float)source[i].Px,(float)source[i].Py,(float)source[i].Pz,(float)source[i].Vx,(float)source[i].Vy,(float)source[i].Vz,(float)source[i].Fx,(float)source[i].Fy,(float)source[i].Fz,(float)source[i].InverseMass); }
static void PublishF64(BodyF64[] source, BodyD[] destination) { for(int i=0;i<source.Length;i++) destination[i]=new(source[i].Px,source[i].Py,source[i].Pz,source[i].Vx,source[i].Vy,source[i].Vz,source[i].Fx,source[i].Fy,source[i].Fz,source[i].InverseMass); }
static void PublishF32(BodyF32[] source, BodyD[] destination) { for(int i=0;i<source.Length;i++) destination[i]=new(source[i].Px,source[i].Py,source[i].Pz,source[i].Vx,source[i].Vy,source[i].Vz,source[i].Fx,source[i].Fy,source[i].Fz,source[i].InverseMass); }
static void IntegrateF64(BodyF64[] bodies,double dt,uint steps) { for(uint s=0;s<steps;s++) foreach(ref BodyF64 b in bodies.AsSpan()) { double scale=dt*b.InverseMass;b.Vx+=b.Fx*scale;b.Vy+=b.Fy*scale;b.Vz+=b.Fz*scale;b.Px+=b.Vx*dt;b.Py+=b.Vy*dt;b.Pz+=b.Vz*dt; } }
static void IntegrateF32(BodyF32[] bodies,float dt,uint steps) { for(uint s=0;s<steps;s++) foreach(ref BodyF32 b in bodies.AsSpan()) { float scale=dt*b.InverseMass;b.Vx+=b.Fx*scale;b.Vy+=b.Fy*scale;b.Vz+=b.Fz*scale;b.Px+=b.Vx*dt;b.Py+=b.Vy*dt;b.Pz+=b.Vz*dt; } }
static double MaxPositionError(BodyF64[] expected,BodyF32[] observed) { double max=0;for(int i=0;i<expected.Length;i++){max=Math.Max(max,Math.Abs(expected[i].Px-observed[i].Px));max=Math.Max(max,Math.Abs(expected[i].Py-observed[i].Py));max=Math.Max(max,Math.Abs(expected[i].Pz-observed[i].Pz));}return max; }
static long[] Measure(int samples,Action operation) { for(int i=0;i<Warmups;i++)operation();var result=new long[samples];for(int i=0;i<samples;i++){long start=Stopwatch.GetTimestamp();operation();result[i]=Stopwatch.GetTimestamp()-start;}return result; }
static Timing Summarize(long[] ticks,int bodies) { Array.Sort(ticks);double scale=1_000_000_000.0/Stopwatch.Frequency;double median=ticks[ticks.Length/2]*scale;double p95=ticks[(int)Math.Ceiling(ticks.Length*0.95)-1]*scale;return new(median,p95,median/bodies); }

[StructLayout(LayoutKind.Sequential)] record struct BodyF64(double Px,double Py,double Pz,double Vx,double Vy,double Vz,double Fx,double Fy,double Fz,double InverseMass);
[StructLayout(LayoutKind.Sequential)] record struct BodyF32(float Px,float Py,float Pz,float Vx,float Vy,float Vz,float Fx,float Fy,float Fz,float InverseMass);
record struct BodyD(double Px,double Py,double Pz,double Vx,double Vy,double Vz,double Fx,double Fy,double Fz,double InverseMass);
record Timing(double MedianNs,double P95Ns,double NsPerBodyAtMedian);
record Row(int Bodies,uint Steps,Timing ManagedF64Kernel,Timing NativeF64Call,Timing F64Pack,Timing F64Publication,
    Timing F64EndToEnd,double NativeF64CallSpeedup,double F64EndToEndSpeedup,
    Timing ManagedF32Kernel,Timing NativeF32Call,Timing F32PackConversion,Timing F32PublicationConversion,
    Timing F32EndToEnd,double NativeF32CallSpeedup,double F32EndToEndSpeedup,double F32MaximumPositionError);
record Report(string Schema,string ProcessArchitecture,string OperatingSystem,string Framework,int SamplesPerCase,Timing NoopBoundary,string NativeRoute,string TransportAndPrecision,List<Row> Rows);

static partial class Native
{
    const string Library = "continuum_native_boundary";
    [DllImport(Library, EntryPoint="continuum_boundary_abi_version")] internal static extern uint ContinuumBoundaryAbiVersion();
    [DllImport(Library, EntryPoint="continuum_boundary_noop")] internal static extern ulong ContinuumBoundaryNoop(ulong value);
    [DllImport(Library, EntryPoint="continuum_integrate_f64")] static extern unsafe int IntegrateF64(BodyF64* bodies,nuint count,double dt,uint steps);
    [DllImport(Library, EntryPoint="continuum_integrate_f32")] static extern unsafe int IntegrateF32(BodyF32* bodies,nuint count,float dt,uint steps);
    internal static unsafe void Integrate(BodyF64[] bodies,double dt,uint steps) { fixed(BodyF64* pointer=bodies) if(IntegrateF64(pointer,(nuint)bodies.Length,dt,steps)!=0) throw new InvalidOperationException("native f64 integration failed"); }
    internal static unsafe void Integrate(BodyF32[] bodies,float dt,uint steps) { fixed(BodyF32* pointer=bodies) if(IntegrateF32(pointer,(nuint)bodies.Length,dt,steps)!=0) throw new InvalidOperationException("native f32 integration failed"); }
}
