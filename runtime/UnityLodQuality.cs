using Godot;
using System.Collections.Generic;

// Autoload that turns Unity's LOD levels into a graphics quality setting: level 0 is the most detailed, 1, 2... are lighter.
// It applies to scenes loaded after it's set, so set Level and then load or reload the scene. Objects with fewer levels use their last one
public partial class UnityLodQuality : Node {
// ----- VALUES -----
    public static int Level {
        get => _level;
        set => _level = Mathf.Max(value, 0);
    }
    
    private static int _level;
    private readonly Dictionary<string, Resource> _loaded = new();
    private const string MESHES_META = "unity_quality_meshes";
    private const string MATERIALS_META = "unity_quality_materials";
    private const string SHADOWS_META = "unity_quality_shadows";
    private const string LEVELS_META = "unity_lod_levels";
    private const string COUNT_META = "unity_lod_count";
    
// ----- OVERRIDABLE -----
    public override void _EnterTree() {
        GetTree().NodeAdded += _nodeAdded;
    }
    public override void _ExitTree() {
        GetTree().NodeAdded -= _nodeAdded;
    }
    
// ----- HELPERS -----
    // Quality
    private void applyMeshes(MeshInstance3D node) {
        var paths = node.GetMeta(MESHES_META).AsStringArray();
        var index = Mathf.Min(_level, paths.Length - 1);
        if (index <= 0) return;
        // An empty path is a level drawn by other nodes, so this one hides
        if (paths[index] == "") {
            node.Visible = false;
            return;
        }
        if (load(paths[index]) is not Mesh mesh) return;
        node.Mesh = mesh;
        if (node.HasMeta(SHADOWS_META)) node.CastShadow = (GeometryInstance3D.ShadowCastingSetting)node.GetMeta(SHADOWS_META).AsInt32Array()[index];
        if (!node.HasMeta(MATERIALS_META)) return;
        var materials = node.GetMeta(MATERIALS_META).AsGodotArray()[index].AsStringArray();
        for (var i = 0; i < mesh.GetSurfaceCount(); i++) {
            node.SetSurfaceOverrideMaterial(i, i < materials.Length ? load(materials[i]) as Material : null);
        }
    }
    // Objects whose levels couldn't be merged into one node keep a node per level
    private void applyVisibility(GeometryInstance3D node) {
        var shown = Mathf.Min(_level, node.GetMeta(COUNT_META, 1).AsInt32() - 1);
        node.Visible = System.Array.IndexOf(node.GetMeta(LEVELS_META).AsInt32Array(), shown) >= 0;
    }
    private Resource load(string path) {
        if (string.IsNullOrEmpty(path)) return null;
        if (!_loaded.TryGetValue(path, out var resource)) {
            resource = ResourceLoader.Load(path);
            _loaded[path] = resource;
        }
        return resource;
    }
    
    // Signals
    private void _nodeAdded(Node node) {
        if (_level == 0 || node is not GeometryInstance3D geometry) return;
        if (geometry is MeshInstance3D meshNode && meshNode.HasMeta(MESHES_META)) applyMeshes(meshNode);
        else if (geometry.HasMeta(LEVELS_META)) applyVisibility(geometry);
    }
}
