#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;

namespace UnityPrefabConverter;

[Tool]
public partial class PrefabConverterDock : VBoxContainer {
// ----- VALUES -----
    private LineEdit _unityProjectEdit;
    private LineEdit _unityPrefabsEdit;
    private LineEdit _outputEdit;
    private CheckBox _overwriteCheck;
    private OptionButton _lodModeOption;
    private CheckBox _lodCullCheck;
    private CheckBox _externalDataCheck;
    private CheckBox _lodCrossfadeCheck;
    private SpinBox _lodScaleSpin;
    private CheckBox _prefabsCheck;
    private CheckBox _scenesCheck;
    private CheckBox _animationsCheck;
    private CheckBox _texturesCheck;
    private CheckBox _modelsCheck;
    private string _outputDir;
    private Button _convertButton;
    private Button _retryButton;
    private ProgressBar _progress;
    private Label _status;
    private TextEdit _logView;
    private Button _logButton;
    private FileDialog _dialog;
    private LineEdit _dialogTarget;
    private PrefabConverter _converter;
    private Queue<string> _queue;
    private Queue<(string Source, string Target)> _copies;
    private List<(string Source, string Target)> _assets;
    private Phase _phase = Phase.Idle;
    private int _quietFrames;
    private bool _rescanned;
    private enum Phase { Idle, Copying, Importing, Converting }
    private const int IMPORT_SETTLE_FRAMES = 30;
    private readonly List<string> _pendingLines = new();
    private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);
    private StreamWriter _logFile;
    private int _total;
    private int _copyTotal;
    private int _converted;
    private const string SETTINGS_SECTION = "unity_prefab_converter";
    private const double FRAME_BUDGET_MS = 50;
    private const string LOG_FILE_NAME = "conversion_log.txt";
    private const string QUALITY_AUTOLOAD = "UnityLodQuality";
    private const string SHADER_LIST_FILE_NAME = "unconverted_shaders.txt";
    private const string CONVERTING_PREFIX = "Converting ";
    private const string FINISHED_PREFIX = "Finished ";
    private const string VERSION = "3.21";
    
// ----- OVERRIDABLE -----
    public override void _Ready() {
        Name = "PrefabConverterDock";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        _unityProjectEdit = addPathRow("UnityProjectEdit", "Unity project", "Unity project root (the folder that contains Assets)", true);
        _unityPrefabsEdit = addPathRow("UnityPrefabsEdit", "Unity folder (optional)", "Only convert assets in this folder. Empty = the whole Assets folder", true);
        _outputEdit = addPathRow("OutputEdit", "Output", "Folder in this Godot project where the scenes are written", false);
        var kinds = new HFlowContainer { Name = "Kinds" };
        _prefabsCheck = new CheckBox { Name = "PrefabsCheck", Text = "Prefabs", ButtonPressed = true };
        _scenesCheck = new CheckBox { Name = "ScenesCheck", Text = "Scenes", ButtonPressed = true };
        _animationsCheck = new CheckBox { Name = "AnimationsCheck", Text = "Animations", ButtonPressed = true, TooltipText = "Convert every .anim and Animator Controller in the folder, not only the ones objects use" };
        kinds.AddChild(_prefabsCheck);
        kinds.AddChild(_scenesCheck);
        kinds.AddChild(_animationsCheck);
        _texturesCheck = new CheckBox { Name = "TexturesCheck", Text = "Textures", TooltipText = "Copy every texture in the folder, not only the ones objects use. PSD and TIFF files become PNG" };
        _modelsCheck = new CheckBox { Name = "ModelsCheck", Text = "Models", TooltipText = "Copy every model in the folder, with the materials it is set to use, not only the ones objects use" };
        kinds.AddChild(_texturesCheck);
        kinds.AddChild(_modelsCheck);
        AddChild(kinds);
        _overwriteCheck = new CheckBox { Name = "OverwriteCheck", Text = "Overwrite existing files", ButtonPressed = true };
        AddChild(_overwriteCheck);
        _lodModeOption = new OptionButton { Name = "LodModeOption", TooltipText = "Quality levels: only the most detailed Unity level shows and Godot's own mesh LOD handles distance; UnityLodQuality.Apply(tree, level) switches every object to a lighter level.\nSwitch by distance: Unity's levels swap by camera distance (visibility ranges).\nSeparate scenes: prefabs get one scene per level (Name_LOD1.tscn, ...)" };
        _lodModeOption.AddItem("LODs: quality levels", (int)PrefabConverter.LodModeKind.Quality);
        _lodModeOption.AddItem("LODs: switch by distance", (int)PrefabConverter.LodModeKind.Ranges);
        _lodModeOption.AddItem("LODs: separate scenes", (int)PrefabConverter.LodModeKind.Separate);
        AddChild(_lodModeOption);
        var lods = new HBoxContainer { Name = "Lods" };
        _lodCullCheck = new CheckBox { Name = "LodCullCheck", Text = "Hide past cull distance", ButtonPressed = true, TooltipText = "With quality levels, objects still disappear at the distance where Unity's LOD Group culls them" };
        _lodCrossfadeCheck = new CheckBox { Name = "LodCrossfadeCheck", Text = "Fade", TooltipText = "Objects fade in and out at LOD and cull distances instead of switching at once.\nThey are drawn as transparent while they fade" };
        _lodScaleSpin = new SpinBox { Name = "LodScaleSpin", MinValue = 0.1, MaxValue = 10, Step = 0.05, Value = 1, Prefix = "LOD distance ×", TooltipText = "Multiplies every LOD and cull distance, on top of the LOD bias read from Unity's quality settings" };
        lods.AddChild(_lodCullCheck);
        lods.AddChild(_lodCrossfadeCheck);
        lods.AddChild(_lodScaleSpin);
        AddChild(lods);
        _externalDataCheck = new CheckBox { Name = "ExternalDataCheck", Text = "Save large data as .res files", ButtonPressed = true, TooltipText = "Meshes, collision shapes, images and animations above 4 KB that are built into a scene are saved as binary .res files in a Name_Data folder next to it.\nKeeps the .tscn small, so it saves and loads faster" };
        AddChild(_externalDataCheck);
        _convertButton = new Button { Name = "ConvertButton", Text = "Convert" };
        _convertButton.Pressed += _convertPressed;
        AddChild(_convertButton);
        _retryButton = new Button { Name = "RetryButton", TooltipText = "Assets that closed Godot during an earlier run are skipped. Click to include them again." };
        _retryButton.Pressed += _retryPressed;
        AddChild(_retryButton);
        _progress = new ProgressBar { Name = "Progress", Visible = false, ShowPercentage = true };
        AddChild(_progress);
        _status = new Label { Name = "Status", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_status);
        _logButton = new Button { Name = "LogButton", ToggleMode = true, Text = "Show log", TooltipText = "The full log is also written to conversion_log.txt in the output folder" };
        _logButton.Toggled += _logToggled;
        AddChild(_logButton);
        _logView = new TextEdit {
            Name = "LogView",
            Visible = false,
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 220)
        };
        AddChild(_logView);
        _dialog = new FileDialog { Name = "FolderDialog", FileMode = FileDialog.FileModeEnum.OpenDir, Title = "Select folder" };
        _dialog.DirSelected += _dirSelected;
        AddChild(_dialog);
        loadSettings();
        updateRetryButton();
        SetProcess(false);
    }
    public override void _Process(double delta) {
        if (_queue == null || _converter == null) {
            SetProcess(false);
            return;
        }
        if (_phase == Phase.Copying) {
            copyAssets();
            return;
        }
        var filesystem = EditorInterface.Singleton.GetResourceFilesystem();
        if (filesystem.IsScanning() || filesystem.IsImporting()) {
            _quietFrames = 0;
            setStatus(_phase == Phase.Importing ? $"Godot is importing {_assets.Count} models and textures..." : "Waiting for Godot to finish importing...");
            return;
        }
        if (_phase == Phase.Importing) {
            waitForImport();
            return;
        }
        _status.Visible = false;
        var timer = Stopwatch.StartNew();
        while (_queue.Count > 0 && timer.Elapsed.TotalMilliseconds < FRAME_BUDGET_MS) {
            var prefab = _queue.Dequeue();
            _logFile?.WriteLine(CONVERTING_PREFIX + prefab);
            try {
                if (_converter.ConvertAsset(prefab) != null) _converted++;
            } catch (Exception exception) {
                appendLog($"WARNING: {Path.GetFileName(prefab)} failed: {exception.Message}");
            }
            _logFile?.WriteLine(FINISHED_PREFIX + prefab);
            _progress.Value = _total - _queue.Count;
        }
        flushLog();
        if (_queue.Count == 0) finishConversion($"Converted {_converted}/{_total} assets and copied {_copyTotal} models and textures, with {_converter.WarningCount} warning(s).");
    }
    public override void _ExitTree() {
        _converter?.Cleanup();
        _logFile?.Dispose();
        _logFile = null;
    }
    
// ----- HELPERS -----
    // Layout
    private LineEdit addPathRow(string name, string label, string tooltip, bool filesystem) {
        AddChild(new Label { Text = label, TooltipText = tooltip });
        var row = new HBoxContainer();
        var edit = new LineEdit { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = tooltip, PlaceholderText = filesystem ? "C:/..." : "res://..." };
        var browse = new Button { Text = "..." };
        browse.Pressed += () => openDialog(edit, filesystem);
        row.AddChild(edit);
        row.AddChild(browse);
        AddChild(row);
        return edit;
    }
    private void openDialog(LineEdit target, bool filesystem) {
        ensureControls();
        _dialogTarget = target;
        _dialog.Access = filesystem ? FileDialog.AccessEnum.Filesystem : FileDialog.AccessEnum.Resources;
        if (!string.IsNullOrWhiteSpace(target.Text)) _dialog.CurrentDir = target.Text;
        _dialog.PopupCentered(new Vector2I(900, 600));
    }
    // Rebuilding the C# project while the editor is open reloads this script and clears its fields; the controls themselves survive
    private void ensureControls() {
        if (_dialog != null && IsInstanceValid(_dialog)) return;
        _unityProjectEdit = FindChild("UnityProjectEdit", true, false) as LineEdit;
        _unityPrefabsEdit = FindChild("UnityPrefabsEdit", true, false) as LineEdit;
        _outputEdit = FindChild("OutputEdit", true, false) as LineEdit;
        _overwriteCheck = GetNodeOrNull<CheckBox>("OverwriteCheck");
        _lodModeOption = GetNodeOrNull<OptionButton>("LodModeOption");
        _lodCullCheck = FindChild("LodCullCheck", true, false) as CheckBox;
        _externalDataCheck = GetNodeOrNull<CheckBox>("ExternalDataCheck");
        _lodCrossfadeCheck = FindChild("LodCrossfadeCheck", true, false) as CheckBox;
        _lodScaleSpin = FindChild("LodScaleSpin", true, false) as SpinBox;
        _prefabsCheck = FindChild("PrefabsCheck", true, false) as CheckBox;
        _scenesCheck = FindChild("ScenesCheck", true, false) as CheckBox;
        _animationsCheck = FindChild("AnimationsCheck", true, false) as CheckBox;
        _texturesCheck = FindChild("TexturesCheck", true, false) as CheckBox;
        _modelsCheck = FindChild("ModelsCheck", true, false) as CheckBox;
        _convertButton = GetNodeOrNull<Button>("ConvertButton");
        _retryButton = GetNodeOrNull<Button>("RetryButton");
        _progress = GetNodeOrNull<ProgressBar>("Progress");
        _status = GetNodeOrNull<Label>("Status");
        _logView = GetNodeOrNull<TextEdit>("LogView");
        _logButton = GetNodeOrNull<Button>("LogButton");
        _dialog = GetNodeOrNull<FileDialog>("FolderDialog");
        _skipped.Clear();
        foreach (var path in EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata(SETTINGS_SECTION, "skipped", Array.Empty<string>()).AsStringArray()) _skipped.Add(path);
        _progress.Visible = false;
        _convertButton.Text = "Convert";
    }
    private void updateRetryButton() {
        _retryButton.Text = $"Retry {_skipped.Count} skipped asset(s)";
        _retryButton.Visible = _skipped.Count > 0 && _queue == null;
    }
    
    // Settings
    private void loadSettings() {
        var settings = EditorInterface.Singleton.GetEditorSettings();
        _unityProjectEdit.Text = settings.GetProjectMetadata(SETTINGS_SECTION, "unity_project", "").AsString();
        _unityPrefabsEdit.Text = settings.GetProjectMetadata(SETTINGS_SECTION, "unity_prefabs", "").AsString();
        _outputEdit.Text = settings.GetProjectMetadata(SETTINGS_SECTION, "output", "res://Prefabs").AsString();
        _overwriteCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "overwrite", true).AsBool();
        _lodModeOption.Select(_lodModeOption.GetItemIndex(settings.GetProjectMetadata(SETTINGS_SECTION, "lod_mode", (int)PrefabConverter.LodModeKind.Quality).AsInt32()));
        _lodCullCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "lod_cull", true).AsBool();
        _externalDataCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "external_data", true).AsBool();
        _lodCrossfadeCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "lod_fade", false).AsBool();
        _lodScaleSpin.Value = settings.GetProjectMetadata(SETTINGS_SECTION, "lod_scale", 1.0).AsDouble();
        _prefabsCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "prefabs", true).AsBool();
        _scenesCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "scenes", true).AsBool();
        _animationsCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "animations", true).AsBool();
        _texturesCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "textures", false).AsBool();
        _modelsCheck.ButtonPressed = settings.GetProjectMetadata(SETTINGS_SECTION, "models", false).AsBool();
        _skipped.Clear();
        foreach (var path in settings.GetProjectMetadata(SETTINGS_SECTION, "skipped", Array.Empty<string>()).AsStringArray()) _skipped.Add(path);
    }
    private void saveSettings() {
        var settings = EditorInterface.Singleton.GetEditorSettings();
        settings.SetProjectMetadata(SETTINGS_SECTION, "unity_project", _unityProjectEdit.Text);
        settings.SetProjectMetadata(SETTINGS_SECTION, "unity_prefabs", _unityPrefabsEdit.Text);
        settings.SetProjectMetadata(SETTINGS_SECTION, "output", _outputEdit.Text);
        settings.SetProjectMetadata(SETTINGS_SECTION, "overwrite", _overwriteCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "lod_mode", _lodModeOption.GetSelectedId());
        settings.SetProjectMetadata(SETTINGS_SECTION, "lod_cull", _lodCullCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "external_data", _externalDataCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "lod_fade", _lodCrossfadeCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "lod_scale", _lodScaleSpin.Value);
        settings.SetProjectMetadata(SETTINGS_SECTION, "prefabs", _prefabsCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "scenes", _scenesCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "animations", _animationsCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "textures", _texturesCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "models", _modelsCheck.ButtonPressed);
        settings.SetProjectMetadata(SETTINGS_SECTION, "skipped", _skipped.ToArray());
    }
    
    // Conversion
    private void startConversion() {
        var unityProject = cleanPath(_unityProjectEdit.Text);
        var unityPrefabs = string.IsNullOrWhiteSpace(_unityPrefabsEdit.Text) ? Path.Combine(unityProject, "Assets") : cleanPath(_unityPrefabsEdit.Text);
        var output = toResPath(_outputEdit.Text).TrimEnd('/');
        if (!Directory.Exists(Path.Combine(unityProject, "Assets"))) {
            setStatus("Unity project not found (expected a folder containing Assets).");
            return;
        }
        if (!Directory.Exists(unityPrefabs)) {
            setStatus("Unity folder not found.");
            return;
        }
        if (!output.StartsWith("res://") || output == "res:/") {
            setStatus("Output must be a folder inside this Godot project.");
            return;
        }
        DirAccess.MakeDirRecursiveAbsolute(output);
        var logPath = ProjectSettings.GlobalizePath(output + "/" + LOG_FILE_NAME);
        var crashed = findCrashedPrefab(logPath);
        if (crashed != null && _skipped.Add(crashed)) saveSettings();
        _logFile = new StreamWriter(logPath, false) { AutoFlush = true };
        appendLog($"Unity Prefab Converter {VERSION}");
        if (crashed != null) appendLog($"Godot closed while converting {Path.GetFileName(crashed)} last time; it will be skipped.");
        var index = new UnityAssetIndex(unityProject);
        var paths = new OutputPaths(unityProject, output);
        _converter = new PrefabConverter(index, paths, unityPrefabs) { Overwrite = _overwriteCheck.ButtonPressed, LodMode = (PrefabConverter.LodModeKind)_lodModeOption.GetSelectedId(), LodCull = _lodCullCheck.ButtonPressed, ExternalData = _externalDataCheck.ButtonPressed,
            LodCrossfade = _lodCrossfadeCheck.ButtonPressed, LodDistanceScale = (float)_lodScaleSpin.Value };
        _converter.Logged += appendLog;
        if (_converter.LodMode == PrefabConverter.LodModeKind.Quality) ensureQualityAutoload();
        _converter.Traced += _traced;
        _outputDir = output;
        var prefabs = _converter.FindSources(_prefabsCheck.ButtonPressed, _scenesCheck.ButtonPressed, _animationsCheck.ButtonPressed);
        _queue = new Queue<string>(prefabs.Where(p => !_skipped.Contains(p)));
        _total = _queue.Count;
        _converted = 0;
        appendLog($"Indexed {index.Count} Unity assets, found {prefabs.Count} prefabs, scenes and animations ({prefabs.Count - _total} skipped).");
        var collector = new AssetCollector(index, paths);
        collector.Logged += appendLog;
        foreach (var prefab in _queue) collector.Collect(prefab);
        if (_texturesCheck.ButtonPressed || _modelsCheck.ButtonPressed) {
            var added = collector.CollectFolder(unityPrefabs, _texturesCheck.ButtonPressed, _modelsCheck.ButtonPressed);
            appendLog($"The folder holds {added} more textures and models.");
        }
        _assets = collector.Copies;
        _copies = new Queue<(string, string)>(_assets.Where(c => AssetCollector.NeedsCopy(c.Source, c.Target)));
        _copyTotal = _copies.Count;
        appendLog($"{_assets.Count} models and textures in all; {_copies.Count} need copying.");
        _progress.Visible = true;
        _convertButton.Text = "Cancel";
        updateRetryButton();
        startPhase(Phase.Copying, _copies.Count);
        SetProcess(true);
    }
    // Quality levels need the runtime autoload to load the coarser meshes
    private void ensureQualityAutoload() {
        var setting = "autoload/" + QUALITY_AUTOLOAD;
        if (ProjectSettings.HasSetting(setting)) return;
        var path = (GetScript().As<Script>()?.ResourcePath ?? "res://addons/unity_prefab_converter/PrefabConverterDock.cs").GetBaseDir() + "/runtime/UnityLodQuality.cs";
        ProjectSettings.SetSetting(setting, "*" + path);
        ProjectSettings.Save();
        appendLog($"Added the {QUALITY_AUTOLOAD} autoload ({path}), which loads Unity's lower LOD levels for lower quality settings.");
    }
    private void startPhase(Phase phase, int steps) {
        _phase = phase;
        _progress.MaxValue = Math.Max(steps, 1);
        _progress.Value = 0;
        if (phase == Phase.Converting) _status.Visible = false;
    }
    private void copyAssets() {
        var timer = Stopwatch.StartNew();
        while (_copies.Count > 0 && timer.Elapsed.TotalMilliseconds < FRAME_BUDGET_MS) {
            var (source, target) = _copies.Dequeue();
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
                if (AssetCollector.IsConvertedTexture(source)) {
                    _logFile?.WriteLine("    Converting texture " + source);
                    var error = AssetCollector.ConvertToPng(source, target);
                    if (error != null) appendLog($"WARNING: Could not convert {Path.GetFileName(source)} to PNG: {error}");
                } else {
                    File.Copy(source, target, true);
                }
            } catch (Exception exception) {
                appendLog($"WARNING: Could not copy {Path.GetFileName(source)}: {exception.Message}");
            }
            _progress.Value = _progress.MaxValue - _copies.Count;
        }
        setStatus($"Copying assets ({_copies.Count} left)...");
        flushLog();
        if (_copies.Count > 0) return;
        _quietFrames = 0;
        _rescanned = false;
        startPhase(Phase.Importing, 1);
        EditorInterface.Singleton.GetResourceFilesystem().Scan();
    }
    // Godot imports new files on its own after a scan; conversion waits until it has been idle for a moment
    private void waitForImport() {
        if (++_quietFrames < IMPORT_SETTLE_FRAMES) return;
        var missing = _assets.Where(a => !File.Exists(a.Target + ".import")).ToList();
        if (missing.Count > 0 && !_rescanned) {
            _rescanned = true;
            _quietFrames = 0;
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
            return;
        }
        foreach (var (source, _) in missing) appendLog($"WARNING: Godot did not import {Path.GetFileName(source)}");
        appendLog("Assets imported, converting prefabs.");
        startPhase(Phase.Converting, _total);
    }
    private void setStatus(string text) {
        _status.Text = text;
        _status.Visible = true;
    }
    private void finishConversion(string summary) {
        SetProcess(false);
        _queue = null;
        _copies = null;
        _phase = Phase.Idle;
        appendLog(summary);
        writeShaderList();
        _converter?.Cleanup();
        _converter = null;
        _logFile?.Dispose();
        _logFile = null;
        _progress.Visible = false;
        setStatus(summary);
        _convertButton.Text = "Convert";
        updateRetryButton();
        EditorInterface.Singleton.GetResourceFilesystem().Scan();
    }
    private void writeShaderList() {
        if (_converter == null || _outputDir == null) return;
        var path = ProjectSettings.GlobalizePath(_outputDir + "/" + SHADER_LIST_FILE_NAME);
        var shaders = _converter.UnconvertedShaders;
        if (shaders.Count == 0) {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllLines(path, shaders.Select(s => $"{s.Key} -> {s.Value}"));
        appendLog($"{shaders.Count} custom shader(s) have no Godot shader in _Shaders and were approximated; they're listed in {SHADER_LIST_FILE_NAME}.");
    }
    private static string findCrashedPrefab(string logPath) {
        if (!File.Exists(logPath)) return null;
        string open = null;
        foreach (var line in File.ReadLines(logPath)) {
            if (line.StartsWith(CONVERTING_PREFIX)) open = line.Substring(CONVERTING_PREFIX.Length);
            else if (line.StartsWith(FINISHED_PREFIX)) open = null;
        }
        return open;
    }
    
    // Log
    private void appendLog(string message) {
        _pendingLines.Add(message);
        _logFile?.WriteLine(message);
        if (_queue == null) flushLog();
    }
    private void flushLog() {
        if (_pendingLines.Count == 0) return;
        _logView.Text += (_logView.Text.Length > 0 ? "\n" : "") + string.Join("\n", _pendingLines);
        _pendingLines.Clear();
        _logView.ScrollVertical = _logView.GetLineCount();
    }
    private static string cleanPath(string path) {
        return path.Trim().Trim('"');
    }
    private static string toResPath(string path) {
        path = cleanPath(path).Replace('\\', '/');
        return path.StartsWith("res://") ? path : ProjectSettings.LocalizePath(path);
    }
    
    // Signals
    private void _convertPressed() {
        ensureControls();
        if (_queue != null) {
            finishConversion("Cancelled.");
            return;
        }
        saveSettings();
        _logView.Text = "";
        try {
            startConversion();
        } catch (Exception exception) {
            appendLog("Conversion failed: " + exception);
            _logFile?.Dispose();
            _logFile = null;
        }
    }
    private void _logToggled(bool shown) {
        ensureControls();
        _logView.Visible = shown;
        _logButton.Text = shown ? "Hide log" : "Show log";
    }
    private void _retryPressed() {
        ensureControls();
        _skipped.Clear();
        saveSettings();
        updateRetryButton();
    }
    private void _traced(string message) {
        _logFile?.WriteLine("    " + message);
    }
    private void _dirSelected(string directory) {
        ensureControls();
        if (_dialogTarget != null) _dialogTarget.Text = directory;
    }
}
#endif
