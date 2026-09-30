using AssetsTools.NET;
using AssetsTools.NET.Extra;

if (!(args.Length == 3 && args[0] == "inspect") &&
    !(args.Length == 6 && (args[0] == "graft" || args[0] == "graft-list")))
{
    Console.Error.WriteLine("Usage: inspect <classdata.tpk> <assets-or-bundle> | graft <classdata.tpk> <target-assets> <donor-bundle> <output-assets> <shader-name> | graft-list <classdata.tpk> <target-assets> <donor-bundle> <output-assets> <names-file>");
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
var names = args[0] == "graft"
    ? [args[5]]
    : File.ReadAllLines(args[5]).Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
    throw new InvalidOperationException("Expected at least one distinct shader name");
if (Path.GetFullPath(targetPath) == Path.GetFullPath(outputPath) || File.Exists(outputPath))
    throw new InvalidOperationException("Output must be a new file, separate from the source");

var target = manager.LoadAssetsFile(targetPath, false);
var donor = manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(bundlePath, true), 0, false);
if (target.file.Metadata.UnityVersion != donor.file.Metadata.UnityVersion)
    throw new InvalidOperationException("Target and donor Unity asset versions differ");
manager.LoadClassDatabaseFromPackage(target.file.Metadata.UnityVersion);

var targetShaders = target.file.GetAssetsOfType(AssetClassID.Shader)
    .GroupBy(asset => Name(manager.GetBaseField(target, asset)), StringComparer.Ordinal)
    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
var donorShaders = donor.file.GetAssetsOfType(AssetClassID.Shader)
    .GroupBy(asset => Name(manager.GetBaseField(donor, asset)), StringComparer.Ordinal)
    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
var replacements = new List<(AssetFileInfo targetAsset, AssetTypeValueField donorField)>();
foreach (var shaderName in names)
{
    if (!targetShaders.TryGetValue(shaderName, out var targetMatches) || targetMatches.Length != 1 ||
        !donorShaders.TryGetValue(shaderName, out var donorMatches) || donorMatches.Length != 1)
        throw new InvalidOperationException($"Expected exactly one target and donor shader named {shaderName}");
    var targetField = manager.GetBaseField(target, targetMatches[0]);
    var donorField = manager.GetBaseField(donor, donorMatches[0]);
    if (!Platforms(targetField).SequenceEqual([15]) || !Platforms(donorField).Contains(14))
        throw new InvalidOperationException($"Expected OpenGL-only target and Metal donor for {shaderName}");
    replacements.Add((targetMatches[0], donorField));
}

foreach (var replacement in replacements)
    replacement.targetAsset.SetNewData(replacement.donorField);
using (var writer = new AssetsFileWriter(outputPath))
    target.file.Write(writer);
foreach (var replacement in replacements)
    Console.WriteLine($"Grafted {Name(replacement.donorField)} into path ID {replacement.targetAsset.PathId}");
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
