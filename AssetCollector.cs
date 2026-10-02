#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace UnityPrefabConverter;

// Maps every Unity asset to the same relative location under the output folder
public sealed class OutputPaths {
// ----- VALUES -----
    public readonly string ProjectRoot;
    public readonly string AssetsRoot;
    public readonly string OutputDir;
    
// ----- PUBLIC CALLS -----
    // Setup
    public OutputPaths(string projectRoot, string outputDir) {
        ProjectRoot = Path.GetFullPath(projectRoot);
        AssetsRoot = Path.Combine(ProjectRoot, "Assets");
        OutputDir = outputDir.TrimEnd('/');
    }
    
    // Mapping
    public string Relative(string unityPath) {
        var relative = Path.GetRelativePath(AssetsRoot, unityPath);
        if (relative.StartsWith("..") || Path.IsPathRooted(relative)) relative = Path.Combine("_Packages", Path.GetRelativePath(ProjectRoot, unityPath));
        return relative.Replace('\\', '/');
    }
    public string ResPath(string unityPath, string newExtension = null) {
        var relative = Relative(unityPath);
        if (newExtension != null) relative = WithoutExtension(relative) + newExtension;
        return OutputDir + "/" + relative;
    }
    public string ResPathWithoutExtension(string unityPath) {
        return WithoutExtension(ResPath(unityPath));
    }
    public static string WithoutExtension(string path) {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 ? path.Substring(0, path.Length - extension.Length) : path;
    }
}

// Finds every model and texture a set of prefabs needs, following guid references through nested prefabs and materials
public sealed class AssetCollector {
// ----- VALUES -----
    public readonly List<(string Source, string Target)> Copies = new();
    public event Action<string> Logged;
    public static readonly HashSet<string> MODEL_EXTENSIONS = new(StringComparer.OrdinalIgnoreCase) { ".fbx", ".glb", ".gltf", ".obj", ".blend", ".dae" };
    public static readonly HashSet<string> CONVERTED_TEXTURE_EXTENSIONS = new(StringComparer.OrdinalIgnoreCase) { ".psd", ".psb", ".tif", ".tiff" };
    public static readonly HashSet<string> TEXTURE_EXTENSIONS = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp", ".exr", ".hdr", ".svg" };
    private readonly UnityAssetIndex _index;
    private readonly OutputPaths _paths;
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex GUID_REGEX = new(@"guid: ([0-9a-fA-F]{32})");
    private static readonly HashSet<string> SCANNED_EXTENSIONS = new(StringComparer.OrdinalIgnoreCase) { ".prefab", ".mat", ".unity", ".controller", ".overrideController", ".anim", ".asset", ".terrainlayer" };
    
// ----- PUBLIC CALLS -----
    // Setup
    public AssetCollector(UnityAssetIndex index, OutputPaths paths) {
        _index = index;
        _paths = paths;
    }
    
    // Collection
    public void Collect(string unityPath) {
        visit(Path.GetFullPath(unityPath));
    }
    // Every texture and model in a folder, whether or not a prefab uses it
    public int CollectFolder(string folder, bool textures, bool models) {
        var before = Copies.Count;
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p)) {
            var extension = Path.GetExtension(path);
            var wanted = (textures && (TEXTURE_EXTENSIONS.Contains(extension) || IsConvertedTexture(path))) || (models && IsModel(path));
            if (wanted) visit(Path.GetFullPath(path));
        }
        return Copies.Count - before;
    }
    public static bool IsModel(string path) {
        return MODEL_EXTENSIONS.Contains(Path.GetExtension(path));
    }
    public static bool IsConvertedTexture(string path) {
        return CONVERTED_TEXTURE_EXTENSIONS.Contains(Path.GetExtension(path));
    }
    public static bool NeedsCopy(string source, string target) {
        if (!File.Exists(target)) return true;
        if (IsConvertedTexture(source)) return File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target);
        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        return sourceInfo.Length != targetInfo.Length || sourceInfo.LastWriteTimeUtc > targetInfo.LastWriteTimeUtc;
    }
    
    // Conversion
    public static string ConvertToPng(string source, string target) {
        TextureDecoder.DecodedImage decoded;
        try {
            decoded = TextureDecoder.Decode(source);
        } catch (Exception exception) {
            return exception.Message;
        }
        var image = Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba);
        var error = image.SavePng(target);
        return error == Error.Ok ? null : error.ToString();
    }
    
// ----- HELPERS -----
    // Traversal
    private void visit(string unityPath) {
        if (unityPath == null || !_visited.Add(unityPath) || !File.Exists(unityPath)) return;
        var extension = Path.GetExtension(unityPath);
        if (SCANNED_EXTENSIONS.Contains(extension)) {
            scanReferences(unityPath);
        } else if (IsModel(unityPath)) {
            addCopy(unityPath);
            foreach (var remap in _index.GetModelMeta(unityPath).MaterialRemaps.Values) visit(_index.GetPath(remap.Guid));
            foreach (var companion in _index.CompanionModels(unityPath)) visit(companion);
        } else if (TEXTURE_EXTENSIONS.Contains(extension)) {
            addCopy(unityPath);
        } else if (IsConvertedTexture(unityPath)) {
            addConvertedTexture(unityPath);
        }
    }
    private void scanReferences(string unityPath) {
        byte[] data;
        try {
            data = File.ReadAllBytes(unityPath);
        } catch (IOException) {
            return;
        }
        if (UnityBinary.IsSerializedFile(data)) {
            try {
                foreach (var guid in UnityBinary.ExternalGuids(data)) visit(_index.GetPath(guid));
            } catch (Exception exception) {
                Logged?.Invoke($"WARNING: Could not read binary file {Path.GetFileName(unityPath)}: {exception.Message}");
            }
            return;
        }
        var text = System.Text.Encoding.UTF8.GetString(data);
        if (!text.StartsWith("%YAML")) return;
        foreach (Match match in GUID_REGEX.Matches(text)) visit(_index.GetPath(match.Groups[1].Value));
    }
    
    // Copies
    private void addCopy(string unityPath) {
        Copies.Add((unityPath, ProjectSettings.GlobalizePath(_paths.ResPath(unityPath))));
    }
    // Godot can't import PSD or TIFF, so they become a PNG at the same location (a same-named PNG/JPG/TGA next to it wins)
    private void addConvertedTexture(string unityPath) {
        var directory = Path.GetDirectoryName(unityPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(unityPath);
        var sibling = TEXTURE_EXTENSIONS.Select(e => Path.Combine(directory, stem + e)).FirstOrDefault(File.Exists);
        if (sibling != null) {
            visit(sibling);
            return;
        }
        Copies.Add((unityPath, ProjectSettings.GlobalizePath(_paths.ResPath(unityPath, ".png"))));
    }
}
#endif
