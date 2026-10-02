#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace UnityPrefabConverter;

public sealed class PrefabConverter {
// ----- VALUES -----
    public bool Overwrite {
        get => _overwrite;
        set {
            _overwrite = value;
            _animationConverter.Overwrite = value;
            _terrainConverter.Overwrite = value;
        }
    }
    private bool _overwrite = true;
    public enum LodModeKind { Quality = 0, Ranges = 1, Separate = 2 }
    public LodModeKind LodMode = LodModeKind.Quality;
    public bool LodCull = true;
    public bool ExternalData = true;
    public bool LodCrossfade;
    public float LodDistanceScale = 1;
    private float _lodBias = -1;
    private bool _materialTypeChanged;
    private readonly AnimationConverter _animationConverter;
    private readonly TerrainConverter _terrainConverter;
    private readonly ParticleConverter _particleConverter;
    private readonly Dictionary<string, Material> _particleMaterials = new();
    private readonly Dictionary<Mesh, (string ModelPath, string Name)> _meshModels = new();
    private readonly Dictionary<string, Dictionary<string, List<string>>> _materialOrders = new();
    private readonly Dictionary<Mesh, int[]> _slotSurfaces = new();
    private readonly Dictionary<string, Mesh> _sharedMeshes = new();
    private readonly Dictionary<string, string> _meshFiles = new();
    public event Action<string> Logged;
    public event Action<string> Traced;
    private readonly UnityAssetIndex _index;
    private readonly string _prefabsDir;
    private readonly OutputPaths _paths;
    private readonly MaterialConverter _materialConverter;
    private readonly string _godotOutputDir;
    private readonly Dictionary<string, ConvertedAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mesh> _meshes = new();
    private readonly Dictionary<string, Material> _materials = new();
    private readonly HashSet<string> _loggedMessages = new();
    private readonly Dictionary<string, (string Path, PackedScene Scene)> _modelScenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node> _modelInstances = new(StringComparer.OrdinalIgnoreCase);
    private int _warningCount;
    private const int CLASS_GAME_OBJECT = 1;
    private const int CLASS_MESH_ASSET = 43;
    private const int CLASS_TRANSFORM = 4;
    private const int CLASS_CAMERA = 20;
    private const int CLASS_MESH_RENDERER = 23;
    private const int CLASS_MESH_FILTER = 33;
    private const int CLASS_RIGIDBODY = 54;
    private const int CLASS_MESH_COLLIDER = 64;
    private const int CLASS_BOX_COLLIDER = 65;
    private const int CLASS_LIGHT = 108;
    private const int CLASS_MONO_BEHAVIOUR = 114;
    private const int CLASS_SPHERE_COLLIDER = 135;
    private const int CLASS_CAPSULE_COLLIDER = 136;
    private const int CLASS_SKINNED_MESH_RENDERER = 137;
    private const int CLASS_RECT_TRANSFORM = 224;
    private const int CLASS_PREFAB_INSTANCE = 1001;
    private const int CLASS_ANIMATOR = 95;
    private const int CLASS_ANIMATION = 111;
    private const int CLASS_RENDER_SETTINGS = 104;
    private const int CLASS_SCENE_ROOTS = 1660057539;
    private const int CLASS_TERRAIN = 218;
    private const int CLASS_TERRAIN_COLLIDER = 154;
    private const int CLASS_LOD_GROUP = 205;
    private const int CLASS_PARTICLE_SYSTEM = 198;
    private const int CLASS_PARTICLE_SYSTEM_RENDERER = 199;
    // Unity's LOD heights are fractions of a 60° view: distance = size / (2 * tan(30°) * height)
    private const float LOD_VIEW_FACTOR = 1.1547f;
    // Neighbouring levels cross-fade over this share of the switch distance on each side
    private const float LOD_FADE_FRACTION = 0.1f;
    private const int CLASS_QUALITY_SETTINGS = 47;
    private static readonly string[] INVALID_NODE_NAME_CHARACTERS = { ".", ":", "@", "/", "\"", "%" };
    private const string LOD_GROUP = "unity_lod";
    private const string LOD_LEVELS_META = "unity_lod_levels";
    private const string LOD_COUNT_META = "unity_lod_count";
    private const int MAX_HIERARCHY_DEPTH = 256;
    private const string EXTRACTED_MESH_SUFFIX = "_Meshes";
    private const string EXTERNAL_DATA_SUFFIX = "_Data";
    // Embedded data above this many bytes goes to its own binary file; a text scene writes every byte as a number, about four times the size
    private const int EXTERNAL_DATA_THRESHOLD = 4096;
    private const string REMAPPED_MODEL_SUFFIX = "_Remapped.tscn";
    private const string INHERITED_BASE_NAME = "__InheritedBase";
    
    private sealed class ConvertedAsset {
        public string ScenePath;
        public PackedScene Scene;
        public bool IsModel;
        public NodePath SingleMeshPath;
        public readonly Dictionary<(string Guid, long FileId), (NodePath Path, int ClassId)> Objects = new();
    }
    private sealed class PrefabContext {
        public string Guid;
        public string FileName;
        public string UnityPath;
        public Node3D Root;
        public bool RootIsInstance;
        public bool IsScene;
        public readonly Dictionary<long, UnityDocument> Documents = new();
        public readonly Dictionary<long, List<UnityDocument>> Components = new();
        public readonly Dictionary<long, Node> Nodes = new();
        public readonly List<(Node Node, ConvertedAsset Source)> Instances = new();
        public readonly HashSet<Node> Editable = new();
        public readonly List<StaticBody3D> StaticBodies = new();
        public readonly HashSet<long> Built = new();
        public readonly Dictionary<string, BoneAttachment3D> BoneAttachments = new();
        public readonly Dictionary<long, List<UnityDocument>> StrippedByInstance = new();
        public readonly Dictionary<long, List<UnityDocument>> AdditionsByInstance = new();
        public readonly HashSet<long> HiddenRenderers = new();
        public List<LodGroup> LodGroups = new();
        public readonly List<Node> Detached = new();
        public int Depth;
    }
    private sealed class TransformState {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }
    
// ----- PUBLIC CALLS -----
    // Setup
    public PrefabConverter(UnityAssetIndex index, OutputPaths paths, string prefabsDir) {
        _index = index;
        _paths = paths;
        _prefabsDir = Path.GetFullPath(prefabsDir);
        _godotOutputDir = paths.OutputDir;
        _materialConverter = new MaterialConverter(index, paths);
        _animationConverter = new AnimationConverter(index, paths, modelInstance);
        _animationConverter.Logged += relayLog;
        _animationConverter.Traced += trace;
        _terrainConverter = new TerrainConverter(index, paths, _materialConverter);
        _terrainConverter.Logged += relayLog;
        _terrainConverter.Traced += trace;
        _materialConverter.Logged += relayLog;
        _particleConverter = new ParticleConverter(particleMaterial, resolveMesh, _materialConverter.SpriteStrip, paths.OutputDir);
        _particleConverter.Logged += message => note(message);
    }
    public int WarningCount => _warningCount;
    
    // Conversion
    public IReadOnlyDictionary<string, string> UnconvertedShaders => _materialConverter.UnconvertedShaders;
    public List<string> FindSources(bool prefabs, bool scenes, bool animations) {
        var patterns = new List<string>();
        if (prefabs) patterns.Add("*.prefab");
        if (scenes) patterns.Add("*.unity");
        if (animations) patterns.AddRange(new[] { "*.anim", "*.controller", "*.overrideController" });
        return patterns.SelectMany(p => Directory.EnumerateFiles(_prefabsDir, p, SearchOption.AllDirectories)).Select(Path.GetFullPath).OrderBy(p => p).ToList();
    }
    public string ConvertAsset(string unityPath) {
        unityPath = Path.GetFullPath(unityPath);
        var extension = Path.GetExtension(unityPath).ToLowerInvariant();
        if (extension == ".anim") {
            var guid = _index.GetGuid(unityPath);
            return _animationConverter.ConvertClip(new UnityRef(7400000, guid))?.ResourcePath;
        }
        if (extension == ".controller" || extension == ".overridecontroller") {
            return _animationConverter.ConvertController(unityPath).Library?.ResourcePath;
        }
        return convertPrefab(unityPath)?.ScenePath;
    }
    public void Cleanup() {
        foreach (var instance in _modelInstances.Values) instance?.Free();
        _modelInstances.Clear();
        _modelScenes.Clear();
        _assets.Clear();
    }
    
// ----- HELPERS -----
    // Prefab files
    private ConvertedAsset convertPrefab(string unityPath) {
        if (_assets.TryGetValue(unityPath, out var cached)) return cached;
        if (!_inProgress.Add(unityPath)) {
            warn($"Circular prefab reference through {Path.GetFileName(unityPath)}");
            return null;
        }
        ConvertedAsset asset = null;
        try {
            asset = buildPrefab(unityPath);
        } catch (Exception exception) {
            warn($"Failed to convert {Path.GetFileName(unityPath)}: {exception.Message}");
        } finally {
            _inProgress.Remove(unityPath);
        }
        _assets[unityPath] = asset;
        return asset;
    }
    // LOD groups become quality levels or visibility ranges in one scene, or with LodModeKind.Separate one prefab scene per level (NAME_LOD1.tscn and up)
    private ConvertedAsset buildPrefab(string unityPath) {
        var fileName = Path.GetFileName(unityPath);
        var isScene = unityPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        trace($"Reading {fileName}");
        var documents = UnityYaml.ParseFile(unityPath);
        trace($"Read {documents.Count} objects");
        var groups = lodGroups(documents);
        var levels = groups.Count == 0 ? 1 : groups.Max(g => g.Levels.Count);
        var separate = LodMode == LodModeKind.Separate && !isScene && levels > 1;
        var asset = buildLevel(unityPath, documents, groups, separate ? 0 : -1);
        if (!separate || asset == null) return asset;
        for (var level = 1; level < levels; level++) buildLevel(unityPath, documents, groups, level);
        return asset;
    }
    // Level -1 keeps every level, switched by visibility range
    private ConvertedAsset buildLevel(string unityPath, List<UnityDocument> documents, List<LodGroup> groups, int level) {
        var context = new PrefabContext {
            Guid = _index.GetGuid(unityPath) ?? "",
            FileName = Path.GetFileName(unityPath),
            UnityPath = unityPath,
            IsScene = unityPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
        };
        if (level >= 0) hideOtherLevels(context, groups, level);
        else context.LodGroups = groups;
        foreach (var document in documents) {
            context.Documents[document.FileId] = document;
            if (document.Stripped || document.ClassId == CLASS_GAME_OBJECT) continue;
            var owner = UnityYaml.GetRef(document.Body, "m_GameObject");
            if (!owner.IsNull) listFor(context.Components, owner.FileId).Add(document);
        }
        indexInstances(context);
        context.Root = context.IsScene ? buildSceneRoot(context, unityPath) : buildRoot(context);
        if (context.Root == null) {
            warn($"{context.FileName}: no root object found");
            return null;
        }
        var sceneName = UnityYaml.NodeName(Path.GetFileNameWithoutExtension(unityPath)) + (level > 0 ? $"_LOD{level}" : "");
        var sceneRoot = context.RootIsInstance ? wrapForInheritance(context.Root) : context.Root;
        if (!context.RootIsInstance) context.Root.Name = sceneName;
        trace("Finishing scene tree");
        removeHiddenRenderers(context);
        if (LodMode == LodModeKind.Ranges || context.IsScene && LodMode == LodModeKind.Separate) applyVisibilityRanges(context);
        else applyQualityLevels(context);
        mergeCompoundColliders(context);
        assignOwners(sceneRoot, sceneRoot);
        foreach (var instance in context.Editable) {
            if (instance != sceneRoot) sceneRoot.SetEditableInstance(instance, true);
        }
        var scenePath = scenePathFor(unityPath);
        var asset = new ConvertedAsset { ScenePath = level > 0 ? $"{scenePath.GetBaseName()}_LOD{level}.tscn" : scenePath };
        recordPaths(context, asset);
        saveScene(sceneRoot, asset, context.RootIsInstance ? sceneName : null);
        sceneRoot.Free();
        foreach (var node in context.Detached) node.Free();
        note($"{context.FileName} -> {asset.ScenePath}");
        return asset;
    }
    // Each level holds the screen height it's used down to and the renderer IDs it shows
    private sealed record LodGroup(long GameObject, float Size, List<(float Height, HashSet<long> Renderers)> Levels);
    private static List<LodGroup> lodGroups(List<UnityDocument> documents) {
        var groups = new List<LodGroup>();
        foreach (var group in documents.Where(d => d.ClassId == CLASS_LOD_GROUP && !d.Stripped)) {
            var levels = new List<(float, HashSet<long>)>();
            foreach (var lod in (UnityYaml.GetList(group.Body, "m_LODs") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
                var renderers = (UnityYaml.GetList(lod, "renderers") ?? new List<object>()).OfType<Dictionary<string, object>>();
                levels.Add(((float)UnityYaml.GetDouble(lod, "screenRelativeHeight"), renderers.Select(r => UnityYaml.GetRef(r, "renderer").FileId).Where(id => id != 0).ToHashSet()));
            }
            if (levels.Count > 1) groups.Add(new LodGroup(UnityYaml.GetRef(group.Body, "m_GameObject").FileId, (float)UnityYaml.GetDouble(group.Body, "m_Size", 1), levels));
        }
        return groups;
    }
    // Groups with fewer levels keep showing their last one
    private static void hideOtherLevels(PrefabContext context, List<LodGroup> groups, int level) {
        foreach (var group in groups) {
            var shown = group.Levels[Math.Min(level, group.Levels.Count - 1)].Renderers;
            foreach (var (_, renderers) in group.Levels) {
                if (renderers != shown) context.HiddenRenderers.UnionWith(renderers.Where(id => !shown.Contains(id)));
            }
        }
    }
    // Level i shows between the distances where level i-1 and level i reach their screen heights; past the last one nothing shows, as in Unity
    private void applyVisibilityRanges(PrefabContext context) {
        var distanceScale = lodBias() * LodDistanceScale;
        foreach (var group in context.LodGroups) {
            if (context.Nodes.GetValueOrDefault(group.GameObject) is not Node3D owner) continue;
            var scale = worldScale(owner);
            var size = group.Size * Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
            var ranges = new Dictionary<long, (float Begin, float End)>();
            var begin = 0f;
            foreach (var (height, renderers) in group.Levels) {
                var end = size * distanceScale / (LOD_VIEW_FACTOR * Math.Max(height, 0.0001f));
                foreach (var id in renderers) ranges[id] = ranges.TryGetValue(id, out var range) ? (Math.Min(range.Begin, begin), Math.Max(range.End, end)) : (begin, end);
                begin = end;
            }
            foreach (var (id, (rangeBegin, rangeEnd)) in ranges) {
                if (context.Nodes.GetValueOrDefault(id) is not Node3D target) continue;
                var geometries = target is GeometryInstance3D single ? new List<GeometryInstance3D> { single } : target.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>().ToList();
                foreach (var geometry in geometries) {
                    geometry.VisibilityRangeBegin = rangeBegin;
                    geometry.VisibilityRangeEnd = rangeEnd;
                    if (!LodCrossfade) continue;
                    geometry.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self;
                    geometry.VisibilityRangeBeginMargin = rangeBegin * LOD_FADE_FRACTION;
                    geometry.VisibilityRangeEndMargin = rangeEnd * LOD_FADE_FRACTION;
                }
                markInstanceEditable(context, target);
            }
        }
    }
    // Every Unity level stays in the scene but only the most detailed one shows; Godot's own mesh LOD handles distance.
    // Each mesh records which levels it belongs to, so UnityLodQuality can show a coarser level on slower machines
    private void applyQualityLevels(PrefabContext context) {
        var distanceScale = lodBias() * LodDistanceScale;
        foreach (var group in context.LodGroups) {
            if (context.Nodes.GetValueOrDefault(group.GameObject) is not Node3D owner) continue;
            var scale = worldScale(owner);
            var size = group.Size * Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
            var cull = size * distanceScale / (LOD_VIEW_FACTOR * Math.Max(group.Levels[^1].Height, 0.0001f));
            var levels = new Dictionary<long, List<int>>();
            for (var i = 0; i < group.Levels.Count; i++) {
                foreach (var id in group.Levels[i].Renderers) listFor(levels, id).Add(i);
            }
            foreach (var (id, shownAt) in levels) {
                if (context.Nodes.GetValueOrDefault(id) is not Node3D target) {
                    trace($"LOD group on '{owner.Name}': the renderer for level {string.Join("/", shownAt)} (id {id}) wasn't found among the converted objects");
                    continue;
                }
                var geometries = target is GeometryInstance3D single ? new List<GeometryInstance3D> { single } : target.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>().ToList();
                trace($"LOD group on '{owner.Name}': level {string.Join("/", shownAt)} -> {string.Join(", ", geometries.Select(g => $"{g.Name}{(g.Visible ? "" : " (hidden in Unity)")}"))}{(geometries.Count == 0 ? $"'{target.Name}', which has no mesh" : "")}");
                foreach (var geometry in geometries.Where(g => g.Visible)) {
                    geometry.Visible = shownAt.Contains(0);
                    geometry.SetMeta(LOD_LEVELS_META, shownAt.ToArray());
                    geometry.SetMeta(LOD_COUNT_META, group.Levels.Count);
                    geometry.AddToGroup(LOD_GROUP, true);
                    if (!LodCull) continue;
                    geometry.VisibilityRangeEnd = cull;
                    geometry.VisibilityRangeEndMargin = cull * LOD_FADE_FRACTION;
                    if (LodCrossfade) geometry.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self;
                }
                markInstanceEditable(context, target);
            }
        }
    }
    // Unity multiplies LOD distances by the quality level's LOD bias; desktop builds default to Ultra, whose bias is 2
    private float lodBias() {
        if (_lodBias >= 0) return _lodBias;
        _lodBias = 1;
        var path = Path.Combine(_index.ProjectRoot, "ProjectSettings", "QualitySettings.asset");
        try {
            var settings = File.Exists(path) ? UnityYaml.ParseFile(path).FirstOrDefault(d => d.ClassId == CLASS_QUALITY_SETTINGS)?.Body : null;
            var levels = UnityYaml.GetList(settings, "m_QualitySettings")?.OfType<Dictionary<string, object>>().ToList();
            if (levels == null || levels.Count == 0) return _lodBias;
            var platforms = UnityYaml.GetMap(settings, "m_PerPlatformDefaultQuality");
            var current = (int)UnityYaml.GetLong(platforms, "Standalone", UnityYaml.GetLong(settings, "m_CurrentQuality"));
            var level = levels[Math.Clamp(current, 0, levels.Count - 1)];
            _lodBias = (float)Math.Max(0.01, UnityYaml.GetDouble(level, "lodBias", 1));
            note($"LOD distances use LOD bias {_lodBias} from Unity quality level '{UnityYaml.GetString(level, "name")}'");
        } catch (Exception exception) {
            note($"Could not read Unity's quality settings ({exception.Message}); LOD bias 1 is used");
        }
        return _lodBias;
    }
    private static Vector3 worldScale(Node3D node) {
        var scale = Vector3.One;
        for (Node current = node; current is Node3D spatial; current = current.GetParent()) scale *= spatial.Transform.Basis.Scale;
        return scale;
    }
    // Changes to nodes inside a nested prefab or model only save when that instance is editable
    private static void markInstanceEditable(PrefabContext context, Node node) {
        foreach (var (instance, _) in context.Instances) {
            if (instance != node && instance.IsAncestorOf(node)) context.Editable.Add(instance);
        }
    }
    // Objects built here are taken out; ones inside a nested prefab or model can't be removed, so their meshes are hidden
    private static void removeHiddenRenderers(PrefabContext context) {
        foreach (var id in context.HiddenRenderers) {
            if (!context.Documents.TryGetValue(id, out var renderer)) continue;
            var owner = renderer.Stripped ? null : context.Nodes.GetValueOrDefault(UnityYaml.GetRef(renderer.Body, "m_GameObject").FileId);
            if (owner != null && owner != context.Root && owner.GetParent() != null && !insideInstance(context, owner)) {
                owner.GetParent().RemoveChild(owner);
                context.Detached.Add(owner);
            } else if (context.Nodes.GetValueOrDefault(id) is Node3D meshNode) {
                meshNode.Visible = false;
                markInstanceEditable(context, meshNode);
            }
        }
    }
    private static bool insideInstance(PrefabContext context, Node node) {
        return context.Instances.Any(i => i.Node == node || i.Node.IsAncestorOf(node));
    }
    private Node3D buildRoot(PrefabContext context) {
        foreach (var document in context.Documents.Values) {
            if (document.Stripped || !isTransform(document.ClassId)) continue;
            if (UnityYaml.GetRef(document.Body, "m_Father").IsNull) return buildGameObject(context, document);
        }
        foreach (var document in context.Documents.Values) {
            if (document.ClassId != CLASS_PREFAB_INSTANCE) continue;
            var modification = UnityYaml.GetMap(document.Body, "m_Modification");
            if (!UnityYaml.GetRef(modification, "m_TransformParent").IsNull) continue;
            context.RootIsInstance = true;
            return buildInstance(context, document);
        }
        return null;
    }
    private void saveScene(Node root, ConvertedAsset asset, string inheritedName = null) {
        if (!Overwrite && Godot.FileAccess.FileExists(asset.ScenePath)) {
            // Scenes loaded earlier still hold the material objects that were replaced, so they're read again
            asset.Scene = ResourceLoader.Load<PackedScene>(asset.ScenePath, null, _materialTypeChanged ? ResourceLoader.CacheMode.ReplaceDeep : ResourceLoader.CacheMode.Reuse);
            return;
        }
        ensureDirectory(asset.ScenePath.GetBaseDir());
        trace($"Saving {asset.ScenePath}");
        if (ExternalData) externalizeData(root, asset.ScenePath);
        var packed = new PackedScene();
        var error = packed.Pack(root);
        if (error != Error.Ok) {
            warn($"Could not pack {asset.ScenePath}: {error}");
            return;
        }
        error = ResourceSaver.Save(packed, asset.ScenePath);
        if (error != Error.Ok) {
            warn($"Could not save {asset.ScenePath}: {error}");
            return;
        }
        if (inheritedName != null) rewriteAsInherited(asset.ScenePath, inheritedName);
        asset.Scene = ResourceLoader.Load<PackedScene>(asset.ScenePath, null, ResourceLoader.CacheMode.Replace);
    }
    // Large meshes, collision shapes and images built during conversion are saved as .res files next to the scene, so the .tscn stays small and loads fast
    private void externalizeData(Node root, string scenePath) {
        var folder = scenePath.GetBaseDir() + "/" + scenePath.GetFile().GetBaseName() + EXTERNAL_DATA_SUFFIX;
        var visited = new HashSet<ulong>();
        var names = new HashSet<string>();
        var saved = 0;
        void visit(Variant value) {
            if (value.VariantType == Variant.Type.Array) {
                foreach (var item in value.AsGodotArray()) visit(item);
                return;
            }
            if (value.VariantType != Variant.Type.Object || value.AsGodotObject() is not Resource resource || !visited.Add(resource.GetInstanceId())) return;
            if (!isBuiltIn(resource)) return;
            var size = dataSize(resource);
            if (size < 0) {
                foreach (var property in resource.GetPropertyList()) {
                    if ((property["usage"].AsInt64() & (long)PropertyUsageFlags.Storage) == 0) continue;
                    var type = (Variant.Type)property["type"].AsInt64();
                    if (type == Variant.Type.Object || type == Variant.Type.Array) visit(resource.Get(property["name"].AsStringName()));
                }
                return;
            }
            if (size < EXTERNAL_DATA_THRESHOLD) return;
            var name = sanitizeFileName(string.IsNullOrEmpty(resource.ResourceName) ? resource.GetClass() : resource.ResourceName);
            var unique = name;
            for (var i = 2; !names.Add(unique); i++) unique = $"{name}_{i}";
            var path = $"{folder}/{unique}.res";
            ensureDirectory(folder);
            resource.TakeOverPath(path);
            if (ResourceSaver.Save(resource, path, ResourceSaver.SaverFlags.Compress) == Error.Ok) saved++;
            else warn($"Could not save {path}");
        }
        void walk(Node node) {
            if (node == root || node.Owner == root) {
                if (node is MeshInstance3D meshNode && meshNode.Mesh != null && meshNode.Mesh.ResourcePath.Contains("::")) meshNode.Mesh = sharedMeshCopy(meshNode.Mesh);
                foreach (var property in node.GetPropertyList()) {
                    if ((property["usage"].AsInt64() & (long)PropertyUsageFlags.Storage) == 0) continue;
                    var type = (Variant.Type)property["type"].AsInt64();
                    if (type == Variant.Type.Object || type == Variant.Type.Array) visit(node.Get(property["name"].AsStringName()));
                }
            }
            foreach (var child in node.GetChildren()) walk(child);
        }
        walk(root);
        if (saved > 0) trace($"Saved {saved} large resource(s) to {folder}");
    }
    // Two meshes in one model can carry the same name (colour variants, LOD levels); each still needs a file of its own, or the last one saved would replace the others
    private string meshFilePath(string basePath, Mesh mesh) {
        var key = string.IsNullOrEmpty(mesh.ResourcePath) ? mesh.GetInstanceId().ToString() : mesh.ResourcePath;
        var path = basePath + ".res";
        for (var i = 2; _meshFiles.TryGetValue(path, out var owner) && owner != key; i++) path = $"{basePath}_{i}.res";
        _meshFiles[path] = key;
        return path;
    }
    // A mesh that lives inside an imported model would be copied into every scene that uses it directly; one copy is saved next to the model and shared
    private Mesh sharedMeshCopy(Mesh mesh) {
        var source = mesh.ResourcePath;
        if (_sharedMeshes.TryGetValue(source, out var cached)) return cached;
        var file = source.Substring(0, source.IndexOf("::", StringComparison.Ordinal));
        Mesh copy = mesh;
        if (dataSize(mesh) >= EXTERNAL_DATA_THRESHOLD && !file.StartsWith("res://.godot")) {
            var name = sanitizeFileName(string.IsNullOrEmpty(mesh.ResourceName) ? source.Substring(source.IndexOf("::", StringComparison.Ordinal) + 2) : mesh.ResourceName);
            var path = meshFilePath(file.GetBaseName() + EXTRACTED_MESH_SUFFIX + "/" + name, mesh);
            copy = (!Overwrite && Godot.FileAccess.FileExists(path) ? ResourceLoader.Load<Mesh>(path) : saveMeshCopy(mesh, path)) ?? mesh;
            if (_slotSurfaces.TryGetValue(mesh, out var slots)) _slotSurfaces[copy] = slots;
        }
        _sharedMeshes[source] = copy;
        return copy;
    }
    // Data inside an imported model or another scene has a path of the form file::id and stays where it is
    private static bool isBuiltIn(Resource resource) {
        return string.IsNullOrEmpty(resource.ResourcePath);
    }
    // -1 for resources that only hold other resources
    private static long dataSize(Resource resource) {
        switch (resource) {
            case ImageTexture texture:
                return texture.GetImage()?.GetData().Length ?? 0;
            case Image image:
                return image.GetData().Length;
            case ArrayMesh mesh:
                long bytes = 0;
                for (var i = 0; i < mesh.GetSurfaceCount(); i++) bytes += mesh.SurfaceGetArrayLen(i) * 32L + mesh.SurfaceGetArrayIndexLen(i) * 4L;
                return bytes;
            case HeightMapShape3D heightMap:
                return heightMap.MapData.Length * 4L;
            case ConcavePolygonShape3D concave:
                return concave.Data.Length * 12L;
            case ConvexPolygonShape3D convex:
                return convex.Points.Length * 12L;
            case Animation animation:
                long keys = 0;
                for (var i = 0; i < animation.GetTrackCount(); i++) keys += animation.TrackGetKeyCount(i);
                return keys * 20L;
            default:
                return -1;
        }
    }
    private void recordPaths(PrefabContext context, ConvertedAsset asset) {
        foreach (var (fileId, node) in context.Nodes) {
            if (node != context.Root && !context.Root.IsAncestorOf(node)) continue;
            var classId = context.Documents.TryGetValue(fileId, out var document) ? document.ClassId : 0;
            asset.Objects[(context.Guid, fileId)] = (context.Root.GetPathTo(node), classId);
        }
        foreach (var (instance, source) in context.Instances) {
            if (instance != context.Root && !context.Root.IsAncestorOf(instance)) continue;
            var prefix = context.Root.GetPathTo(instance).ToString();
            foreach (var (key, value) in source.Objects) {
                if (asset.Objects.ContainsKey(key)) continue;
                asset.Objects[key] = (new NodePath(joinNodePath(prefix, value.Path.ToString())), value.ClassId);
            }
        }
    }
    
    // Game objects
    private Node3D buildGameObject(PrefabContext context, UnityDocument transform) {
        if (!enterObject(context, transform.FileId)) return new Node3D { Name = "SkippedLoop" };
        try {
            return buildGameObjectNode(context, transform);
        } finally {
            context.Depth--;
        }
    }
    // Guards against hierarchies that reference themselves, which would otherwise recurse until Godot closes
    private bool enterObject(PrefabContext context, long fileId) {
        if (!context.Built.Add(fileId)) {
            warn($"{context.FileName}: object {fileId} is referenced more than once in the hierarchy; skipped the repeat");
            return false;
        }
        if (context.Depth >= MAX_HIERARCHY_DEPTH) {
            warn($"{context.FileName}: hierarchy deeper than {MAX_HIERARCHY_DEPTH} levels; stopped there");
            return false;
        }
        context.Depth++;
        return true;
    }
    private Node3D buildGameObjectNode(PrefabContext context, UnityDocument transform) {
        var gameObjectRef = UnityYaml.GetRef(transform.Body, "m_GameObject");
        context.Documents.TryGetValue(gameObjectRef.FileId, out var gameObject);
        var components = context.Components.TryGetValue(gameObjectRef.FileId, out var list) ? list : new List<UnityDocument>();
        var name = UnityYaml.GetString(gameObject?.Body, "m_Name", "GameObject");
        trace($"Building '{name}' ({components.Count} components)");
        var animatedModel = findAnimatedModel(context, transform, components);
        if (animatedModel != null) {
            var rebuilt = buildAnimatedModel(context, transform, gameObject, components, animatedModel, name);
            if (rebuilt != null) return rebuilt;
        }
        var node = createGameObjectNode(components);
        node.Name = string.IsNullOrWhiteSpace(name) ? "GameObject" : UnityYaml.NodeName(name);
        node.Transform = readUnityTransform(transform.Body);
        if (!UnityYaml.GetBool(gameObject?.Body, "m_IsActive", true)) setActive(node, false);
        context.Nodes[gameObjectRef.FileId] = node;
        context.Nodes[transform.FileId] = node;
        attachComponents(context, node, components, true);
        foreach (var child in UnityYaml.GetList(transform.Body, "m_Children") ?? new List<object>()) {
            var childRef = UnityYaml.ToRef(child);
            if (!context.Documents.TryGetValue(childRef.FileId, out var childDocument)) continue;
            if (!childDocument.Stripped) {
                node.AddChild(buildGameObject(context, childDocument), true);
                continue;
            }
            var instanceRef = UnityYaml.GetRef(childDocument.Body, "m_PrefabInstance");
            if (context.Documents.TryGetValue(instanceRef.FileId, out var instance)) node.AddChild(buildInstance(context, instance), true);
        }
        return node;
    }
    private static Node3D createGameObjectNode(List<UnityDocument> components) {
        if (components.Any(c => c.ClassId == CLASS_RIGIDBODY)) return new RigidBody3D();
        var renderer = components.FirstOrDefault(c => c.ClassId == CLASS_MESH_RENDERER || c.ClassId == CLASS_SKINNED_MESH_RENDERER);
        if (renderer != null && UnityYaml.GetBool(renderer.Body, "m_Enabled", true)) return new MeshInstance3D();
        return new Node3D();
    }
    private void attachComponents(PrefabContext context, Node3D node, List<UnityDocument> components, bool primary) {
        var meshFilter = components.FirstOrDefault(c => c.ClassId == CLASS_MESH_FILTER);
        var scripts = new List<string>();
        MeshInstance3D meshNode = null;
        foreach (var component in components) {
            context.Nodes[component.FileId] = node;
            switch (component.ClassId) {
                case CLASS_MESH_RENDERER:
                case CLASS_SKINNED_MESH_RENDERER:
                    meshNode = primary && meshNode == null && node is MeshInstance3D inline ? inline : addChild(node, new MeshInstance3D { Name = "Mesh" });
                    var skinned = component.ClassId == CLASS_SKINNED_MESH_RENDERER;
                    var meshRef = UnityYaml.GetRef(skinned ? component.Body : meshFilter?.Body, "m_Mesh");
                    setupMesh(context, meshNode, meshRef, component, node.Name);
                    context.Nodes[component.FileId] = meshNode;
                    if (meshFilter != null) context.Nodes[meshFilter.FileId] = meshNode;
                    break;
                case CLASS_RIGIDBODY:
                    if (node is RigidBody3D body) setupRigidbody(body, component.Body);
                    else warn($"{context.FileName}: Rigidbody added to a nested prefab object '{node.Name}' was skipped");
                    break;
                case CLASS_LIGHT:
                    addChild(node, createLight(component.Body));
                    break;
                case CLASS_ANIMATOR:
                    attachAnimator(context, node, component);
                    break;
                case CLASS_ANIMATION:
                    attachLegacyAnimation(node, component);
                    break;
                case CLASS_TERRAIN:
                    attachTerrain(context, node, component);
                    break;
                case CLASS_TERRAIN_COLLIDER:
                    attachTerrainCollider(node, component);
                    break;
                case CLASS_PARTICLE_SYSTEM:
                    var particleRenderer = components.FirstOrDefault(c => c.ClassId == CLASS_PARTICLE_SYSTEM_RENDERER);
                    addChild(node, _particleConverter.Convert(component.Body, particleRenderer?.Body, node.Name));
                    break;
                case CLASS_PARTICLE_SYSTEM_RENDERER:
                    break;
                case CLASS_CAMERA:
                    addChild(node, createCamera(component.Body));
                    break;
                case CLASS_MONO_BEHAVIOUR:
                    var scriptPath = _index.GetPath(UnityYaml.GetRef(component.Body, "m_Script").Guid);
                    if (scriptPath != null) scripts.Add(Path.GetFileNameWithoutExtension(scriptPath));
                    break;
                case CLASS_TRANSFORM:
                case CLASS_RECT_TRANSFORM:
                case CLASS_MESH_FILTER:
                case CLASS_BOX_COLLIDER:
                case CLASS_SPHERE_COLLIDER:
                case CLASS_CAPSULE_COLLIDER:
                case CLASS_MESH_COLLIDER:
                case CLASS_LOD_GROUP:
                    break;
                default:
                    var typeName = component.TypeName ?? $"class {component.ClassId}";
                    warnOnce($"unsupported:{typeName}", $"Unsupported component type skipped: {typeName}");
                    break;
            }
        }
        attachColliders(context, node, components, meshNode);
        if (scripts.Count > 0) {
            node.SetMeta("unity_scripts", scripts.ToArray());
            note($"{context.FileName}: '{node.Name}' had scripts {string.Join(", ", scripts)} (stored in node metadata 'unity_scripts')");
        }
    }
    private static void setActive(Node3D node, bool active) {
        node.Visible = active;
        node.ProcessMode = active ? Node.ProcessModeEnum.Inherit : Node.ProcessModeEnum.Disabled;
    }
    
    // Meshes
    private void setupMesh(PrefabContext context, MeshInstance3D meshNode, UnityRef meshRef, UnityDocument renderer, string gameObjectName) {
        trace($"Mesh {meshRef.FileId} on '{gameObjectName}'");
        meshNode.Mesh = resolveMesh(meshRef, gameObjectName);
        var materials = UnityYaml.GetList(renderer.Body, "m_Materials");
        if (materials != null && meshNode.Mesh != null) {
            var surfaces = meshNode.Mesh.GetSurfaceCount();
            for (var i = 0; i < Math.Min(materials.Count, surfaces); i++) {
                var material = resolveMaterial(UnityYaml.ToRef(materials[i]));
                if (material != null) meshNode.SetSurfaceOverrideMaterial(surfaceForSlot(meshNode.Mesh, i), material);
            }
        }
        if (UnityYaml.GetLong(renderer.Body, "m_CastShadows", 1) == 0) meshNode.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        if (!UnityYaml.GetBool(renderer.Body, "m_Enabled", true)) meshNode.Visible = false;
        if (renderer.ClassId == CLASS_SKINNED_MESH_RENDERER) note($"{context.FileName}: '{gameObjectName}' is skinned; converted as a static mesh");
    }
    private Mesh resolveMesh(UnityRef meshRef, string fallbackName) {
        if (meshRef.IsNull) return null;
        var key = $"{meshRef.Guid}:{meshRef.FileId}";
        if (_meshes.TryGetValue(key, out var cached)) return cached;
        Mesh mesh = null;
        var unityPath = _index.GetPath(meshRef.Guid);
        if (meshRef.Guid == UnityAssetIndex.BUILTIN_EXTRA_GUID) {
            mesh = builtinMesh(meshRef.FileId);
        } else if (unityPath == null) {
            warn($"Mesh with guid {meshRef.Guid} not found in the Unity project");
        } else if (!isModelFile(unityPath)) {
            warnOnce($"mesh-asset:{unityPath}", $"Mesh asset {Path.GetFileName(unityPath)} is not a model file and was skipped");
        } else {
            mesh = extractMesh(unityPath, meshRef.FileId, fallbackName);
        }
        _meshes[key] = mesh;
        return mesh;
    }
    private Mesh extractMesh(string unityPath, long fileId, string fallbackName) {
        var instance = modelInstance(unityPath);
        if (instance == null) return null;
        var godotPath = modelScene(unityPath).Path;
        trace($"Extracting mesh {fileId} from {godotPath}");
        var meshes = new List<MeshInstance3D>();
        collectNodes(instance, meshes);
        meshes.RemoveAll(m => m.Mesh == null);
        var meta = _index.GetModelMeta(unityPath);
        var metaName = meta.Objects.TryGetValue(fileId, out var info) ? info.Name : null;
        var picked = meshes.FirstOrDefault(m => matchesMesh(m, metaName)) ?? meshes.FirstOrDefault(m => matchesMesh(m, fallbackName));
        picked ??= meshes.FirstOrDefault(m => plainNamesMatch(m.Name, metaName) || plainNamesMatch(m.Mesh.ResourceName, metaName));
        if (picked == null && meshes.Count == 1) picked = meshes[0];
        if (picked == null) {
            // Godot renames a model's single top object after the file, so its mesh matches no Unity name; it's the one left once the others are accounted for
            var others = meta.Objects.Where(o => o.Key != fileId && o.Value.ClassId == CLASS_MESH_ASSET).Select(o => o.Value.Name).ToList();
            var unclaimed = meshes.Where(m => !others.Any(name => matchesMesh(m, name))).ToList();
            if (unclaimed.Count == 1) picked = unclaimed[0];
        }
        Mesh mesh = null;
        if (picked == null) {
            var found = string.Join(", ", meshes.Take(24).Select(m => $"{m.Name} [{m.Mesh.ResourceName}]"));
            warn($"Could not find mesh '{metaName ?? fallbackName}' inside {godotPath}; Godot imported {meshes.Count} mesh object(s): {found}{(meshes.Count > 24 ? ", ..." : "")}");
        } else {
            var meshName = string.IsNullOrEmpty(picked.Mesh.ResourceName) ? picked.Name.ToString() : picked.Mesh.ResourceName;
            var path = meshFilePath(_paths.ResPathWithoutExtension(unityPath) + EXTRACTED_MESH_SUFFIX + "/" + sanitizeFileName(meshName), picked.Mesh);
            mesh = !Overwrite && Godot.FileAccess.FileExists(path) ? ResourceLoader.Load<Mesh>(path) : saveMeshCopy(picked.Mesh, path);
            if (mesh != null) _meshModels[mesh] = (unityPath, metaName ?? picked.Name);
        }
        return mesh;
    }
    // Unity numbers a mesh's material slots by the first polygon that uses each material; Godot keeps the order they're attached in the file.
    // The model file says which is which, and Godot's surfaces carry the material names to match on
    private int surfaceForSlot(Mesh mesh, int slot) {
        if (!_slotSurfaces.TryGetValue(mesh, out var map)) {
            map = slotSurfaces(mesh);
            _slotSurfaces[mesh] = map;
        }
        return slot < map.Length ? map[slot] : slot;
    }
    private int[] slotSurfaces(Mesh mesh) {
        var surfaces = Enumerable.Range(0, mesh.GetSurfaceCount()).ToArray();
        if (surfaces.Length < 2 || !_meshModels.TryGetValue(mesh, out var source)) return surfaces;
        if (!_materialOrders.TryGetValue(source.ModelPath, out var orders)) {
            orders = FbxReader.MaterialOrders(source.ModelPath);
            _materialOrders[source.ModelPath] = orders;
        }
        if (orders == null || orders.Count == 0) return surfaces;
        var order = orders.FirstOrDefault(o => namesMatch(source.Name, o.Key) || namesMatch(mesh.ResourceName, o.Key)).Value ?? (orders.Count == 1 ? orders.Values.First() : null);
        if (order == null) return surfaces;
        var ranks = surfaces.Select(surface => {
            var name = mesh is ArrayMesh arrayMesh ? arrayMesh.SurfaceGetName(surface) : "";
            if (string.IsNullOrEmpty(name)) name = mesh.SurfaceGetMaterial(surface)?.ResourceName ?? "";
            return order.FindIndex(n => namesMatch(name, n));
        }).ToArray();
        // A surface that can't be placed means the names don't line up; the plain order is safer than a half-sorted one
        if (ranks.Any(r => r < 0) || ranks.Distinct().Count() != ranks.Length) {
            trace($"Material slots of '{source.Name}': Unity order [{string.Join(", ", order)}] doesn't match Godot's surfaces; slot numbers are used as they are");
            return surfaces;
        }
        var sorted = surfaces.OrderBy(s => ranks[s]).ToArray();
        trace($"Material slots of '{source.Name}': Unity order [{string.Join(", ", order)}] -> Godot surfaces [{string.Join(", ", sorted)}]");
        return sorted;
    }
    private Mesh saveMeshCopy(Mesh source, string path) {
        ensureDirectory(path.GetBaseDir());
        var error = ResourceSaver.Save((Mesh)source.Duplicate(), path);
        if (error == Error.Ok) return ResourceLoader.Load<Mesh>(path, null, ResourceLoader.CacheMode.Replace);
        warn($"Could not save mesh {path}: {error}");
        return null;
    }
    private static bool matchesMesh(MeshInstance3D node, string name) {
        if (string.IsNullOrEmpty(name)) return false;
        return namesMatch(node.Name, name) || namesMatch(node.Mesh.ResourceName, name);
    }
    private static Mesh builtinMesh(long fileId) {
        return fileId switch {
            10202 => new BoxMesh(),
            10206 => new CylinderMesh(),
            10207 => new SphereMesh(),
            10208 => new CapsuleMesh(),
            10209 => new PlaneMesh { Size = new Vector2(10, 10) },
            10210 => new QuadMesh(),
            _ => null
        };
    }
    
    // Materials
    private Material resolveMaterial(UnityRef materialRef) {
        if (materialRef.IsNull || materialRef.Guid == UnityAssetIndex.BUILTIN_DEFAULT_GUID || materialRef.Guid == UnityAssetIndex.BUILTIN_EXTRA_GUID) return null;
        var key = $"{materialRef.Guid}:{materialRef.FileId}";
        if (_materials.TryGetValue(key, out var cached)) return cached;
        Material material = null;
        var unityPath = _index.GetPath(materialRef.Guid);
        if (unityPath == null) {
            warnOnce($"material-guid:{materialRef.Guid}", $"Material with guid {materialRef.Guid} not found in the Unity project");
        } else if (unityPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) {
            material = convertMaterial(unityPath);
        }
        _materials[key] = material;
        return material;
    }
    
    // Saved next to the material with a suffix, since particles need billboarding and vertex colour the mesh version doesn't have
    private Material particleMaterial(UnityRef materialRef, ParticleConverter.ParticleMaterialOptions options) {
        var unityPath = materialRef.IsNull ? null : _index.GetPath(materialRef.Guid);
        if (unityPath == null || !unityPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) return null;
        var suffix = "_Particles" + (options.Mesh ? (options.Billboard == BaseMaterial3D.BillboardModeEnum.Particles ? "MeshFacing" : "Mesh") : options.Billboard == BaseMaterial3D.BillboardModeEnum.FixedY ? "Vertical" : options.Billboard == BaseMaterial3D.BillboardModeEnum.Disabled ? "Flat" : "") + (options.Sprites != null ? $"_Sprites{options.SpritesKey}" : options.FramesX * options.FramesY > 1 ? $"_{options.FramesX}x{options.FramesY}" : "");
        var godotPath = _paths.ResPathWithoutExtension(unityPath) + suffix + ".tres";
        if (_particleMaterials.TryGetValue(godotPath, out var cached)) return cached;
        Material material = null;
        if (Overwrite || !Godot.FileAccess.FileExists(godotPath) || needsShaderUpdate(unityPath, godotPath)) {
            trace($"Converting particle material {Path.GetFileName(unityPath)}");
            material = _materialConverter.ConvertParticles(unityPath, options);
            if (material != null) material = saveMaterial(material, godotPath);
        } else {
            material = ResourceLoader.Load(godotPath, null, ResourceLoader.CacheMode.Replace) as Material;
        }
        _particleMaterials[godotPath] = material;
        return material;
    }
    private Material convertMaterial(string unityPath) {
        var godotPath = _paths.ResPath(unityPath, ".tres");
        if (Overwrite || !Godot.FileAccess.FileExists(godotPath) || needsShaderUpdate(unityPath, godotPath)) {
            trace($"Converting material {Path.GetFileName(unityPath)}");
            var converted = _materialConverter.Convert(unityPath);
            if (converted == null) {
                warnOnce($"material:{unityPath}", $"Material {Path.GetFileName(unityPath)} could not be read");
                return null;
            }
            return saveMaterial(converted, godotPath);
        }
        return ResourceLoader.Load(godotPath, null, ResourceLoader.CacheMode.Replace) as Material;
    }
    private Material saveMaterial(Material material, string godotPath) {
        ensureDirectory(godotPath.GetBaseDir());
        var error = ResourceSaver.Save(material, godotPath);
        if (error != Error.Ok) {
            warn($"Could not save material {godotPath}: {error}");
            return material;
        }
        // Godot keeps serving an already loaded material of the old type from that path, so the new one takes its place
        if (ResourceLoader.HasCached(godotPath) && ResourceLoader.GetCachedRef(godotPath)?.GetClass() != material.GetClass()) {
            material.TakeOverPath(godotPath);
            _materialTypeChanged = true;
            return material;
        }
        return ResourceLoader.Load(godotPath, null, ResourceLoader.CacheMode.Replace) as Material;
    }
    // A shader added to _Shaders since the last run replaces the material even without Overwrite
    private bool needsShaderUpdate(string unityPath, string godotPath) {
        var wanted = _materialConverter.CustomShaderPath(unityPath);
        if (wanted == null) return false;
        return ResourceLoader.Load(godotPath) is not ShaderMaterial existing || existing.Shader?.ResourcePath != wanted;
    }
    
    // Nested prefabs
    private Node3D buildInstance(PrefabContext context, UnityDocument instanceDocument) {
        if (!enterObject(context, instanceDocument.FileId)) return new Node3D { Name = "SkippedLoop" };
        try {
            return buildInstanceNode(context, instanceDocument);
        } finally {
            context.Depth--;
        }
    }
    private Node3D buildInstanceNode(PrefabContext context, UnityDocument instanceDocument) {
        var modification = UnityYaml.GetMap(instanceDocument.Body, "m_Modification");
        var sourceRef = UnityYaml.GetRef(instanceDocument.Body, "m_SourcePrefab");
        var sourcePath = _index.GetPath(sourceRef.Guid);
        trace($"Nested prefab {(sourcePath == null ? sourceRef.Guid : Path.GetFileName(sourcePath))}");
        var source = sourcePath == null ? null : isModelFile(sourcePath) ? loadModel(sourcePath) : convertPrefab(sourcePath);
        if (source?.Scene != null) trace($"Instancing {source.ScenePath}");
        var node = source?.Scene?.Instantiate(PackedScene.GenEditState.Instance) as Node3D;
        if (node == null) {
            warn($"{context.FileName}: nested prefab source {(sourcePath == null ? sourceRef.Guid : Path.GetFileName(sourcePath))} could not be instanced");
            node = new Node3D { Name = sourcePath == null ? "MissingPrefab" : UnityYaml.NodeName(Path.GetFileNameWithoutExtension(sourcePath)) };
            source = null;
        } else {
            context.Instances.Add((node, source));
        }
        var aliases = new Dictionary<(string, long), (NodePath Path, int ClassId)>();
        mapStrippedObjects(context, instanceDocument, modification, node, source, aliases);
        applyModifications(context, modification, node, source, aliases);
        attachInstanceAdditions(context, instanceDocument, node);
        return node;
    }
    // Lookups per nested instance are indexed once per file; scanning every object per instance made large scenes freeze Godot
    private static void indexInstances(PrefabContext context) {
        foreach (var document in context.Documents.Values) {
            if (document.Stripped) listFor(context.StrippedByInstance, UnityYaml.GetRef(document.Body, "m_PrefabInstance").FileId).Add(document);
        }
        foreach (var document in context.Documents.Values) {
            if (document.Stripped) continue;
            UnityRef parentRef;
            if (isTransform(document.ClassId)) parentRef = UnityYaml.GetRef(document.Body, "m_Father");
            else if (document.ClassId == CLASS_PREFAB_INSTANCE) parentRef = UnityYaml.GetRef(UnityYaml.GetMap(document.Body, "m_Modification"), "m_TransformParent");
            else continue;
            if (!context.Documents.TryGetValue(parentRef.FileId, out var parent) || !parent.Stripped) continue;
            listFor(context.AdditionsByInstance, UnityYaml.GetRef(parent.Body, "m_PrefabInstance").FileId).Add(document);
        }
    }
    private void mapStrippedObjects(PrefabContext context, UnityDocument instanceDocument, Dictionary<string, object> modification, Node3D node, ConvertedAsset source, Dictionary<(string, long), (NodePath, int)> aliases) {
        var stripped = context.StrippedByInstance.TryGetValue(instanceDocument.FileId, out var strippedList) ? strippedList : new List<UnityDocument>();
        foreach (var document in stripped) {
            var corresponding = UnityYaml.GetRef(document.Body, "m_CorrespondingSourceObject");
            if (source != null && tryResolve(source, aliases, corresponding, out var path, out _)) {
                var target = node.GetNodeOrNull(path);
                if (target != null) context.Nodes[document.FileId] = target;
            }
        }
        var rootTransform = findRootStrippedTransform(context, stripped, UnityYaml.GetRef(modification, "m_TransformParent"), node);
        if (rootTransform == null) return;
        context.Nodes[rootTransform.FileId] = node;
        aliases[keyOf(UnityYaml.GetRef(rootTransform.Body, "m_CorrespondingSourceObject"))] = (new NodePath("."), CLASS_TRANSFORM);
        var nameTargets = (UnityYaml.GetList(modification, "m_Modifications") ?? new List<object>())
            .OfType<Dictionary<string, object>>()
            .Where(m => UnityYaml.GetString(m, "propertyPath") == "m_Name")
            .Select(m => UnityYaml.GetRef(m, "target"))
            .Where(r => source == null || !tryResolve(source, aliases, r, out _, out _))
            .ToList();
        if (nameTargets.Count == 1) aliases[keyOf(nameTargets[0])] = (new NodePath("."), CLASS_GAME_OBJECT);
    }
    private static UnityDocument findRootStrippedTransform(PrefabContext context, List<UnityDocument> stripped, UnityRef parentRef, Node3D node) {
        var transforms = stripped.Where(d => isTransform(d.ClassId)).ToList();
        if (!parentRef.IsNull && context.Documents.TryGetValue(parentRef.FileId, out var parent)) {
            var children = (UnityYaml.GetList(parent.Body, "m_Children") ?? new List<object>()).Select(c => UnityYaml.ToRef(c).FileId).ToHashSet();
            var match = transforms.FirstOrDefault(t => children.Contains(t.FileId));
            if (match != null) return match;
        }
        return transforms.FirstOrDefault(t => context.Nodes.TryGetValue(t.FileId, out var mapped) && mapped == node) ?? transforms.FirstOrDefault();
    }
    private void applyModifications(PrefabContext context, Dictionary<string, object> modification, Node3D node, ConvertedAsset source, Dictionary<(string, long), (NodePath, int)> aliases) {
        var transforms = new Dictionary<Node3D, TransformState>();
        var skipped = 0;
        foreach (var entry in (UnityYaml.GetList(modification, "m_Modifications") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            var property = UnityYaml.GetString(entry, "propertyPath");
            var value = UnityYaml.GetString(entry, "value");
            var target = UnityYaml.GetRef(entry, "target");
            NodePath path = null;
            var classId = 0;
            var resolved = source != null && tryResolve(source, aliases, target, out path, out classId);
            if (!resolved && source?.SingleMeshPath != null && property.StartsWith("m_Materials")) {
                path = source.SingleMeshPath;
                classId = CLASS_MESH_RENDERER;
                resolved = true;
            }
            var targetNode = resolved ? node.GetNodeOrNull(path) as Node3D : null;
            if (targetNode == null) {
                if (!isNoiseProperty(property)) skipped++;
                continue;
            }
            var changed = applyModification(targetNode, classId, property, value, UnityYaml.GetRef(entry, "objectReference"), transforms, targetNode == node);
            if (!changed && !isNoiseProperty(property) && !isTransform(classId)) skipped++;
            if (changed && targetNode != node && !isTransform(classId)) context.Editable.Add(node);
        }
        foreach (var (target, state) in transforms) {
            var transform = toGodotTransform(state.Position, state.Rotation, state.Scale);
            if (target.Transform.IsEqualApprox(transform)) continue;
            target.Transform = transform;
            if (target != node) context.Editable.Add(node);
        }
        applyRemovals(context, modification, node, source, aliases);
        if (skipped > 0) note($"{context.FileName}: {skipped} override(s) on '{node.Name}' could not be converted");
    }
    private bool applyModification(Node3D target, int classId, string property, string value, UnityRef objectReference, Dictionary<Node3D, TransformState> transforms, bool isInstanceRoot) {
        if (isTransform(classId)) {
            if (!transforms.TryGetValue(target, out var state)) {
                state = fromGodotTransform(target.Transform);
                transforms[target] = state;
            }
            return applyTransformProperty(state, property, value);
        }
        if (classId == CLASS_GAME_OBJECT) {
            if (property == "m_Name" && isInstanceRoot) {
                target.Name = UnityYaml.NodeName(value);
                return true;
            }
            if (property == "m_IsActive") {
                setActive(target, value != "0");
                return true;
            }
            return false;
        }
        if (classId != CLASS_MESH_RENDERER && classId != CLASS_SKINNED_MESH_RENDERER) return false;
        var meshNode = findMeshInstance(target);
        if (meshNode == null) return false;
        if (property == "m_Enabled") {
            meshNode.Visible = value != "0";
            return true;
        }
        if (!property.StartsWith("m_Materials.Array.data[")) return false;
        var start = property.IndexOf('[') + 1;
        if (!int.TryParse(property.Substring(start, property.IndexOf(']') - start), out var surface)) return false;
        var material = resolveMaterial(objectReference);
        if (material == null || meshNode.Mesh == null || surface >= meshNode.Mesh.GetSurfaceCount()) return false;
        meshNode.SetSurfaceOverrideMaterial(surfaceForSlot(meshNode.Mesh, surface), material);
        return true;
    }
    private void applyRemovals(PrefabContext context, Dictionary<string, object> modification, Node3D node, ConvertedAsset source, Dictionary<(string, long), (NodePath, int)> aliases) {
        if (source == null) return;
        foreach (var key in new[] { "m_RemovedGameObjects", "m_RemovedComponents" }) {
            foreach (var entry in UnityYaml.GetList(modification, key) ?? new List<object>()) {
                if (!tryResolve(source, aliases, UnityYaml.ToRef(entry), out var path, out var classId)) continue;
                if (node.GetNodeOrNull(path) is not Node3D target) continue;
                if (classId == CLASS_GAME_OBJECT) setActive(target, false);
                else if (classId == CLASS_MESH_RENDERER || classId == CLASS_SKINNED_MESH_RENDERER) target.Visible = false;
                else continue;
                if (target != node) context.Editable.Add(node);
            }
        }
    }
    private void attachInstanceAdditions(PrefabContext context, UnityDocument instanceDocument, Node3D node) {
        var stripped = context.StrippedByInstance.TryGetValue(instanceDocument.FileId, out var strippedList) ? strippedList : new List<UnityDocument>();
        var additions = context.AdditionsByInstance.TryGetValue(instanceDocument.FileId, out var additionList) ? additionList : new List<UnityDocument>();
        foreach (var document in stripped.Concat(additions).ToList()) {
            UnityRef parentRef;
            if (document.Stripped) {
                if (document.ClassId != CLASS_GAME_OBJECT) continue;
                if (!context.Components.TryGetValue(document.FileId, out var components)) continue;
                if (context.Nodes.TryGetValue(document.FileId, out var owner) && owner is Node3D owner3D) {
                    attachComponents(context, owner3D, components, false);
                    if (owner3D != node) context.Editable.Add(node);
                } else {
                    warn($"{context.FileName}: components added to an unresolved nested object were skipped");
                }
                continue;
            }
            if (isTransform(document.ClassId)) parentRef = UnityYaml.GetRef(document.Body, "m_Father");
            else if (document.ClassId == CLASS_PREFAB_INSTANCE && document != instanceDocument) parentRef = UnityYaml.GetRef(UnityYaml.GetMap(document.Body, "m_Modification"), "m_TransformParent");
            else continue;
            if (!belongsToInstance(context, parentRef.FileId, instanceDocument.FileId)) continue;
            if (!context.Nodes.TryGetValue(parentRef.FileId, out var parent)) {
                warn($"{context.FileName}: an object added under an unresolved nested object was skipped");
                continue;
            }
            parent.AddChild(isTransform(document.ClassId) ? buildGameObject(context, document) : buildInstance(context, document), true);
            if (parent != node) context.Editable.Add(node);
        }
    }
    private static bool belongsToInstance(PrefabContext context, long fileId, long instanceId) {
        return context.Documents.TryGetValue(fileId, out var document) && document.Stripped && UnityYaml.GetRef(document.Body, "m_PrefabInstance").FileId == instanceId;
    }
    private static bool tryResolve(ConvertedAsset source, Dictionary<(string, long), (NodePath Path, int ClassId)> aliases, UnityRef target, out NodePath path, out int classId) {
        var key = keyOf(target);
        if (aliases.TryGetValue(key, out var alias) || source.Objects.TryGetValue(key, out alias)) {
            path = alias.Path;
            classId = alias.ClassId;
            return true;
        }
        path = null;
        classId = 0;
        return false;
    }
    private static bool isNoiseProperty(string property) {
        return property.StartsWith("m_LocalEulerAnglesHint") || property == "m_RootOrder" || property.StartsWith("m_ConstrainProportionsScale");
    }
    
    // Models
    private ConvertedAsset loadModel(string unityPath) {
        if (_assets.TryGetValue(unityPath, out var cached)) return cached;
        ConvertedAsset asset = null;
        var (godotPath, scene) = modelScene(unityPath);
        var instance = modelInstance(unityPath);
        if (scene != null && instance != null) {
            asset = new ConvertedAsset { ScenePath = godotPath, Scene = scene, IsModel = true };
            mapModelObjects(unityPath, instance, asset);
            applyMaterialRemaps(unityPath, asset);
        }
        _assets[unityPath] = asset;
        return asset;
    }
    // Each model is loaded once; models whose import failed or hasn't finished are never handed to the loader
    private (string Path, PackedScene Scene) modelScene(string unityPath) {
        if (_modelScenes.TryGetValue(unityPath, out var cached)) return cached;
        var godotPath = _paths.ResPath(unityPath);
        PackedScene scene = null;
        var name = Path.GetFileName(unityPath);
        if (!Godot.FileAccess.FileExists(godotPath)) {
            warnOnce($"model:{unityPath}", $"Model {name} was not copied to {godotPath}");
        } else if (!isImported(godotPath, out var reason)) {
            warnOnce($"model:{unityPath}", $"Model {name} skipped: {reason} ({godotPath})");
        } else {
            trace($"Loading model {godotPath}");
            scene = ResourceLoader.Load<PackedScene>(godotPath);
            if (scene == null) warnOnce($"model:{unityPath}", $"Model {name} could not be loaded ({godotPath})");
        }
        _modelScenes[unityPath] = (godotPath, scene);
        return (godotPath, scene);
    }
    private Node modelInstance(string unityPath) {
        if (_modelInstances.TryGetValue(unityPath, out var cached)) return cached;
        var scene = modelScene(unityPath).Scene;
        if (scene != null) trace($"Instancing model {scene.ResourcePath}");
        var instance = scene?.Instantiate();
        if (instance != null) {
            var meshes = new List<MeshInstance3D>();
            collectNodes(instance, meshes);
            foreach (var meshNode in meshes.Where(m => m.Mesh != null)) _meshModels[meshNode.Mesh] = (unityPath, meshNode.Name);
        }
        _modelInstances[unityPath] = instance;
        return instance;
    }
    private static bool isImported(string godotPath, out string reason) {
        var importFile = godotPath + ".import";
        if (!Godot.FileAccess.FileExists(importFile)) {
            reason = "Godot has not imported it yet";
            return false;
        }
        var importedPath = "";
        foreach (var line in Godot.FileAccess.GetFileAsString(importFile).Split('\n')) {
            if (line.Trim() == "valid=false") {
                reason = "Godot failed to import it (check the Output panel when it imports)";
                return false;
            }
            if (line.StartsWith("path=") || line.StartsWith("path.")) importedPath = line.Substring(line.IndexOf('=') + 1).Trim().Trim('"');
        }
        if (importedPath.Length == 0 || !Godot.FileAccess.FileExists(importedPath)) {
            reason = "its import is missing or unfinished; reimport it in Godot";
            return false;
        }
        reason = null;
        return true;
    }
    private void mapModelObjects(string unityPath, Node instance, ConvertedAsset asset) {
        var guid = _index.GetGuid(unityPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(unityPath);
        foreach (var (fileId, info) in _index.GetModelMeta(unityPath).Objects) {
            var target = info.Name == stem || info.Name == "//RootNode" ? instance : findByName(instance, info.Name);
            if (target == null) continue;
            if (info.ClassId == CLASS_MESH_RENDERER || info.ClassId == CLASS_SKINNED_MESH_RENDERER || info.ClassId == CLASS_MESH_FILTER) {
                target = findMeshInstance(target) ?? target;
            }
            asset.Objects[(guid, fileId)] = (instance.GetPathTo(target), info.ClassId);
        }
        var meshes = new List<MeshInstance3D>();
        collectNodes(instance, meshes);
        if (meshes.Count == 1) asset.SingleMeshPath = instance.GetPathTo(meshes[0]);
    }
    private void applyMaterialRemaps(string unityPath, ConvertedAsset asset) {
        var remaps = _index.GetModelMeta(unityPath).MaterialRemaps;
        if (remaps.Count == 0) return;
        trace($"Applying material remaps to {asset.ScenePath}");
        var root = asset.Scene.Instantiate(PackedScene.GenEditState.Instance);
        var wrapper = wrapForInheritance(root);
        var meshes = new List<MeshInstance3D>();
        collectNodes(root, meshes);
        var applied = 0;
        foreach (var meshNode in meshes) {
            if (meshNode.Mesh == null) continue;
            for (var i = 0; i < meshNode.Mesh.GetSurfaceCount(); i++) {
                var surfaceMaterial = meshNode.Mesh.SurfaceGetMaterial(i);
                var materialName = surfaceMaterial?.ResourceName ?? "";
                if (!remaps.TryGetValue(materialName, out var materialRef)) continue;
                var material = resolveMaterial(materialRef);
                if (material == null) continue;
                meshNode.SetSurfaceOverrideMaterial(i, material);
                applied++;
            }
        }
        if (applied > 0) {
            var remapped = new ConvertedAsset { ScenePath = _paths.ResPathWithoutExtension(unityPath) + REMAPPED_MODEL_SUFFIX };
            assignOwners(wrapper, wrapper);
            wrapper.SetEditableInstance(root, true);
            saveScene(wrapper, remapped, UnityYaml.NodeName(Path.GetFileNameWithoutExtension(unityPath)));
            if (remapped.Scene != null) {
                asset.ScenePath = remapped.ScenePath;
                asset.Scene = remapped.Scene;
                note($"{Path.GetFileName(unityPath)}: material remaps applied in {remapped.ScenePath}");
            }
        }
        wrapper.Free();
    }
    
    // Components
    private static void setupRigidbody(RigidBody3D body, Dictionary<string, object> data) {
        body.Mass = Math.Max((float)UnityYaml.GetDouble(data, "m_Mass", 1), 0.001f);
        body.LinearDamp = (float)UnityYaml.GetDouble(data, "m_LinearDamping", UnityYaml.GetDouble(data, "m_Drag", 0));
        body.AngularDamp = (float)UnityYaml.GetDouble(data, "m_AngularDamping", UnityYaml.GetDouble(data, "m_AngularDrag", 0.05));
        if (!UnityYaml.GetBool(data, "m_UseGravity", true)) body.GravityScale = 0;
        if (UnityYaml.GetBool(data, "m_IsKinematic")) {
            body.Freeze = true;
            body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        }
    }
    private static Light3D createLight(Dictionary<string, object> data) {
        var range = (float)UnityYaml.GetDouble(data, "m_Range", 10);
        Light3D light = UnityYaml.GetLong(data, "m_Type", 2) switch {
            0 => new SpotLight3D { SpotRange = range, SpotAngle = (float)UnityYaml.GetDouble(data, "m_SpotAngle", 30) * 0.5f },
            1 => new DirectionalLight3D(),
            _ => new OmniLight3D { OmniRange = range }
        };
        light.Name = "Light";
        light.LightColor = readColor(UnityYaml.GetMap(data, "m_Color"));
        light.LightEnergy = (float)UnityYaml.GetDouble(data, "m_Intensity", 1);
        light.ShadowEnabled = UnityYaml.GetLong(UnityYaml.GetMap(data, "m_Shadows"), "m_Type") != 0;
        light.Visible = UnityYaml.GetBool(data, "m_Enabled", true);
        light.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero);
        return light;
    }
    private static Camera3D createCamera(Dictionary<string, object> data) {
        var camera = new Camera3D {
            Name = "Camera",
            Fov = (float)UnityYaml.GetDouble(data, "field of view", 60),
            Near = (float)UnityYaml.GetDouble(data, "near clip plane", 0.3),
            Far = (float)UnityYaml.GetDouble(data, "far clip plane", 1000),
            Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero)
        };
        if (UnityYaml.GetBool(data, "orthographic")) {
            camera.Projection = Camera3D.ProjectionType.Orthogonal;
            camera.Size = (float)UnityYaml.GetDouble(data, "orthographic size", 5) * 2;
        }
        return camera;
    }
    
    // Colliders
    private void attachColliders(PrefabContext context, Node3D node, List<UnityDocument> components, MeshInstance3D meshNode) {
        CollisionObject3D solid = node as RigidBody3D;
        Area3D trigger = null;
        foreach (var component in components) {
            var shape = createCollisionShape(component, meshNode);
            if (shape == null) continue;
            if (UnityYaml.GetBool(component.Body, "m_IsTrigger")) {
                trigger ??= addChild(node, new Area3D { Name = "Trigger" });
                trigger.AddChild(shape, true);
                continue;
            }
            if (solid == null) {
                var staticBody = addChild(node, new StaticBody3D { Name = "StaticBody" });
                context.StaticBodies.Add(staticBody);
                solid = staticBody;
            }
            solid.AddChild(shape, true);
        }
    }
    private CollisionShape3D createCollisionShape(UnityDocument component, MeshInstance3D meshNode) {
        var data = component.Body;
        var center = readVector3(UnityYaml.GetMap(data, "m_Center"), Vector3.Zero);
        var shape = new CollisionShape3D {
            Name = component.TypeName ?? "Collider",
            Position = toGodotPosition(center),
            Disabled = !UnityYaml.GetBool(data, "m_Enabled", true)
        };
        switch (component.ClassId) {
            case CLASS_BOX_COLLIDER:
                shape.Shape = new BoxShape3D { Size = readVector3(UnityYaml.GetMap(data, "m_Size"), Vector3.One).Abs() };
                break;
            case CLASS_SPHERE_COLLIDER:
                shape.Shape = new SphereShape3D { Radius = (float)UnityYaml.GetDouble(data, "m_Radius", 0.5) };
                break;
            case CLASS_CAPSULE_COLLIDER:
                var radius = (float)UnityYaml.GetDouble(data, "m_Radius", 0.5);
                var height = Math.Max((float)UnityYaml.GetDouble(data, "m_Height", 2), radius * 2);
                shape.Shape = new CapsuleShape3D { Radius = radius, Height = height };
                var direction = UnityYaml.GetLong(data, "m_Direction", 1);
                if (direction == 0) shape.Rotation = new Vector3(0, 0, Mathf.Pi * 0.5f);
                else if (direction == 2) shape.Rotation = new Vector3(Mathf.Pi * 0.5f, 0, 0);
                break;
            case CLASS_MESH_COLLIDER:
                trace("Creating mesh collider");
                var mesh = resolveMesh(UnityYaml.GetRef(data, "m_Mesh"), meshNode?.Name.ToString() ?? "") ?? meshNode?.Mesh;
                if (mesh == null) {
                    shape.Free();
                    return null;
                }
                shape.Shape = UnityYaml.GetBool(data, "m_Convex") ? mesh.CreateConvexShape() : mesh.CreateTrimeshShape();
                shape.Position = Vector3.Zero;
                break;
            default:
                shape.Free();
                return null;
        }
        return shape;
    }
    private static void mergeCompoundColliders(PrefabContext context) {
        foreach (var staticBody in context.StaticBodies) {
            RigidBody3D body = null;
            var relative = Transform3D.Identity;
            for (var current = staticBody as Node; current?.GetParent() != null; current = current.GetParent()) {
                if (current is Node3D current3D) relative = current3D.Transform * relative;
                if (current.GetParent() is RigidBody3D parentBody) {
                    body = parentBody;
                    break;
                }
            }
            if (body == null) continue;
            foreach (var shape in staticBody.GetChildren().OfType<CollisionShape3D>().ToList()) {
                staticBody.RemoveChild(shape);
                shape.Transform = relative * shape.Transform;
                body.AddChild(shape, true);
            }
            staticBody.GetParent().RemoveChild(staticBody);
            staticBody.Free();
        }
    }
    
    // Animation
    // The player gets the controller's clips plus every clip that belongs to the animated model, its animation files, or this hierarchy
    private void attachAnimator(PrefabContext context, Node3D node, UnityDocument animator) {
        var library = new AnimationLibrary();
        string autoplay = null;
        var controllerRef = UnityYaml.GetRef(animator.Body, "m_Controller");
        var controllerPath = controllerRef.IsNull ? null : _index.GetPath(controllerRef.Guid);
        if (!controllerRef.IsNull && controllerPath == null) warnOnce($"controller:{controllerRef.Guid}", $"Animator controller {controllerRef.Guid} not found in the Unity project");
        if (controllerPath != null) {
            var (controllerLibrary, defaultClip) = _animationConverter.ConvertController(controllerPath);
            if (controllerLibrary != null) {
                foreach (var name in controllerLibrary.GetAnimationList()) library.AddAnimation(name, controllerLibrary.GetAnimation(name));
                autoplay = defaultClip;
            }
        }
        foreach (var animation in relatedAnimations(context, animator)) {
            if (library.GetAnimationList().Any(n => library.GetAnimation(n) == animation || (animation.ResourcePath.Length > 0 && library.GetAnimation(n).ResourcePath == animation.ResourcePath))) continue;
            var name = string.IsNullOrEmpty(animation.ResourceName) ? "Clip" : animation.ResourceName.Replace("/", "_").Replace(":", "_").Replace(",", "_").Replace("[", "_").Replace("]", "_");
            var unique = name;
            for (var i = 2; library.HasAnimation(unique); i++) unique = name + i;
            library.AddAnimation(unique, animation);
        }
        if (library.GetAnimationList().Count == 0) return;
        addAnimationPlayer(node, library, autoplay);
    }
    private List<Animation> relatedAnimations(PrefabContext context, UnityDocument animator) {
        var models = new List<string>();
        var avatarPath = _index.GetPath(UnityYaml.GetRef(animator.Body, "m_Avatar").Guid);
        if (avatarPath != null && isModelFile(avatarPath)) models.Add(avatarPath);
        var gameObjectId = UnityYaml.GetRef(animator.Body, "m_GameObject").FileId;
        var transform = context.Components.TryGetValue(gameObjectId, out var components) ? components.FirstOrDefault(c => isTransform(c.ClassId)) : null;
        var hierarchy = new HashSet<string>();
        if (transform != null) collectHierarchy(context, transform, "", hierarchy, models, 0);
        var animations = new List<Animation>();
        foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase)) {
            animations.AddRange(_animationConverter.ModelAnimations(model));
            foreach (var companion in _animationConverter.CompanionModels(model)) animations.AddRange(_animationConverter.ModelAnimations(companion));
        }
        animations.AddRange(_animationConverter.MatchingClipFiles(hierarchy, packFolder(context.UnityPath)));
        return animations;
    }
    private void collectHierarchy(PrefabContext context, UnityDocument transform, string prefix, HashSet<string> hierarchy, List<string> models, int depth) {
        if (depth > MAX_HIERARCHY_DEPTH) return;
        var gameObjectId = UnityYaml.GetRef(transform.Body, "m_GameObject").FileId;
        foreach (var component in context.Components.TryGetValue(gameObjectId, out var list) ? list : new List<UnityDocument>()) {
            if (component.ClassId != CLASS_MESH_FILTER && component.ClassId != CLASS_SKINNED_MESH_RENDERER) continue;
            var meshPath = _index.GetPath(UnityYaml.GetRef(component.Body, "m_Mesh").Guid);
            if (meshPath != null && isModelFile(meshPath)) models.Add(meshPath);
        }
        foreach (var child in UnityYaml.GetList(transform.Body, "m_Children") ?? new List<object>()) {
            if (!context.Documents.TryGetValue(UnityYaml.ToRef(child).FileId, out var childDocument)) continue;
            if (childDocument.Stripped) {
                var instanceId = UnityYaml.GetRef(childDocument.Body, "m_PrefabInstance").FileId;
                if (context.Documents.TryGetValue(instanceId, out var instance)) {
                    var sourcePath = _index.GetPath(UnityYaml.GetRef(instance.Body, "m_SourcePrefab").Guid);
                    if (sourcePath != null && isModelFile(sourcePath)) models.Add(sourcePath);
                }
                continue;
            }
            context.Documents.TryGetValue(UnityYaml.GetRef(childDocument.Body, "m_GameObject").FileId, out var gameObject);
            var name = UnityYaml.GetString(gameObject?.Body, "m_Name");
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            hierarchy.Add(path);
            collectHierarchy(context, childDocument, path, hierarchy, models, depth + 1);
        }
    }
    // The top-level folder under Assets usually holds the whole asset pack (for example "Toon Series")
    private string packFolder(string unityPath) {
        var parts = _paths.Relative(unityPath).Split('/');
        var depth = Math.Min(1, parts.Length - 1);
        return Path.Combine(new[] { _paths.AssetsRoot }.Concat(parts.Take(depth)).ToArray());
    }
    private void attachLegacyAnimation(Node3D node, UnityDocument component) {
        var clips = (UnityYaml.GetList(component.Body, "m_Animations") ?? new List<object>()).Select(UnityYaml.ToRef).Where(r => !r.IsNull).ToList();
        var defaultRef = UnityYaml.GetRef(component.Body, "m_Animation");
        if (!defaultRef.IsNull && !clips.Contains(defaultRef)) clips.Insert(0, defaultRef);
        if (clips.Count == 0) return;
        var library = _animationConverter.BuildLibrary(clips);
        var defaultAnimation = _animationConverter.ConvertClip(defaultRef);
        var defaultClip = library.GetAnimationList().Select(n => n.ToString()).FirstOrDefault(n => library.GetAnimation(n) == defaultAnimation);
        addAnimationPlayer(node, library, UnityYaml.GetBool(component.Body, "m_PlayAutomatically", true) ? defaultClip : null);
    }
    private static void addAnimationPlayer(Node3D node, AnimationLibrary library, string autoplay) {
        var player = new AnimationPlayer { Name = "UnityAnimator" };
        player.AddAnimationLibrary("", library);
        if (!string.IsNullOrEmpty(autoplay)) player.Autoplay = autoplay;
        node.AddChild(player, true);
    }
    
    // Animated models: an Animator over skinned meshes is rebuilt from Godot's import of the model so the skeleton and skinning work
    private string findAnimatedModel(PrefabContext context, UnityDocument transform, List<UnityDocument> components) {
        if (!components.Any(c => c.ClassId == CLASS_ANIMATOR)) return null;
        var pending = new Stack<UnityDocument>();
        pending.Push(transform);
        for (var visited = 0; pending.Count > 0 && visited < 10000; visited++) {
            var current = pending.Pop();
            var owner = UnityYaml.GetRef(current.Body, "m_GameObject").FileId;
            if (context.Components.TryGetValue(owner, out var ownComponents)) {
                foreach (var renderer in ownComponents.Where(c => c.ClassId == CLASS_SKINNED_MESH_RENDERER)) {
                    var modelPath = _index.GetPath(UnityYaml.GetRef(renderer.Body, "m_Mesh").Guid);
                    if (modelPath != null && isModelFile(modelPath)) return modelPath;
                }
            }
            foreach (var child in UnityYaml.GetList(current.Body, "m_Children") ?? new List<object>()) {
                if (context.Documents.TryGetValue(UnityYaml.ToRef(child).FileId, out var childDocument) && !childDocument.Stripped) pending.Push(childDocument);
            }
        }
        return null;
    }
    private Node3D buildAnimatedModel(PrefabContext context, UnityDocument transform, UnityDocument gameObject, List<UnityDocument> components, string modelPath, string name) {
        var source = loadModel(modelPath);
        if (source?.Scene == null) return null;
        if (source.Scene.Instantiate(PackedScene.GenEditState.Instance) is not Node3D node) return null;
        trace($"Rebuilding '{name}' from {source.ScenePath}");
        context.Instances.Add((node, source));
        context.Editable.Add(node);
        node.Name = string.IsNullOrWhiteSpace(name) ? "GameObject" : UnityYaml.NodeName(name);
        node.Transform = readUnityTransform(transform.Body);
        if (!UnityYaml.GetBool(gameObject?.Body, "m_IsActive", true)) setActive(node, false);
        context.Nodes[UnityYaml.GetRef(transform.Body, "m_GameObject").FileId] = node;
        context.Nodes[transform.FileId] = node;
        var skeletons = new List<Skeleton3D>();
        collectNodes(node, skeletons);
        mapAnimatedChildren(context, transform, node, () => node, skeletons.FirstOrDefault());
        attachComponents(context, node, components.Where(isExtraComponent).ToList(), false);
        note($"{context.FileName}: '{name}' rebuilt from {Path.GetFileName(modelPath)} with its skeleton and animations");
        return node;
    }
    private void mapAnimatedChildren(PrefabContext context, UnityDocument parentTransform, Node3D root, Func<Node3D> parent, Skeleton3D skeleton) {
        foreach (var child in UnityYaml.GetList(parentTransform.Body, "m_Children") ?? new List<object>()) {
            if (!context.Documents.TryGetValue(UnityYaml.ToRef(child).FileId, out var document)) continue;
            if (document.Stripped) {
                if (context.Documents.TryGetValue(UnityYaml.GetRef(document.Body, "m_PrefabInstance").FileId, out var instance)) parent().AddChild(buildInstance(context, instance), true);
                continue;
            }
            var gameObjectId = UnityYaml.GetRef(document.Body, "m_GameObject").FileId;
            context.Documents.TryGetValue(gameObjectId, out var gameObject);
            var components = context.Components.TryGetValue(gameObjectId, out var list) ? list : new List<UnityDocument>();
            var name = UnityYaml.GetString(gameObject?.Body, "m_Name");
            var match = findByName(root, name) as Node3D;
            if (match != null) {
                context.Built.Add(document.FileId);
                context.Nodes[gameObjectId] = match;
                context.Nodes[document.FileId] = match;
                if (!UnityYaml.GetBool(gameObject?.Body, "m_IsActive", true)) setActive(match, false);
                foreach (var renderer in components.Where(c => c.ClassId == CLASS_MESH_RENDERER || c.ClassId == CLASS_SKINNED_MESH_RENDERER)) {
                    var meshNode = match as MeshInstance3D ?? findMeshInstance(match);
                    if (meshNode == null) continue;
                    applyRendererSettings(meshNode, renderer);
                    context.Nodes[renderer.FileId] = meshNode;
                }
                attachComponents(context, match, components.Where(isExtraComponent).ToList(), false);
                mapAnimatedChildren(context, document, root, () => match, skeleton);
            } else if (skeleton != null && skeleton.FindBone(name) >= 0) {
                context.Built.Add(document.FileId);
                var bone = name;
                Node3D attach() => boneAttachment(context, skeleton, bone);
                var extra = components.Where(isExtraComponent).ToList();
                if (extra.Count > 0) attachComponents(context, attach(), extra, false);
                mapAnimatedChildren(context, document, root, attach, skeleton);
            } else {
                parent().AddChild(buildGameObject(context, document), true);
            }
        }
    }
    private static bool isExtraComponent(UnityDocument component) {
        return !isTransform(component.ClassId) && component.ClassId != CLASS_MESH_RENDERER && component.ClassId != CLASS_SKINNED_MESH_RENDERER && component.ClassId != CLASS_MESH_FILTER;
    }
    private static BoneAttachment3D boneAttachment(PrefabContext context, Skeleton3D skeleton, string bone) {
        var key = skeleton.GetInstanceId() + ":" + bone;
        if (context.BoneAttachments.TryGetValue(key, out var existing)) return existing;
        var attachment = new BoneAttachment3D { Name = UnityYaml.NodeName((bone + "Attachment")), BoneName = bone };
        skeleton.AddChild(attachment, true);
        context.BoneAttachments[key] = attachment;
        return attachment;
    }
    private void applyRendererSettings(MeshInstance3D meshNode, UnityDocument renderer) {
        var materials = UnityYaml.GetList(renderer.Body, "m_Materials");
        if (materials != null && meshNode.Mesh != null) {
            for (var i = 0; i < Math.Min(materials.Count, meshNode.Mesh.GetSurfaceCount()); i++) {
                var material = resolveMaterial(UnityYaml.ToRef(materials[i]));
                if (material != null) meshNode.SetSurfaceOverrideMaterial(surfaceForSlot(meshNode.Mesh, i), material);
            }
        }
        if (UnityYaml.GetLong(renderer.Body, "m_CastShadows", 1) == 0) meshNode.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        if (!UnityYaml.GetBool(renderer.Body, "m_Enabled", true)) meshNode.Visible = false;
    }
    
    // Terrain
    private void attachTerrain(PrefabContext context, Node3D node, UnityDocument terrain) {
        var assets = _terrainConverter.Convert(UnityYaml.GetRef(terrain.Body, "m_TerrainData"));
        if (assets == null) {
            warn($"{context.FileName}: terrain '{node.Name}' could not be converted (see the warning above)");
            return;
        }
        note($"{context.FileName}: terrain '{node.Name}' converted ({assets.Summary})");
        var meshNode = new MeshInstance3D { Name = "TerrainMesh", Mesh = assets.Mesh, MaterialOverride = assets.Material };
        if (UnityYaml.GetLong(terrain.Body, "m_CastShadows", 1) == 0) meshNode.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        node.AddChild(meshNode, true);
        if (assets.Trees.Count > 0 && UnityYaml.GetBool(terrain.Body, "m_DrawTreesAndFoliage", true)) addTrees(context, node, assets);
        if (assets.DetailPrototypes > 0) note($"{context.FileName}: terrain '{node.Name}' has {assets.DetailPrototypes} grass/detail layer(s), which are not converted");
    }
    private void attachTerrainCollider(Node3D node, UnityDocument collider) {
        var assets = _terrainConverter.Convert(UnityYaml.GetRef(collider.Body, "m_TerrainData"));
        if (assets?.Shape == null) return;
        var body = new StaticBody3D { Name = "TerrainCollision" };
        body.AddChild(new CollisionShape3D { Name = "Heightmap", Shape = assets.Shape, Position = assets.ShapeOffset, Scale = assets.ShapeScale });
        node.AddChild(body, true);
    }
    // Trees are instances of their converted prefabs; names are made unique up front because thousands of same-named siblings are slow to add
    private void addTrees(PrefabContext context, Node3D node, TerrainConverter.TerrainAssets assets) {
        var scenes = assets.TreePrototypes.Select(prototype => {
            var path = _index.GetPath(prototype.Guid);
            if (path == null) return (Scene: (PackedScene)null, Name: "Tree");
            var source = isModelFile(path) ? loadModel(path) : convertPrefab(path);
            return (Scene: source?.Scene, Name: UnityYaml.NodeName(Path.GetFileNameWithoutExtension(path)));
        }).ToList();
        var trees = new Node3D { Name = "Trees" };
        node.AddChild(trees, true);
        var missing = 0;
        for (var i = 0; i < assets.Trees.Count; i++) {
            var tree = assets.Trees[i];
            var prototype = tree.Prototype >= 0 && tree.Prototype < scenes.Count ? scenes[tree.Prototype] : default;
            if (prototype.Scene?.Instantiate(PackedScene.GenEditState.Instance) is not Node3D instance) {
                missing++;
                continue;
            }
            instance.Name = $"{prototype.Name}_{i}";
            instance.Transform = new Transform3D(new Basis(Vector3.Up, tree.Rotation) * Basis.FromScale(new Vector3(tree.Width, tree.Height, tree.Width)), tree.Position);
            trees.AddChild(instance);
        }
        trace($"Placed {assets.Trees.Count - missing} terrain trees");
        if (missing > 0) warn($"{context.FileName}: {missing} terrain tree(s) skipped because their prototype could not be converted");
    }
    
    // Scenes
    private Node3D buildSceneRoot(PrefabContext context, string unityPath) {
        var root = new Node3D { Name = UnityYaml.NodeName(Path.GetFileNameWithoutExtension(unityPath)) };
        var order = new Dictionary<long, int>();
        var sceneRoots = context.Documents.Values.FirstOrDefault(d => d.ClassId == CLASS_SCENE_ROOTS);
        var listed = UnityYaml.GetList(sceneRoots?.Body, "m_Roots") ?? new List<object>();
        for (var i = 0; i < listed.Count; i++) order[UnityYaml.ToRef(listed[i]).FileId] = i;
        var roots = new List<(double Order, Func<Node3D> Build)>();
        foreach (var document in context.Documents.Values.ToList()) {
            if (!document.Stripped && isTransform(document.ClassId) && UnityYaml.GetRef(document.Body, "m_Father").IsNull) {
                var rank = order.TryGetValue(document.FileId, out var listedRank) ? listedRank : 100000 + UnityYaml.GetLong(document.Body, "m_RootOrder");
                roots.Add((rank, () => buildGameObject(context, document)));
            } else if (document.ClassId == CLASS_PREFAB_INSTANCE && UnityYaml.GetRef(UnityYaml.GetMap(document.Body, "m_Modification"), "m_TransformParent").IsNull) {
                var instanceStripped = context.StrippedByInstance.TryGetValue(document.FileId, out var strippedList) ? strippedList : new List<UnityDocument>();
                var rootTransform = instanceStripped.FirstOrDefault(d => isTransform(d.ClassId) && order.ContainsKey(d.FileId));
                var rank = rootTransform != null ? order[rootTransform.FileId] : 200000;
                roots.Add((rank, () => buildInstance(context, document)));
            }
        }
        var renderSettings = context.Documents.Values.FirstOrDefault(d => d.ClassId == CLASS_RENDER_SETTINGS);
        if (renderSettings != null) root.AddChild(createEnvironment(renderSettings.Body, context.FileName), true);
        else note($"{context.FileName}: no lighting settings found, so no WorldEnvironment was created");
        foreach (var (_, build) in roots.OrderBy(r => r.Order)) root.AddChild(build(), true);
        return root;
    }
    private Material skyFrom(UnityRef skybox, string sceneName) {
        if (skybox.IsNull) {
            note($"{sceneName}: no skybox; Godot's procedural sky is used");
            return null;
        }
        if (skybox.Guid == UnityAssetIndex.BUILTIN_EXTRA_GUID) {
            note($"{sceneName}: Unity's default skybox becomes Godot's procedural sky");
            return null;
        }
        var path = _index.GetPath(skybox.Guid);
        if (path == null || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) {
            warn($"{sceneName}: skybox material {skybox.Guid} not found in the Unity project; Godot's procedural sky is used");
            return null;
        }
        var (material, kind) = _materialConverter.ConvertSky(path);
        if (material == null) warn($"{sceneName}: skybox {Path.GetFileName(path)} could not be converted because {kind}; Godot's procedural sky is used");
        else note($"{sceneName}: skybox {Path.GetFileName(path)} converted as a {kind}");
        return material;
    }
    private WorldEnvironment createEnvironment(Dictionary<string, object> settings, string sceneName) {
        var skyMaterial = skyFrom(UnityYaml.GetRef(settings, "m_SkyboxMaterial"), sceneName);
        var environment = new Godot.Environment {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMaterial ?? new ProceduralSkyMaterial() }
        };
        var ambientMode = UnityYaml.GetLong(settings, "m_AmbientMode");
        if (ambientMode == 0) {
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            environment.AmbientLightEnergy = (float)UnityYaml.GetDouble(settings, "m_AmbientIntensity", 1);
        } else {
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            environment.AmbientLightColor = readColor(UnityYaml.GetMap(settings, "m_AmbientSkyColor"));
        }
        // Unity's fog never covers the skybox; Godot's does unless told otherwise
        if (UnityYaml.GetBool(settings, "m_Fog")) {
            environment.FogEnabled = true;
            environment.FogLightColor = readColor(UnityYaml.GetMap(settings, "m_FogColor"));
            environment.FogSkyAffect = 0;
            if (UnityYaml.GetLong(settings, "m_FogMode", 3) == 1) {
                environment.FogMode = Godot.Environment.FogModeEnum.Depth;
                environment.FogDensity = 1;
                environment.FogDepthCurve = 1;
                environment.FogDepthBegin = (float)UnityYaml.GetDouble(settings, "m_LinearFogStart", 0);
                environment.FogDepthEnd = (float)UnityYaml.GetDouble(settings, "m_LinearFogEnd", 300);
            } else {
                environment.FogDensity = (float)UnityYaml.GetDouble(settings, "m_FogDensity", 0.01);
            }
        }
        return new WorldEnvironment { Name = "WorldEnvironment", Environment = environment };
    }
    
    // Coordinate conversion (Unity is left-handed, mirrored on X to match how Godot imports the same model files)
    private static Transform3D readUnityTransform(Dictionary<string, object> data) {
        var position = readVector3(UnityYaml.GetMap(data, "m_LocalPosition"), Vector3.Zero);
        var rotation = readQuaternion(UnityYaml.GetMap(data, "m_LocalRotation"));
        var scale = readVector3(UnityYaml.GetMap(data, "m_LocalScale"), Vector3.One);
        return toGodotTransform(position, rotation, scale);
    }
    private static Transform3D toGodotTransform(Vector3 position, Quaternion rotation, Vector3 scale) {
        var godotRotation = new Quaternion(rotation.X, -rotation.Y, -rotation.Z, rotation.W);
        godotRotation = godotRotation.LengthSquared() < 1e-8f ? Quaternion.Identity : godotRotation.Normalized();
        return new Transform3D(new Basis(godotRotation) * Basis.FromScale(scale), toGodotPosition(position));
    }
    private static TransformState fromGodotTransform(Transform3D transform) {
        var rotation = transform.Basis.GetRotationQuaternion();
        return new TransformState {
            Position = toGodotPosition(transform.Origin),
            Rotation = new Quaternion(rotation.X, -rotation.Y, -rotation.Z, rotation.W),
            Scale = transform.Basis.Scale
        };
    }
    private static Vector3 toGodotPosition(Vector3 position) {
        return new Vector3(-position.X, position.Y, position.Z);
    }
    private static bool applyTransformProperty(TransformState state, string property, string value) {
        var parts = property.Split('.');
        if (parts.Length != 2) return false;
        var number = (float)UnityYaml.ParseDouble(value, 0);
        switch (parts[0]) {
            case "m_LocalPosition":
                state.Position = withComponent(state.Position, parts[1], number);
                return true;
            case "m_LocalScale":
                state.Scale = withComponent(state.Scale, parts[1], number);
                return true;
            case "m_LocalRotation":
                var r = state.Rotation;
                state.Rotation = parts[1] switch {
                    "x" => new Quaternion(number, r.Y, r.Z, r.W),
                    "y" => new Quaternion(r.X, number, r.Z, r.W),
                    "z" => new Quaternion(r.X, r.Y, number, r.W),
                    "w" => new Quaternion(r.X, r.Y, r.Z, number),
                    _ => r
                };
                return true;
            default:
                return false;
        }
    }
    private static Vector3 withComponent(Vector3 vector, string axis, float value) {
        return axis switch {
            "x" => new Vector3(value, vector.Y, vector.Z),
            "y" => new Vector3(vector.X, value, vector.Z),
            "z" => new Vector3(vector.X, vector.Y, value),
            _ => vector
        };
    }
    private static Vector3 readVector3(Dictionary<string, object> map, Vector3 fallback) {
        if (map == null) return fallback;
        return new Vector3(
            (float)UnityYaml.GetDouble(map, "x", fallback.X),
            (float)UnityYaml.GetDouble(map, "y", fallback.Y),
            (float)UnityYaml.GetDouble(map, "z", fallback.Z));
    }
    private static Quaternion readQuaternion(Dictionary<string, object> map) {
        if (map == null) return Quaternion.Identity;
        return new Quaternion(
            (float)UnityYaml.GetDouble(map, "x"),
            (float)UnityYaml.GetDouble(map, "y"),
            (float)UnityYaml.GetDouble(map, "z"),
            (float)UnityYaml.GetDouble(map, "w", 1));
    }
    private static Color readColor(Dictionary<string, object> map) {
        if (map == null) return Colors.White;
        return new Color(
            (float)UnityYaml.GetDouble(map, "r", 1),
            (float)UnityYaml.GetDouble(map, "g", 1),
            (float)UnityYaml.GetDouble(map, "b", 1),
            (float)UnityYaml.GetDouble(map, "a", 1));
    }
    
    // Paths
    // A scene sharing its name with a prefab in the same folder gets a suffix so neither overwrites the other
    private string scenePathFor(string unityPath) {
        if (unityPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(unityPath, ".prefab"))) {
            return _paths.ResPathWithoutExtension(unityPath) + "_Scene.tscn";
        }
        return _paths.ResPath(unityPath, ".tscn");
    }
    private static string joinNodePath(string prefix, string path) {
        if (path == ".") return prefix;
        return prefix == "." ? path : prefix + "/" + path;
    }
    private static string sanitizeFileName(string name) {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '*', '?', '"', '<', '>', '|', '\\', '/' };
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Mesh" : cleaned;
    }
    private static void ensureDirectory(string resDirectory) {
        DirAccess.MakeDirRecursiveAbsolute(resDirectory);
    }
    private static bool isModelFile(string path) {
        return AssetCollector.IsModel(path);
    }
    private static bool isTransform(int classId) {
        return classId == CLASS_TRANSFORM || classId == CLASS_RECT_TRANSFORM;
    }
    private static (string, long) keyOf(UnityRef reference) {
        return (reference.Guid ?? "", reference.FileId);
    }
    
    // Inherited scenes (built as wrapper + instance, then rewritten so the instance becomes the inherited root)
    private static Node3D wrapForInheritance(Node instance) {
        var wrapper = new Node3D { Name = "Wrapper" };
        instance.Name = INHERITED_BASE_NAME;
        wrapper.AddChild(instance);
        return wrapper;
    }
    private static void rewriteAsInherited(string scenePath, string rootName) {
        var lines = Godot.FileAccess.GetFileAsString(scenePath).Split('\n');
        var output = new List<string>();
        var skipping = false;
        foreach (var line in lines) {
            if (line.StartsWith("[node ")) {
                var parent = attributeOf(line, "parent");
                skipping = parent == null;
                if (skipping) continue;
                output.Add(rewriteNodeHeader(line, parent, rootName));
                continue;
            }
            if (line.StartsWith("[editable ")) {
                var editablePath = attributeOf(line, "path");
                if (editablePath == INHERITED_BASE_NAME) continue;
                output.Add(line.Replace($"path=\"{INHERITED_BASE_NAME}/", "path=\""));
                continue;
            }
            if (line.StartsWith("[")) skipping = false;
            if (!skipping) output.Add(line);
        }
        using var file = Godot.FileAccess.Open(scenePath, Godot.FileAccess.ModeFlags.Write);
        file.StoreString(string.Join("\n", output));
    }
    private static string rewriteNodeHeader(string line, string parent, string rootName) {
        if (parent == "." && attributeOf(line, "name") == INHERITED_BASE_NAME) {
            var escaped = rootName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return line.Replace($"name=\"{INHERITED_BASE_NAME}\"", $"name=\"{escaped}\"").Replace(" parent=\".\"", "");
        }
        string rewritten;
        if (parent == INHERITED_BASE_NAME) rewritten = ".";
        else if (parent.StartsWith(INHERITED_BASE_NAME + "/")) rewritten = parent.Substring(INHERITED_BASE_NAME.Length + 1);
        else return line;
        return line.Replace($"parent=\"{parent}\"", $"parent=\"{rewritten}\"");
    }
    private static string attributeOf(string line, string attribute) {
        var marker = " " + attribute + "=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = start;
        while (end < line.Length && !(line[end] == '"' && line[end - 1] != '\\')) end++;
        return line.Substring(start, end - start);
    }
    
    // Scene tree
    private static T addChild<T>(Node parent, T child) where T : Node {
        parent.AddChild(child, true);
        return child;
    }
    private static void assignOwners(Node node, Node root) {
        foreach (var child in node.GetChildren()) {
            if (child.Owner == null) child.Owner = root;
            assignOwners(child, root);
        }
    }
    private static void collectNodes<T>(Node node, List<T> results) where T : Node {
        if (node is T match) results.Add(match);
        foreach (var child in node.GetChildren()) collectNodes(child, results);
    }
    private static MeshInstance3D findMeshInstance(Node node) {
        var meshes = new List<MeshInstance3D>();
        collectNodes(node, meshes);
        return meshes.FirstOrDefault();
    }
    private static Node findByName(Node root, string name) {
        var queue = new Queue<Node>();
        queue.Enqueue(root);
        while (queue.Count > 0) {
            var node = queue.Dequeue();
            if (node != root && namesMatch(node.Name, name)) return node;
            foreach (var child in node.GetChildren()) queue.Enqueue(child);
        }
        return null;
    }
    private static bool namesMatch(string godotName, string unityName) {
        if (string.IsNullOrEmpty(godotName) || string.IsNullOrEmpty(unityName)) return false;
        if (godotName.Equals(unityName, StringComparison.OrdinalIgnoreCase)) return true;
        // Godot's importers turn characters a node name can't hold into underscores (Door.L becomes Door_L); older ones dropped them
        return godotName.Equals(nodeName(unityName, "_"), StringComparison.OrdinalIgnoreCase) || godotName.Equals(nodeName(unityName, ""), StringComparison.OrdinalIgnoreCase);
    }
    private static string nodeName(string name, string replacement) {
        foreach (var invalid in INVALID_NODE_NAME_CHARACTERS) name = name.Replace(invalid, replacement);
        return name;
    }
    // Importers swap or drop punctuation in different ways; used only when the exact name finds nothing
    private static bool plainNamesMatch(string godotName, string unityName) {
        return !string.IsNullOrEmpty(godotName) && !string.IsNullOrEmpty(unityName) && plainName(godotName).Equals(plainName(unityName), StringComparison.OrdinalIgnoreCase);
    }
    private static string plainName(string name) {
        return new string(name.Where(char.IsLetterOrDigit).ToArray());
    }
    private static List<TValue> listFor<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key) {
        if (!map.TryGetValue(key, out var list)) {
            list = new List<TValue>();
            map[key] = list;
        }
        return list;
    }
    
    // Logging
    private void relayLog(string message) {
        if (message.StartsWith("WARNING: ")) warn(message.Substring(9));
        else note(message);
    }
    private void note(string message) {
        Logged?.Invoke(message);
    }
    private void warn(string message) {
        _warningCount++;
        Logged?.Invoke("WARNING: " + message);
    }
    private void trace(string message) {
        Traced?.Invoke(message);
    }
    private void warnOnce(string key, string message) {
        if (_loggedMessages.Add(key)) warn(message);
    }
}
#endif
