using Godot;

// Picks which of Unity's LOD levels every converted object shows: 0 is the most detailed, higher levels are lighter.
// Objects with fewer levels show their last one. Call Apply again after adding converted scenes to the tree
public static class UnityLodQuality {
// ----- VALUES -----
    public static int Level { get; private set; }
    private const string GROUP = "unity_lod";
    private const string LEVELS_META = "unity_lod_levels";
    private const string COUNT_META = "unity_lod_count";
    
// ----- PUBLIC CALLS -----
    // Quality
    public static void Apply(SceneTree tree, int level) {
        Level = Mathf.Max(level, 0);
        foreach (var node in tree.GetNodesInGroup(GROUP)) {
            if (node is not Node3D target || !target.HasMeta(LEVELS_META)) continue;
            var shown = Mathf.Min(Level, target.GetMeta(COUNT_META, 1).AsInt32() - 1);
            target.Visible = System.Array.IndexOf(target.GetMeta(LEVELS_META).AsInt32Array(), shown) >= 0;
        }
    }
}
