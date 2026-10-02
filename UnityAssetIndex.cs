#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnityPrefabConverter;

public sealed class UnityAssetIndex {
// ----- VALUES -----
    public sealed class ModelMeta {
        public readonly Dictionary<long, (int ClassId, string Name)> Objects = new();
        public readonly Dictionary<string, UnityRef> MaterialRemaps = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ModelClip> Clips = new();
    }
    public sealed class ModelClip {
        public string Name;
        public string TakeName;
        public double FirstFrame;
        public double LastFrame;
        public bool Loop;
    }
    public readonly string ProjectRoot;
    public const string BUILTIN_EXTRA_GUID = "0000000000000000e000000000000000";
    public const string BUILTIN_DEFAULT_GUID = "0000000000000000f000000000000000";
    private readonly Dictionary<string, string> _pathsByGuid = new();
    private readonly Dictionary<string, string> _guidsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelMeta> _modelMetas = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _companions;
    private static readonly System.Text.RegularExpressions.Regex META_GUID_REGEX = new(@"guid: ([0-9a-fA-F]{32})");
    private static readonly string[] INDEXED_FOLDERS = { "Assets", "Packages" };
    
// ----- PUBLIC CALLS -----
    // Setup
    public UnityAssetIndex(string projectRoot) {
        ProjectRoot = Path.GetFullPath(projectRoot);
        foreach (var folder in INDEXED_FOLDERS) {
            var directory = Path.Combine(ProjectRoot, folder);
            if (Directory.Exists(directory)) indexFolder(directory, "*.meta");
        }
        var packageCache = Path.Combine(ProjectRoot, "Library", "PackageCache");
        if (Directory.Exists(packageCache)) {
            indexFolder(packageCache, "*.shader.meta");
            indexFolder(packageCache, "*.shadergraph.meta");
        }
    }
    public int Count => _pathsByGuid.Count;
    public IEnumerable<string> AllPaths => _pathsByGuid.Values;
    
    // Lookups
    public string GetPath(string guid) {
        return guid != null && _pathsByGuid.TryGetValue(guid.ToLowerInvariant(), out var path) ? path : null;
    }
    public string GetGuid(string assetPath) {
        return _guidsByPath.TryGetValue(Path.GetFullPath(assetPath), out var guid) ? guid : null;
    }
    // Animation-only models made for a model: Unity's "Model@Clip" naming, or import settings that point at it (copied avatar)
    public List<string> CompanionModels(string modelPath) {
        if (_companions == null) findCompanions();
        return _companions.TryGetValue(modelPath, out var list) ? list : new List<string>();
    }
    public ModelMeta GetModelMeta(string assetPath) {
        if (_modelMetas.TryGetValue(assetPath, out var cached)) return cached;
        var meta = new ModelMeta();
        var metaPath = assetPath + ".meta";
        if (File.Exists(metaPath)) {
            foreach (var value in UnityYaml.ParseMapping(File.ReadAllText(metaPath)).Values) {
                if (value is not Dictionary<string, object> importer) continue;
                readRecycleNames(importer, meta);
                readInternalIds(importer, meta);
                readExternalObjects(importer, meta);
                readClips(importer, meta);
            }
        }
        _modelMetas[assetPath] = meta;
        return meta;
    }
    
// ----- HELPERS -----
    // Indexing
    private void indexFolder(string directory, string pattern) {
        foreach (var metaPath in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)) {
            var guid = readGuid(metaPath);
            if (guid == null) continue;
            var assetPath = Path.GetFullPath(metaPath.Substring(0, metaPath.Length - 5));
            _pathsByGuid[guid] = assetPath;
            _guidsByPath[assetPath] = guid;
        }
    }
    private static string readGuid(string metaPath) {
        try {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(metaPath)) {
                if (line.StartsWith("guid:")) return line.Substring(5).Trim().ToLowerInvariant();
                if (++lineNumber > 6) break;
            }
        } catch (IOException) {
        }
        return null;
    }
    
    // Companions
    private void findCompanions() {
        _companions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var models = _pathsByGuid.Values.Where(AssetCollector.IsModel).ToList();
        var byStem = models.GroupBy(m => Path.GetFileNameWithoutExtension(m), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var model in models) {
            var stem = Path.GetFileNameWithoutExtension(model);
            var at = stem.IndexOf('@');
            if (at > 0 && byStem.TryGetValue(stem.Substring(0, at), out var owner)) addCompanion(owner, model);
            string metaText;
            try {
                metaText = File.ReadAllText(model + ".meta");
            } catch (IOException) {
                continue;
            }
            foreach (System.Text.RegularExpressions.Match match in META_GUID_REGEX.Matches(metaText)) {
                if (!_pathsByGuid.TryGetValue(match.Groups[1].Value.ToLowerInvariant(), out var referenced)) continue;
                if (referenced != model && AssetCollector.IsModel(referenced)) addCompanion(referenced, model);
            }
        }
    }
    private void addCompanion(string owner, string companion) {
        if (!_companions.TryGetValue(owner, out var list)) {
            list = new List<string>();
            _companions[owner] = list;
        }
        if (!list.Contains(companion)) list.Add(companion);
    }
    
    // Model meta
    private static void readRecycleNames(Dictionary<string, object> importer, ModelMeta meta) {
        var table = UnityYaml.GetMap(importer, "fileIDToRecycleName");
        if (table == null) return;
        foreach (var (key, value) in table) {
            if (long.TryParse(key, out var fileId) && value is string name) meta.Objects[fileId] = ((int)(fileId / 100000), name);
        }
    }
    private static void readInternalIds(Dictionary<string, object> importer, ModelMeta meta) {
        var table = UnityYaml.GetList(importer, "internalIDToNameTable");
        if (table == null) return;
        foreach (var item in table) {
            if (item is not Dictionary<string, object> entry) continue;
            var first = UnityYaml.GetMap(entry, "first");
            if (first == null) continue;
            foreach (var (classKey, idValue) in first) {
                if (int.TryParse(classKey, out var classId) && idValue is string idText && long.TryParse(idText, out var fileId)) {
                    meta.Objects[fileId] = (classId, UnityYaml.GetString(entry, "second"));
                }
            }
        }
    }
    private static void readClips(Dictionary<string, object> importer, ModelMeta meta) {
        var clips = UnityYaml.GetList(UnityYaml.GetMap(importer, "animations"), "clipAnimations");
        if (clips == null) return;
        foreach (var item in clips) {
            if (item is not Dictionary<string, object> clip) continue;
            meta.Clips.Add(new ModelClip {
                Name = UnityYaml.GetString(clip, "name"),
                TakeName = UnityYaml.GetString(clip, "takeName"),
                FirstFrame = UnityYaml.GetDouble(clip, "firstFrame"),
                LastFrame = UnityYaml.GetDouble(clip, "lastFrame"),
                Loop = UnityYaml.GetBool(clip, "loopTime")
            });
        }
    }
    private static void readExternalObjects(Dictionary<string, object> importer, ModelMeta meta) {
        var table = UnityYaml.GetList(importer, "externalObjects");
        if (table == null) return;
        foreach (var item in table) {
            if (item is not Dictionary<string, object> entry) continue;
            var first = UnityYaml.GetMap(entry, "first");
            if (!UnityYaml.GetString(first, "type").EndsWith("Material")) continue;
            var target = UnityYaml.GetRef(entry, "second");
            if (!target.IsNull) meta.MaterialRemaps[UnityYaml.GetString(first, "name")] = target;
        }
    }
}
#endif
