using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;

// Exports item icons and wearable meshes for the local wardrobe preview.
// The job file comes from `python tools/wardrobe.py plan`; existing outputs are
// kept, so an interrupted run can be resumed with the same arguments.
if (args.Length != 6)
    throw new ArgumentException("Usage: WardrobeExport <Paks directory> <key report> <Oodle DLL> <usmap> <job.json> <output directory under research/local>");
var local = Path.GetFullPath("research/local") + Path.DirectorySeparatorChar;
var root = Path.GetFullPath(args[5]);
if (!(root + Path.DirectorySeparatorChar).StartsWith(local, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Output directory must be under research/local/");
using var keyReport = JsonDocument.Parse(File.ReadAllText(args[1]));
using var job = JsonDocument.Parse(File.ReadAllText(args[4]));
OodleHelper.Initialize(Path.GetFullPath(args[2]));
var detexPath = Path.GetFullPath("third_party/CUE4Parse/CUE4Parse-Conversion/Resources/Detex.dll");
if (File.Exists(detexPath)) DetexHelper.Initialize(detexPath);
using var provider = new DefaultFileProvider(args[0], SearchOption.AllDirectories,
    new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase);
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(args[3]);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(keyReport.RootElement.GetProperty("key_hex").GetString()!));
provider.PostMount();
Console.WriteLine($"Mounted file entries: {provider.Files.Count}");

var failures = new List<object>();
var written = 0;
var skipped = 0;
string Target(string relative)
{
    var path = Path.GetFullPath(Path.Combine(root, relative));
    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"Output escapes the export directory: {relative}");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    return path;
}
bool SaveTexture(UTexture texture, int maxSize, string path)
{
    var bitmap = texture.Decode(maxSize);
    if (bitmap == null) return false;
    var bytes = bitmap.Encode(ETextureFormat.Webp, false, out _, 90);
    File.WriteAllBytes(path + ".tmp", bytes);
    File.Move(path + ".tmp", path, true);
    return true;
}
string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

// Item icons: one texture package per entry. Entries with only a "name" were not in
// the IoStore asset catalogue; look them up among all mounted files (including Paks).
Dictionary<string, string>? uiPackages = null;
string? FindUiPackage(string name)
{
    uiPackages ??= provider.Files.Keys.Where(k => k.StartsWith("AION2/Content/UI/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        .GroupBy(k => Path.GetFileNameWithoutExtension(k), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First()[..^".uasset".Length], StringComparer.OrdinalIgnoreCase);
    return uiPackages.GetValueOrDefault(name);
}
if (job.RootElement.TryGetProperty("icons", out var icons))
    foreach (var icon in icons.EnumerateArray())
    {
        var package = icon.TryGetProperty("package", out var known) ? known.GetString()! : FindUiPackage(icon.GetProperty("name").GetString()!);
        if (package == null) { failures.Add(new {kind = "icon", package = icon.GetProperty("name").GetString(), error = "Not found among mounted files"}); continue; }
        var path = Target(icon.GetProperty("out").GetString()!);
        if (File.Exists(path)) { skipped++; continue; }
        try
        {
            var texture = provider.LoadPackage(package).GetExports().OfType<UTexture>().First();
            if (!SaveTexture(texture, icon.GetProperty("max").GetInt32(), path)) throw new InvalidDataException("Texture did not decode");
            written++;
        }
        catch (Exception e) { failures.Add(new {kind = "icon", package, error = e.Message}); }
        if ((written + failures.Count) % 200 == 0) Console.WriteLine($"icons: {written} written, {skipped} kept, {failures.Count} failed");
    }

// Meshes: glTF geometry plus a material manifest with downscaled textures.
// Materials are resolved here rather than embedded so textures are shared.
var textureSize = job.RootElement.TryGetProperty("texture_max", out var max) ? max.GetInt32() : 1024;
var textureFiles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
string? ExportTexture(UTexture texture)
{
    var key = texture.GetPathName();
    if (textureFiles.TryGetValue(key, out var known)) return known;
    var relative = "textures/" + key.Split('.')[0].TrimStart('/').Replace("Game/", "") + ".webp";
    var path = Target(relative);
    string? result = relative;
    try { if (!File.Exists(path) && !SaveTexture(texture, textureSize, path)) result = null; }
    catch (Exception e) { failures.Add(new {kind = "texture", package = key, error = e.Message}); result = null; }
    return textureFiles[key] = result;
}
var meshOptions = new ExportOptions(meshFormat: EMeshFormat.Gltf2, exportMaterials: false, exportMorphTargets: false);
var scratch = Path.Combine(root, "_scratch");
if (job.RootElement.TryGetProperty("meshes", out var meshes))
    foreach (var item in meshes.EnumerateArray())
    {
        var package = item.GetProperty("package").GetString()!;
        var outName = item.GetProperty("out").GetString()!;
        var manifestPath = Target(outName + ".json");
        if (File.Exists(manifestPath)) { skipped++; continue; }
        try
        {
            var mesh = provider.LoadPackage(package).GetExports().FirstOrDefault(o => o is USkeletalMesh or UStaticMesh)
                ?? throw new InvalidDataException("No skeletal or static mesh export");
            var slots = mesh switch
            {
                USkeletalMesh s => s.SkeletalMaterials.Select(m => (Slot: m.MaterialSlotName.Text, Ref: m.MaterialInterface)).ToArray(),
                UStaticMesh s => s.StaticMaterials.Select(m => (Slot: m.MaterialSlotName.Text, Ref: m.MaterialInterface)).ToArray(),
                _ => []
            };
            var session = new ExportSession {MaxDegreeOfParallelism = 1};
            session.Add(mesh);
            var result = (await session.RunAsync(scratch, meshOptions)).Single();
            if (!result.Success) throw result.Error ?? new InvalidDataException("Mesh export failed");
            var glb = result.DiskFilePaths!.Single(p => p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
            var glbPath = Target(outName + ".glb");
            File.Move(glb, glbPath, true);
            var materials = new List<object>();
            foreach (var (slot, reference) in slots)
            {
                if (reference == null || !reference.TryLoad<UMaterialInterface>(out var material))
                {
                    materials.Add(new {slot, name = (string?)null});
                    continue;
                }
                var parameters = new CMaterialParams2();
                material.GetParams(parameters, EMaterialDepth.TopLayerOnly);
                var textures = new Dictionary<string, string>();
                foreach (var (name, pointer) in parameters.Textures)
                    if (pointer.TryLoad<UTexture>(out var texture) && ExportTexture(texture) is { } file)
                        textures[name] = file;
                materials.Add(new
                {
                    slot, name = material.Name, parent = (material as UMaterialInstance)?.Parent?.Name,
                    blend = parameters.BlendMode.ToString(), textures,
                    colors = parameters.Colors.ToDictionary(c => c.Key, c => new[] {c.Value.R, c.Value.G, c.Value.B, c.Value.A}),
                    scalars = parameters.Scalars, switches = parameters.Switches
                });
            }
            var manifest = new {package, kind = mesh is USkeletalMesh ? "skeletal" : "static", glb = Rel(glbPath), materials};
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions {WriteIndented = true}));
            written++;
        }
        catch (Exception e) { failures.Add(new {kind = "mesh", package, error = e.Message}); }
        if ((written + failures.Count) % 25 == 0) Console.WriteLine($"meshes: {written} written, {skipped} kept, {failures.Count} failed");
    }
// Sockets (named attach points such as weapon grips) of a mesh and its skeleton,
// converted to glTF space the same way CUE4Parse converts bones: swap Y/Z, cm to m.
if (job.RootElement.TryGetProperty("sockets", out var socketJobs))
    foreach (var item in socketJobs.EnumerateArray())
    {
        var package = item.GetProperty("package").GetString()!;
        var path = Target(item.GetProperty("out").GetString()!);
        try
        {
            var mesh = provider.LoadPackage(package).GetExports().OfType<USkeletalMesh>().First();
            var references = mesh.Sockets.ToList();
            if (mesh.Skeleton.TryLoad<USkeleton>(out var skeleton)) references.AddRange(skeleton.Sockets);
            var sockets = new List<object>();
            foreach (var reference in references)
            {
                if (!reference.TryLoad<USkeletalMeshSocket>(out var socket)) continue;
                var l = socket.RelativeLocation; var q = socket.RelativeRotation.Quaternion(); var s = socket.RelativeScale;
                sockets.Add(new
                {
                    name = socket.SocketName.Text, bone = socket.BoneName.Text,
                    translation = new[] {l.X * 0.01f, l.Z * 0.01f, l.Y * 0.01f},
                    rotation = new[] {q.X, q.Z, q.Y, -q.W},
                    scale = new[] {s.X, s.Z, s.Y}
                });
            }
            // The root mesh skeleton carries the weapon attach bones (GswordWeapon, BowWeapon, ...)
            // that the visible body parts omit; record them relative to their parent bone.
            var bones = new List<object>();
            if (item.TryGetProperty("bones_from", out var bonesFrom))
            {
                var skeletonRef = provider.LoadPackage(bonesFrom.GetString()!).GetExports().OfType<USkeletalMesh>().First().ReferenceSkeleton;
                var pattern = new System.Text.RegularExpressions.Regex(item.GetProperty("bone_pattern").GetString()!);
                for (var i = 0; i < skeletonRef.FinalRefBoneInfo.Length; i++)
                {
                    var info = skeletonRef.FinalRefBoneInfo[i];
                    if (info.ParentIndex < 0 || !pattern.IsMatch(info.Name.Text)) continue;
                    var pose = skeletonRef.FinalRefBonePose[i];
                    var (l, q, s) = (pose.Translation, pose.Rotation, pose.Scale3D);
                    bones.Add(new
                    {
                        name = info.Name.Text, bone = skeletonRef.FinalRefBoneInfo[info.ParentIndex].Name.Text,
                        translation = new[] {l.X * 0.01f, l.Z * 0.01f, l.Y * 0.01f},
                        rotation = new[] {q.X, q.Z, q.Y, -q.W},
                        scale = new[] {s.X, s.Z, s.Y}
                    });
                }
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new {package, sockets, bones}, new JsonSerializerOptions {WriteIndented = true}));
            written++;
        }
        catch (Exception e) { failures.Add(new {kind = "sockets", package, error = e.Message}); }
    }
// Animations: every frame as bone-local transforms (glTF space), keyed by bone name
// so the viewer can apply them to every part's skeleton. Per bone, "rotation" holds 4
// values per frame and "translation" 3 per frame; a track that never changes is
// stored as a single frame.
if (job.RootElement.TryGetProperty("poses", out var poseJobs))
    foreach (var item in poseJobs.EnumerateArray())
    {
        var package = item.GetProperty("package").GetString()!;
        var path = Target(item.GetProperty("out").GetString()!);
        if (File.Exists(path)) { skipped++; continue; }
        try
        {
            var animation = provider.LoadPackage(package).GetExports().OfType<UAnimSequence>().First();
            var set = animation.ConvertAnims();
            var sequence = set.Sequences.Single();
            var reference = set.Skeleton.ReferenceSkeleton;
            var frames = Math.Max(sequence.NumFrames, 1);
            var bones = new Dictionary<string, object>();
            float[] Compact(List<float> values, int width) =>
                Enumerable.Range(1, frames - 1).All(f => Enumerable.Range(0, width).All(c => Math.Abs(values[f * width + c] - values[c]) < 1e-5f))
                    ? values.Take(width).ToArray() : values.ToArray();
            for (var i = 0; i < Math.Min(sequence.Tracks.Count, reference.FinalRefBoneInfo.Length); i++)
            {
                var track = sequence.Tracks[i];
                if (track.KeyQuat.Length == 0 && track.KeyPos.Length == 0) continue;
                var rotation = new List<float>(frames * 4);
                var translation = new List<float>(frames * 3);
                FQuat? previous = null;
                for (var frame = 0; frame < frames; frame++)
                {
                    var pose = reference.FinalRefBonePose[i];
                    var (q, l, s) = (pose.Rotation, pose.Translation, pose.Scale3D);
                    track.GetBoneTransform(frame, frames, ref q, ref l, ref s);
                    // Keep consecutive quaternions in the same hemisphere for clean interpolation.
                    if (previous is { } last && last.X * q.X + last.Y * q.Y + last.Z * q.Z + last.W * q.W < 0)
                        q = new FQuat(-q.X, -q.Y, -q.Z, -q.W);
                    previous = q;
                    rotation.AddRange(new[] {q.X, q.Z, q.Y, -q.W}.Select(v => MathF.Round(v, 5)));
                    translation.AddRange(new[] {l.X * 0.01f, l.Z * 0.01f, l.Y * 0.01f}.Select(v => MathF.Round(v, 5)));
                }
                bones[reference.FinalRefBoneInfo[i].Name.Text] = new {rotation = Compact(rotation, 4), translation = Compact(translation, 3)};
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new {package, frames, fps = sequence.FramesPerSecond, bones}));
            written++;
        }
        catch (Exception e) { failures.Add(new {kind = "pose", package, error = e.Message}); }
    }
if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
var report = Path.Combine(root, $"export-report-{DateTime.Now:yyyyMMdd-HHmmss}.json");
File.WriteAllText(report, JsonSerializer.Serialize(new {written, skipped, failures}, new JsonSerializerOptions {WriteIndented = true}));
Console.WriteLine($"Done: {written} written, {skipped} kept, {failures.Count} failed. Report: {report}");
