using AssetsTools.NET;
using AssetsTools.NET.Extra;

if (!(args.Length == 3 && args[0] == "inspect") &&
    !(args.Length == 6 && args[0] == "graft"))
{
    Console.Error.WriteLine("Usage: inspect <classdata.tpk> <assets-or-bundle> | graft <classdata.tpk> <target-assets> <donor-bundle> <output-assets> <shader-name>");
    return 2;
}

var manager = new AssetsManager();
manager.LoadClassPackage(args[1]);

if (args[0] == "inspect")
{
    var path = args[2];
    var file = IsBundle(path)
        ? manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(path, true), 0, false)
        : manager.LoadAssetsFile(path, false);
    manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
    Console.WriteLine($"Unity {file.file.Metadata.UnityVersion}");
    foreach (var asset in file.file.GetAssetsOfType(AssetClassID.Shader))
    {
        var field = manager.GetBaseField(file, asset);
        Console.WriteLine($"{Name(field)} pathId={asset.PathId} platforms=[{string.Join(",", Platforms(field))}]");
    }
    return 0;
}

var targetPath = args[2];
var bundlePath = args[3];
var outputPath = args[4];
var shaderName = args[5];
if (Path.GetFullPath(targetPath) == Path.GetFullPath(outputPath) || File.Exists(outputPath))
    throw new InvalidOperationException("Output must be a new file, separate from the source");

var target = manager.LoadAssetsFile(targetPath, false);
var donor = manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(bundlePath, true), 0, false);
if (target.file.Metadata.UnityVersion != donor.file.Metadata.UnityVersion)
    throw new InvalidOperationException("Target and donor Unity asset versions differ");
manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);

var targetMatches = target.file.GetAssetsOfType(AssetClassID.Shader)
    .Where(asset => Name(manager.GetBaseField(target, asset)) == shaderName).ToArray();
var donorMatches = donor.file.GetAssetsOfType(AssetClassID.Shader)
    .Where(asset => Name(manager.GetBaseField(donor, asset)) == shaderName).ToArray();
if (targetMatches.Length != 1 || donorMatches.Length != 1)
    throw new InvalidOperationException("Expected exactly one target and donor shader with that name");

var targetField = manager.GetBaseField(target, targetMatches[0]);
var donorField = manager.GetBaseField(donor, donorMatches[0]);
if (!Platforms(targetField).SequenceEqual([15]) || !Platforms(donorField).Contains(14))
    throw new InvalidOperationException("Expected an OpenGL-only target and a Metal donor");

targetMatches[0].SetNewData(donorField);
using (var writer = new AssetsFileWriter(outputPath))
    target.file.Write(writer);
Console.WriteLine($"Grafted {shaderName} into path ID {targetMatches[0].PathId}");
return 0;

static bool IsBundle(string path)
{
    using var stream = File.OpenRead(path);
    Span<byte> signature = stackalloc byte[7];
    return stream.Read(signature) == signature.Length &&
           System.Text.Encoding.ASCII.GetString(signature) == "UnityFS";
}

static string Name(AssetTypeValueField field) => field["m_ParsedForm"]["m_Name"].AsString;

static int[] Platforms(AssetTypeValueField field) => field["platforms"].Children
    .SelectMany(child => child.Children).Select(child => child.AsInt).ToArray();
