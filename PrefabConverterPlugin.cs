#if TOOLS
using Godot;

namespace UnityPrefabConverter;

[Tool]
public partial class PrefabConverterPlugin : EditorPlugin {
// ----- VALUES -----
    private EditorDock _dock;
    
// ----- OVERRIDABLE -----
    public override void _EnterTree() {
        _dock = new EditorDock {
            Title = "Prefab Converter",
            LayoutKey = "unity_prefab_converter",
            DefaultSlot = EditorDock.DockSlot.RightUl
        };
        _dock.AddChild(new PrefabConverterDock());
        AddDock(_dock);
    }
    public override void _ExitTree() {
        RemoveDock(_dock);
        _dock.QueueFree();
        _dock = null;
    }
}
#endif
