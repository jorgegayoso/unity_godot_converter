#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace UnityPrefabConverter;

// Converts AnimationClips (.anim or inside models) into Animations and Animator Controllers into AnimationLibraries
public sealed class AnimationConverter {
// ----- VALUES -----
    public bool Overwrite = true;
    public event Action<string> Logged;
    public event Action<string> Traced;
    private readonly UnityAssetIndex _index;
    private readonly OutputPaths _paths;
    private readonly Func<string, Node> _modelInstance;
    private readonly Dictionary<string, Animation> _clips = new();
    private readonly Dictionary<string, (AnimationLibrary Library, string DefaultClip)> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new();
    private readonly Dictionary<string, List<Animation>> _modelAnimations = new(StringComparer.OrdinalIgnoreCase);
    private List<(string Path, HashSet<string> Targets)> _clipFiles;
    private static readonly System.Text.RegularExpressions.Regex CURVE_PATH_REGEX = new(@"^\s+path: ?(.*)$", System.Text.RegularExpressions.RegexOptions.Multiline);
    private const int CLASS_ANIMATION_CLIP = 74;
    private const int CLASS_ANIMATOR_CONTROLLER = 91;
    private const int CLASS_BLEND_TREE = 206;
    private const int CLASS_OVERRIDE_CONTROLLER = 221;
    private const double MAX_SAMPLE_RATE = 60;
    private const float KEY_EPSILON = 1e-5f;
    
    private sealed class Key {
        public double Time;
        public double[] Value;
        public double[] InSlope;
        public double[] OutSlope;
    }
    
// ----- PUBLIC CALLS -----
    // Setup
    public AnimationConverter(UnityAssetIndex index, OutputPaths paths, Func<string, Node> modelInstance) {
        _index = index;
        _paths = paths;
        _modelInstance = modelInstance;
    }
    
    // Conversion
    public Animation ConvertClip(UnityRef clipRef) {
        if (clipRef.IsNull) return null;
        var key = $"{clipRef.Guid}:{clipRef.FileId}";
        if (_clips.TryGetValue(key, out var cached)) return cached;
        Animation animation = null;
        var unityPath = _index.GetPath(clipRef.Guid);
        if (unityPath == null) {
            warnOnce($"clip:{key}", $"Animation clip {clipRef.Guid} not found in the Unity project");
        } else if (unityPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) {
            animation = saveOrLoad(_paths.ResPath(unityPath, ".res"), () => bakeClipFile(unityPath));
        } else if (AssetCollector.IsModel(unityPath)) {
            var name = clipName(unityPath, clipRef.FileId);
            var target = _paths.ResPathWithoutExtension(unityPath) + "_Animations/" + sanitize(name ?? "Clip" + clipRef.FileId) + ".res";
            animation = saveOrLoad(target, () => extractModelClip(unityPath, clipRef.FileId, name));
        }
        _clips[key] = animation;
        return animation;
    }
    public (AnimationLibrary Library, string DefaultClip) ConvertController(string controllerPath) {
        if (_controllers.TryGetValue(controllerPath, out var cached)) return cached;
        _controllers[controllerPath] = (null, null);
        var states = new List<(string State, UnityRef Clip)>();
        string defaultState;
        try {
            defaultState = collectControllerStates(controllerPath, states, new Dictionary<string, UnityRef>());
        } catch (Exception exception) {
            warnOnce($"controller:{controllerPath}", $"Could not read {Path.GetFileName(controllerPath)}: {exception.Message}");
            return (null, null);
        }
        var library = new AnimationLibrary();
        string defaultClip = null;
        foreach (var (state, clipRef) in states) {
            var animation = ConvertClip(clipRef);
            if (animation == null) continue;
            var name = uniqueName(library, string.IsNullOrEmpty(animation.ResourceName) ? state : animation.ResourceName);
            if (!library.HasAnimation(name)) library.AddAnimation(name, animation);
            if (state == defaultState && defaultClip == null) defaultClip = name;
        }
        var path = _paths.ResPath(controllerPath, ".tres");
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path)) ?? "");
        if (ResourceSaver.Save(library, path) == Error.Ok) library = ResourceLoader.Load<AnimationLibrary>(path, null, ResourceLoader.CacheMode.Replace) ?? library;
        _controllers[controllerPath] = (library, defaultClip);
        return (library, defaultClip);
    }
    // Every clip Godot imported from a model, named and trimmed as in Unity's import settings
    public List<Animation> ModelAnimations(string modelPath) {
        if (_modelAnimations.TryGetValue(modelPath, out var cached)) return cached;
        var result = new List<Animation>();
        _modelAnimations[modelPath] = result;
        var clips = _index.GetModelMeta(modelPath).Clips;
        var names = clips.Count > 0 ? clips.Select(c => c.Name).ToList() : godotAnimationNames(modelPath);
        foreach (var name in names) {
            var target = _paths.ResPathWithoutExtension(modelPath) + "_Animations/" + sanitize(name) + ".res";
            var animation = saveOrLoad(target, () => extractModelClip(modelPath, 0, name));
            if (animation != null) result.Add(animation);
        }
        return result;
    }
    public List<string> CompanionModels(string modelPath) {
        return _index.CompanionModels(modelPath);
    }
    // .anim files from the same asset pack whose animated paths all exist under the animated object
    public List<Animation> MatchingClipFiles(HashSet<string> hierarchyPaths, string scopeFolder) {
        if (_clipFiles == null) {
            _clipFiles = new List<(string, HashSet<string>)>();
            foreach (var path in _index.AllPaths.Where(p => p.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))) {
                try {
                    var targets = CURVE_PATH_REGEX.Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value.Trim()).Where(t => t.Length > 0).ToHashSet();
                    if (targets.Count > 0) _clipFiles.Add((path, targets));
                } catch (IOException) {
                }
            }
        }
        var result = new List<Animation>();
        foreach (var (path, targets) in _clipFiles) {
            if (!path.StartsWith(scopeFolder, StringComparison.OrdinalIgnoreCase) || !targets.All(hierarchyPaths.Contains)) continue;
            var animation = ConvertClip(new UnityRef(7400000, _index.GetGuid(path)));
            if (animation != null) result.Add(animation);
        }
        return result;
    }
    public AnimationLibrary BuildLibrary(IEnumerable<UnityRef> clips) {
        var library = new AnimationLibrary();
        foreach (var clipRef in clips) {
            var animation = ConvertClip(clipRef);
            if (animation == null) continue;
            var name = uniqueName(library, string.IsNullOrEmpty(animation.ResourceName) ? "Clip" : animation.ResourceName);
            library.AddAnimation(name, animation);
        }
        return library;
    }
    
// ----- HELPERS -----
    // Saving
    private Animation saveOrLoad(string path, Func<Animation> build) {
        if (!Overwrite && Godot.FileAccess.FileExists(path)) return ResourceLoader.Load<Animation>(path);
        var animation = build();
        if (animation == null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path)) ?? "");
        var error = ResourceSaver.Save(animation, path);
        if (error != Error.Ok) {
            warnOnce($"save:{path}", $"Could not save animation {path}: {error}");
            return animation;
        }
        return ResourceLoader.Load<Animation>(path, null, ResourceLoader.CacheMode.Replace) ?? animation;
    }
    
    // Controllers
    private string collectControllerStates(string path, List<(string, UnityRef)> states, Dictionary<string, UnityRef> overrides) {
        var documents = new Dictionary<long, UnityDocument>();
        foreach (var document in UnityYaml.ParseFile(path)) documents[document.FileId] = document;
        var root = documents.Values.FirstOrDefault(d => d.ClassId == CLASS_ANIMATOR_CONTROLLER || d.ClassId == CLASS_OVERRIDE_CONTROLLER);
        if (root == null) return null;
        if (root.ClassId == CLASS_OVERRIDE_CONTROLLER) {
            foreach (var item in (UnityYaml.GetList(root.Body, "m_Clips") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
                var original = UnityYaml.GetRef(item, "m_OriginalClip");
                var replacement = UnityYaml.GetRef(item, "m_OverrideClip");
                if (!original.IsNull && !replacement.IsNull) overrides[$"{original.Guid}:{original.FileId}"] = replacement;
            }
            var basePath = _index.GetPath(UnityYaml.GetRef(root.Body, "m_Controller").Guid);
            return basePath == null ? null : collectControllerStates(basePath, states, overrides);
        }
        string defaultState = null;
        var guid = _index.GetGuid(path);
        var layers = (UnityYaml.GetList(root.Body, "m_AnimatorLayers") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
        for (var i = 0; i < layers.Count; i++) {
            var machine = UnityYaml.GetRef(layers[i], "m_StateMachine");
            Action<string> setDefault = i == 0 ? state => defaultState ??= state : null;
            collectMachine(documents, machine.FileId, guid, states, setDefault, overrides, 0);
        }
        if (layers.Count > 1) noteOnce($"layers:{path}", $"{Path.GetFileName(path)} has {layers.Count} layers; their clips are all included but layer blending isn't converted");
        return defaultState;
    }
    private void collectMachine(Dictionary<long, UnityDocument> documents, long machineId, string guid, List<(string, UnityRef)> states, Action<string> setDefault, Dictionary<string, UnityRef> overrides, int depth) {
        if (depth > 32 || !documents.TryGetValue(machineId, out var machine)) return;
        var defaultId = UnityYaml.GetRef(machine.Body, "m_DefaultState").FileId;
        foreach (var child in (UnityYaml.GetList(machine.Body, "m_ChildStates") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            var stateId = UnityYaml.GetRef(child, "m_State").FileId;
            if (!documents.TryGetValue(stateId, out var state)) continue;
            var name = UnityYaml.GetString(state.Body, "m_Name", "State");
            var count = states.Count;
            collectMotion(documents, UnityYaml.GetRef(state.Body, "m_Motion"), guid, name, states, overrides, 0);
            if (stateId == defaultId && states.Count > count) setDefault?.Invoke(name);
        }
        foreach (var child in (UnityYaml.GetList(machine.Body, "m_ChildStateMachines") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            collectMachine(documents, UnityYaml.GetRef(child, "m_StateMachine").FileId, guid, states, setDefault, overrides, depth + 1);
        }
    }
    private void collectMotion(Dictionary<long, UnityDocument> documents, UnityRef motion, string guid, string stateName, List<(string, UnityRef)> states, Dictionary<string, UnityRef> overrides, int depth) {
        if (motion.IsNull || depth > 16) return;
        if (motion.Guid == null || motion.Guid == guid) {
            if (!documents.TryGetValue(motion.FileId, out var tree) || tree.ClassId != CLASS_BLEND_TREE) return;
            noteOnce($"blend:{stateName}", $"Blend tree in state '{stateName}' converted as its separate clips");
            foreach (var child in (UnityYaml.GetList(tree.Body, "m_Childs") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
                collectMotion(documents, UnityYaml.GetRef(child, "m_Motion"), guid, stateName, states, overrides, depth + 1);
            }
            return;
        }
        if (overrides.TryGetValue($"{motion.Guid}:{motion.FileId}", out var replacement)) motion = replacement;
        states.Add((stateName, motion));
    }
    
    // .anim files
    private Animation bakeClipFile(string path) {
        Traced?.Invoke($"Converting animation {Path.GetFileName(path)}");
        var body = UnityYaml.ParseFile(path).FirstOrDefault(d => d.ClassId == CLASS_ANIMATION_CLIP)?.Body;
        if (body == null) return null;
        var name = UnityYaml.GetString(body, "m_Name", Path.GetFileNameWithoutExtension(path));
        var settings = UnityYaml.GetMap(body, "m_AnimationClipSettings");
        var start = UnityYaml.GetDouble(settings, "m_StartTime");
        var stop = UnityYaml.GetDouble(settings, "m_StopTime", start);
        var rate = Math.Clamp(UnityYaml.GetDouble(body, "m_SampleRate", 60), 1, MAX_SAMPLE_RATE);
        var curves = new List<(string Kind, string Path, List<Key> Keys, string Attribute, long ClassId)>();
        foreach (var kind in new[] { "m_PositionCurves", "m_RotationCurves", "m_EulerCurves", "m_ScaleCurves", "m_FloatCurves" }) {
            foreach (var item in (UnityYaml.GetList(body, kind) ?? new List<object>()).OfType<Dictionary<string, object>>()) {
                var keys = readKeys(UnityYaml.GetMap(item, "curve"));
                if (keys.Count == 0) continue;
                curves.Add((kind, UnityYaml.GetString(item, "path"), keys, UnityYaml.GetString(item, "attribute"), UnityYaml.GetLong(item, "classID")));
                stop = Math.Max(stop, keys[^1].Time);
            }
        }
        var length = Math.Max(stop - start, 1.0 / rate);
        var animation = new Animation {
            ResourceName = name,
            Length = (float)length,
            LoopMode = UnityYaml.GetBool(settings, "m_LoopTime") ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None,
            Step = (float)(1 / rate)
        };
        var rotated = curves.Where(c => c.Kind == "m_RotationCurves").Select(c => c.Path).ToHashSet();
        var skipped = 0;
        foreach (var curve in curves) {
            var nodePath = toNodePath(curve.Path);
            switch (curve.Kind) {
                case "m_PositionCurves":
                    addSampledTrack(animation, Animation.TrackType.Position3D, nodePath, curve.Keys, start, length, rate, v => new Vector3(-(float)v[0], (float)v[1], (float)v[2]));
                    break;
                case "m_RotationCurves":
                    addSampledTrack(animation, Animation.TrackType.Rotation3D, nodePath, curve.Keys, start, length, rate, v => toGodotRotation(new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3])));
                    break;
                case "m_EulerCurves":
                    if (rotated.Contains(curve.Path)) break;
                    addSampledTrack(animation, Animation.TrackType.Rotation3D, nodePath, curve.Keys, start, length, rate, v => toGodotRotation(unityEuler(v[0], v[1], v[2])));
                    break;
                case "m_ScaleCurves":
                    addSampledTrack(animation, Animation.TrackType.Scale3D, nodePath, curve.Keys, start, length, rate, v => new Vector3((float)v[0], (float)v[1], (float)v[2]));
                    break;
                default:
                    if (curve.Attribute == "m_IsActive" && curve.ClassId == 1) addActiveTrack(animation, nodePath, curve.Keys, start);
                    else if (curve.ClassId == 95) noteOnce($"humanoid:{path}", $"{Path.GetFileName(path)} is a humanoid clip; humanoid muscle curves can't be converted");
                    else skipped++;
                    break;
            }
        }
        if (skipped > 0) noteOnce($"curves:{path}", $"{Path.GetFileName(path)}: {skipped} curve(s) on non-transform properties were skipped");
        return animation;
    }
    private static List<Key> readKeys(Dictionary<string, object> curve) {
        var keys = new List<Key>();
        foreach (var item in (UnityYaml.GetList(curve, "m_Curve") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            keys.Add(new Key {
                Time = UnityYaml.GetDouble(item, "time"),
                Value = readComponents(item, "value"),
                InSlope = readComponents(item, "inSlope"),
                OutSlope = readComponents(item, "outSlope")
            });
        }
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        return keys;
    }
    private static double[] readComponents(Dictionary<string, object> key, string name) {
        if (!key.TryGetValue(name, out var value)) return new double[] { 0 };
        if (value is string text) return new[] { UnityYaml.ParseDouble(text, 0) };
        if (value is not Dictionary<string, object> map) return new double[] { 0 };
        return new[] { "x", "y", "z", "w" }.Where(map.ContainsKey).Select(c => UnityYaml.GetDouble(map, c)).ToArray();
    }
    private void addSampledTrack<T>(Animation animation, Animation.TrackType type, string path, List<Key> keys, double start, double length, double rate, Func<double[], T> convert) {
        var track = animation.AddTrack(type);
        animation.TrackSetPath(track, path);
        var steps = (int)Math.Ceiling(length * rate);
        object previous = null;
        var pendingTime = -1.0;
        for (var i = 0; i <= steps; i++) {
            var time = Math.Min(i / rate, length);
            var value = convert(evaluate(keys, start + time));
            if (previous != null && previous.Equals(value) && i < steps) {
                pendingTime = time;
                continue;
            }
            if (pendingTime >= 0) insertKey(animation, type, track, pendingTime, previous);
            insertKey(animation, type, track, time, value);
            previous = value;
            pendingTime = -1;
        }
    }
    private static void insertKey(Animation animation, Animation.TrackType type, int track, double time, object value) {
        switch (type) {
            case Animation.TrackType.Position3D:
                animation.PositionTrackInsertKey(track, time, (Vector3)value);
                break;
            case Animation.TrackType.Rotation3D:
                animation.RotationTrackInsertKey(track, time, (Quaternion)value);
                break;
            case Animation.TrackType.Scale3D:
                animation.ScaleTrackInsertKey(track, time, (Vector3)value);
                break;
        }
    }
    private static void addActiveTrack(Animation animation, string path, List<Key> keys, double start) {
        var track = animation.AddTrack(Animation.TrackType.Value);
        animation.TrackSetPath(track, path + ":visible");
        animation.ValueTrackSetUpdateMode(track, Animation.UpdateMode.Discrete);
        foreach (var key in keys) animation.TrackInsertKey(track, Math.Max(key.Time - start, 0), key.Value[0] > 0.5);
    }
    // Unity curves are cubic Hermite splines; an infinite tangent means a stepped key
    private static double[] evaluate(List<Key> keys, double time) {
        if (time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;
        var index = 0;
        while (index < keys.Count - 2 && keys[index + 1].Time < time) index++;
        var a = keys[index];
        var b = keys[index + 1];
        var span = b.Time - a.Time;
        if (span <= 0) return b.Value;
        var s = (time - a.Time) / span;
        var result = new double[a.Value.Length];
        for (var c = 0; c < result.Length; c++) {
            var outSlope = c < a.OutSlope.Length ? a.OutSlope[c] : 0;
            var inSlope = c < b.InSlope.Length ? b.InSlope[c] : 0;
            var end = c < b.Value.Length ? b.Value[c] : a.Value[c];
            if (double.IsInfinity(outSlope) || double.IsInfinity(inSlope)) {
                result[c] = a.Value[c];
                continue;
            }
            var s2 = s * s;
            var s3 = s2 * s;
            result[c] = (2 * s3 - 3 * s2 + 1) * a.Value[c] + (s3 - 2 * s2 + s) * outSlope * span + (-2 * s3 + 3 * s2) * end + (s3 - s2) * inSlope * span;
        }
        return result;
    }
    
    // Model clips
    private List<string> godotAnimationNames(string modelPath) {
        var instance = _modelInstance(modelPath);
        if (instance == null) return new List<string>();
        var players = new List<AnimationPlayer>();
        collect(instance, players);
        return players.SelectMany(p => p.GetAnimationList()).Select(n => n.ToString()).Where(n => n != "RESET").Distinct().ToList();
    }
    private string clipName(string modelPath, long fileId) {
        var meta = _index.GetModelMeta(modelPath);
        if (meta.Objects.TryGetValue(fileId, out var info) && info.ClassId == CLASS_ANIMATION_CLIP) return info.Name;
        var clips = meta.Clips;
        return clips.Count == 1 ? clips[0].Name : null;
    }
    private Animation extractModelClip(string modelPath, long fileId, string unityName) {
        var instance = _modelInstance(modelPath);
        if (instance == null) return null;
        var players = new List<AnimationPlayer>();
        collect(instance, players);
        var available = players.SelectMany(p => p.GetAnimationList().Select(n => (Player: p, Name: n))).ToList();
        if (available.Count == 0) {
            warnOnce($"noanim:{modelPath}", $"{Path.GetFileName(modelPath)} has no animations in Godot's import");
            return null;
        }
        var clip = _index.GetModelMeta(modelPath).Clips.FirstOrDefault(c => c.Name == unityName);
        var take = clip?.TakeName ?? unityName;
        var match = available.FirstOrDefault(a => normalized(a.Name) == normalized(take));
        if (match.Player == null) match = available.FirstOrDefault(a => normalized(a.Name) == normalized(unityName));
        if (match.Player == null) match = available.FirstOrDefault(a => normalized(a.Name).EndsWith(normalized(take ?? "~")));
        if (match.Player == null && available.Count == 1) match = available[0];
        if (match.Player == null) {
            warnOnce($"clip:{modelPath}:{fileId}", $"Clip '{unityName}' not found among {Path.GetFileName(modelPath)}'s animations ({string.Join(", ", available.Select(a => a.Name))})");
            return null;
        }
        Traced?.Invoke($"Extracting clip '{unityName}' from {Path.GetFileName(modelPath)}");
        var source = match.Player.GetAnimation(match.Name);
        var animation = clip == null ? (Animation)source.Duplicate(true) : slice(source, clip);
        animation.ResourceName = unityName ?? match.Name;
        return animation;
    }
    // Unity can split one take into several clips by frame range
    private static Animation slice(Animation source, UnityAssetIndex.ModelClip clip) {
        var fps = detectFps(source);
        var start = Math.Max(clip.FirstFrame / fps, 0);
        var end = Math.Min(clip.LastFrame / fps, source.Length);
        var loop = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        if (end <= start || (start < KEY_EPSILON && end > source.Length - KEY_EPSILON)) {
            var copy = (Animation)source.Duplicate(true);
            copy.LoopMode = loop;
            return copy;
        }
        var result = new Animation { Length = (float)(end - start), LoopMode = loop, Step = source.Step };
        for (var track = 0; track < source.GetTrackCount(); track++) {
            var type = source.TrackGetType(track);
            if (type != Animation.TrackType.Position3D && type != Animation.TrackType.Rotation3D && type != Animation.TrackType.Scale3D && type != Animation.TrackType.BlendShape) continue;
            var target = result.AddTrack(type);
            result.TrackSetPath(target, source.TrackGetPath(track));
            var times = new List<double> { start };
            for (var k = 0; k < source.TrackGetKeyCount(track); k++) {
                var time = source.TrackGetKeyTime(track, k);
                if (time > start + KEY_EPSILON && time < end - KEY_EPSILON) times.Add(time);
            }
            times.Add(end);
            foreach (var time in times) {
                switch (type) {
                    case Animation.TrackType.Position3D:
                        result.PositionTrackInsertKey(target, time - start, source.PositionTrackInterpolate(track, time));
                        break;
                    case Animation.TrackType.Rotation3D:
                        result.RotationTrackInsertKey(target, time - start, source.RotationTrackInterpolate(track, time));
                        break;
                    case Animation.TrackType.Scale3D:
                        result.ScaleTrackInsertKey(target, time - start, source.ScaleTrackInterpolate(track, time));
                        break;
                    default:
                        result.BlendShapeTrackInsertKey(target, time - start, source.BlendShapeTrackInterpolate(track, time));
                        break;
                }
            }
        }
        return result;
    }
    // Imported takes are baked one key per frame, so the key spacing reveals the frame rate
    private static double detectFps(Animation animation) {
        var smallest = double.MaxValue;
        for (var track = 0; track < animation.GetTrackCount(); track++) {
            for (var k = 1; k < animation.TrackGetKeyCount(track); k++) {
                var delta = animation.TrackGetKeyTime(track, k) - animation.TrackGetKeyTime(track, k - 1);
                if (delta > KEY_EPSILON) smallest = Math.Min(smallest, delta);
            }
        }
        return smallest == double.MaxValue ? 30 : Math.Clamp(Math.Round(1 / smallest), 1, 240);
    }
    
    // Conversion helpers
    private static Quaternion toGodotRotation(Quaternion rotation) {
        var converted = new Quaternion(rotation.X, -rotation.Y, -rotation.Z, rotation.W);
        return converted.LengthSquared() < 1e-8f ? Quaternion.Identity : converted.Normalized();
    }
    // Unity applies Euler angles in Z, X, Y order
    private static Quaternion unityEuler(double x, double y, double z) {
        var qx = new Quaternion(new Vector3(1, 0, 0), Mathf.DegToRad((float)x));
        var qy = new Quaternion(new Vector3(0, 1, 0), Mathf.DegToRad((float)y));
        var qz = new Quaternion(new Vector3(0, 0, 1), Mathf.DegToRad((float)z));
        return qy * qx * qz;
    }
    private static string toNodePath(string unityPath) {
        if (string.IsNullOrEmpty(unityPath)) return ".";
        return string.Join("/", unityPath.Split('/').Select(segment => UnityYaml.NodeName(segment)));
    }
    private static string uniqueName(AnimationLibrary library, string name) {
        name = sanitize(name);
        var candidate = name;
        for (var i = 2; library.HasAnimation(candidate); i++) candidate = name + i;
        return candidate;
    }
    private static string sanitize(string name) {
        var cleaned = new string(name.Select(c => "/:,[]\\*?\"<>|".Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Clip" : cleaned;
    }
    private static string normalized(string name) {
        return name == null ? "" : new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
    private static void collect<T>(Node node, List<T> results) where T : Node {
        if (node is T match) results.Add(match);
        foreach (var child in node.GetChildren()) collect(child, results);
    }
    private void warnOnce(string key, string message) {
        if (_reported.Add(key)) Logged?.Invoke("WARNING: " + message);
    }
    private void noteOnce(string key, string message) {
        if (_reported.Add(key)) Logged?.Invoke(message);
    }
}
#endif
