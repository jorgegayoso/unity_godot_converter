#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace UnityPrefabConverter;

// Approximates Unity Built-in Standard and URP Lit with a StandardMaterial3D; custom shaders get an editable Godot shader of their own
public sealed class MaterialConverter {
// ----- VALUES -----
    public event Action<string> Logged;
    private readonly UnityAssetIndex _index;
    private readonly OutputPaths _paths;
    public readonly SortedDictionary<string, string> UnconvertedShaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedShaders = new();
    private readonly Dictionary<string, Shader> _customShaders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _userShaders;
    private const string CUSTOM_SHADER_FOLDER = "_Shaders";
    private static readonly System.Text.RegularExpressions.Regex SHADER_NAME_REGEX = new(@"^\s*Shader\s+""([^""]+)""");
    private Shader _skyShader;
    private const string SKY_SHADER_FILE = "_Sky/unity_sky.gdshader";
    private static readonly string[] SKY_FACES = { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" };
    private static readonly HashSet<string> PIPELINE_SHADERS = new(StringComparer.OrdinalIgnoreCase) { "Lit.shader", "SimpleLit.shader", "ComplexLit.shader", "BakedLit.shader", "Unlit.shader", "LitTessellation.shader", "TerrainLit.shader", "ParticlesUnlit.shader", "ParticlesLit.shader", "ParticlesSimpleLit.shader" };
    // Unity's built-in legacy particle shaders (Particles/Additive is 200)
    private const long BUILTIN_PARTICLES_FIRST = 200;
    private const long BUILTIN_PARTICLES_LAST = 212;
    private const long BUILTIN_PARTICLES_ADDITIVE = 200;
    private const int CLASS_MATERIAL = 21;
    private const int SPRITE_MODE_MULTIPLE = 2;
    private readonly Dictionary<string, (Texture2D, string)> _spriteStrips = new();
    private readonly Dictionary<string, Image> _spriteImages = new();
    private static readonly string[] ALBEDO_TEXTURES = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_Diffuse", "_DiffuseMap", "_Texture", "_Tex" };
    private static readonly string[] ALBEDO_HINTS = { "main", "albedo", "base", "diffuse", "color" };
    private static readonly string[] ALBEDO_COLORS = { "_BaseColor", "_Color", "_MainColor", "_TintColor", "_Tint" };
    private static readonly string[] NORMAL_TEXTURES = { "_BumpMap", "_NormalMap", "_Normal" };
    private static readonly string[] SMOOTHNESS_FLOATS = { "_Smoothness", "_Glossiness" };
    private static readonly string[] ALPHA_CLIP_KEYWORDS = { "_ALPHATEST_ON" };
    private static readonly string[] ALPHA_BLEND_KEYWORDS = { "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON", "_SURFACE_TYPE_TRANSPARENT" };
    
    private sealed class MaterialData {
        public string Name;
        public string Keywords;
        public bool Unlit;
        public string CustomShader;
        public long BuiltinShader = -1;
        public readonly Dictionary<string, Dictionary<string, object>> Textures = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> Floats = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase);
    }
    
// ----- PUBLIC CALLS -----
    // Setup
    public MaterialConverter(UnityAssetIndex index, OutputPaths paths) {
        _index = index;
        _paths = paths;
    }
    
    // Conversion
    public Material Convert(string matPath) {
        var data = readMaterial(matPath);
        if (data == null) return null;
        if (data.CustomShader != null && customShader(data) is { } shader) return customMaterial(shader, data);
        var material = new StandardMaterial3D { ResourceName = data.Name };
        applyAlbedo(material, data);
        applySurface(material, data);
        applyNormal(material, data);
        applyEmission(material, data);
        applyOcclusion(material, data);
        applyTransparency(material, data);
        if (floatOr(data, "_Cull", floatOr(data, "_CullMode", 2)) == 0) material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        if (data.Unlit) material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        return material;
    }
    
    // Particle materials take the particle colour from vertex colour and face the camera the way the renderer did
    public Material ConvertParticles(string matPath, ParticleConverter.ParticleMaterialOptions options) {
        var data = readMaterial(matPath);
        if (data == null) return null;
        if (data.CustomShader != null && customShader(data) is { } shader) return customMaterial(shader, data);
        var legacy = data.BuiltinShader >= BUILTIN_PARTICLES_FIRST && data.BuiltinShader <= BUILTIN_PARTICLES_LAST;
        var material = new StandardMaterial3D {
            ResourceName = data.Name,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            BillboardMode = options.Billboard,
            BillboardKeepScale = true,
            CullMode = options.Mesh ? BaseMaterial3D.CullModeEnum.Back : BaseMaterial3D.CullModeEnum.Disabled
        };
        applyAlbedo(material, data);
        if (options.Sprites != null) material.AlbedoTexture = options.Sprites;
        if (data.Unlit || legacy) material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        if (options.Billboard == BaseMaterial3D.BillboardModeEnum.Particles && options.FramesX * options.FramesY > 1) {
            material.ParticlesAnimHFrames = options.FramesX;
            material.ParticlesAnimVFrames = options.FramesY;
            material.ParticlesAnimLoop = true;
        }
        if (options.Mesh && options.Billboard != BaseMaterial3D.BillboardModeEnum.Disabled) material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var blend = floatOr(data, "_Blend", 0);
        if (ALPHA_CLIP_KEYWORDS.Any(data.Keywords.Contains) || floatOr(data, "_AlphaClip", 0) == 1) {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = (float)floatOr(data, "_Cutoff", 0.5);
        } else if (legacy || floatOr(data, "_Surface", 1) == 1 || ALPHA_BLEND_KEYWORDS.Any(data.Keywords.Contains)) {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.BlendMode = data.BuiltinShader == BUILTIN_PARTICLES_ADDITIVE || blend == 2 ? BaseMaterial3D.BlendModeEnum.Add : blend == 3 ? BaseMaterial3D.BlendModeEnum.Mul : blend == 1 ? BaseMaterial3D.BlendModeEnum.PremultAlpha : BaseMaterial3D.BlendModeEnum.Mix;
        }
        return material;
    }
    
    // Panoramic and 6-sided skyboxes use a sky shader that repeats Unity's own lookup; the procedural skybox maps onto Godot's
    public (Material Material, string Kind) ConvertSky(string matPath) {
        var data = readMaterial(matPath);
        if (data == null) return (null, "unreadable material");
        var exposure = (float)floatOr(data, "_Exposure", 1);
        if (SKY_FACES.All(f => data.Textures.ContainsKey(f)) && SKY_FACES.Any(f => hasTexture(data, f))) {
            var material = new ShaderMaterial { Shader = skyShader() };
            material.SetShaderParameter("mode", 1);
            foreach (var face in SKY_FACES) {
                if (hasTexture(data, face)) material.SetShaderParameter(face.Substring(1, face.Length - 4).ToLowerInvariant(), loadTexture(data, face));
            }
            setSkyCommon(material, data, exposure);
            return (material, "6-sided sky");
        }
        var panorama = hasTexture(data, "_MainTex") ? "_MainTex" : hasTexture(data, "_Tex") ? "_Tex" : null;
        if (panorama != null) {
            var texture = loadTexture(data, panorama);
            if (texture == null) return (null, $"its texture ({panorama}) isn't available in Godot");
            // The panoramic shader says its mapping; the cubemap shader's texture layout is read from its shape
            var latLong = floatOr(data, "_Mapping", panorama == "_MainTex" ? 1 : -1);
            var (layout, kind) = latLong == 1 ? (0, "panorama sky") : skyLayout(texture.GetWidth(), texture.GetHeight());
            if (layout < 0) return (null, $"its texture is {texture.GetWidth()}x{texture.GetHeight()}, which isn't a panorama (2:1), cross (4:3, 3:4) or strip (6:1, 1:6) layout");
            var material = new ShaderMaterial { Shader = skyShader() };
            material.SetShaderParameter("mode", layout == 0 ? 0 : 2);
            material.SetShaderParameter("layout", layout);
            material.SetShaderParameter("panorama", texture);
            material.SetShaderParameter("image_180", floatOr(data, "_ImageType", 0) == 1);
            material.SetShaderParameter("mirror_on_back", floatOr(data, "_MirrorOnBack", 0) == 1);
            material.SetShaderParameter("layout_3d", (int)floatOr(data, "_Layout", 0));
            setSkyCommon(material, data, exposure);
            if (layout == 0 && floatOr(data, "_ImageType", 0) == 1) kind += " (180 degrees)";
            return (material, kind);
        }
        var sky = new ProceduralSkyMaterial { EnergyMultiplier = exposure / 1.3f };
        if (data.Colors.TryGetValue("_GroundColor", out var ground)) {
            sky.GroundBottomColor = ground;
            sky.GroundHorizonColor = ground.Lerp(sky.SkyHorizonColor, 0.5f);
        }
        if (data.Colors.TryGetValue("_SkyTint", out var tint)) {
            sky.SkyTopColor = new Color(sky.SkyTopColor.R * tint.R * 2, sky.SkyTopColor.G * tint.G * 2, sky.SkyTopColor.B * tint.B * 2);
            sky.SkyHorizonColor = new Color(sky.SkyHorizonColor.R * tint.R * 2, sky.SkyHorizonColor.G * tint.G * 2, sky.SkyHorizonColor.B * tint.B * 2);
        }
        return (sky, "procedural sky");
    }
    
    // Textures
    public Texture2D LoadTexture(UnityRef reference, string usedBy) {
        var unityPath = _index.GetPath(reference.Guid);
        if (unityPath == null) return null;
        var candidates = new[] { _paths.ResPath(unityPath) }.Concat(AssetCollector.TEXTURE_EXTENSIONS.Select(e => _paths.ResPath(unityPath, e)));
        var godotPath = candidates.FirstOrDefault(p => Godot.FileAccess.FileExists(p) && Godot.FileAccess.FileExists(p + ".import"));
        if (godotPath == null) {
            Logged?.Invoke($"WARNING: Texture {Path.GetFileName(unityPath)} for {usedBy} is not imported");
            return null;
        }
        return ResourceLoader.Load<Texture2D>(godotPath);
    }
    // Particle sprite lists become one horizontal strip, a cell per sprite, so the particle material can step through them like a sheet
    public (Texture2D Texture, string Key) SpriteStrip(List<UnityRef> sprites, string usedBy) {
        var key = stableHash(string.Join(",", sprites.Select(s => $"{s.Guid}:{s.FileId}")));
        if (_spriteStrips.TryGetValue(key, out var cached)) return cached;
        var frames = new List<Image>();
        foreach (var sprite in sprites) {
            var unityPath = _index.GetPath(sprite.Guid);
            var image = unityPath == null ? null : spriteImage(sprite, unityPath, usedBy);
            if (image == null) {
                _spriteStrips[key] = (null, key);
                return (null, key);
            }
            var rect = spriteRect(unityPath, sprite.FileId, image.GetWidth(), image.GetHeight());
            frames.Add(rect.HasValue ? image.GetRegion(rect.Value) : image);
        }
        var cellWidth = frames.Max(f => f.GetWidth());
        var cellHeight = frames.Max(f => f.GetHeight());
        var strip = Image.CreateEmpty(cellWidth * frames.Count, cellHeight, false, Image.Format.Rgba8);
        for (var i = 0; i < frames.Count; i++) {
            strip.BlitRect(frames[i], new Rect2I(0, 0, frames[i].GetWidth(), frames[i].GetHeight()), new Vector2I(i * cellWidth + (cellWidth - frames[i].GetWidth()) / 2, (cellHeight - frames[i].GetHeight()) / 2));
        }
        strip.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(strip);
        var path = _paths.ResPathWithoutExtension(_index.GetPath(sprites[0].Guid)) + "_Sprites_" + key + ".res";
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        texture.TakeOverPath(path);
        if (ResourceSaver.Save(texture, path) != Error.Ok) Logged?.Invoke($"WARNING: Could not save {path}");
        _spriteStrips[key] = (texture, key);
        return (texture, key);
    }
    
// ----- HELPERS -----
    // Reading
    private MaterialData readMaterial(string matPath) {
        List<UnityDocument> documents;
        try {
            documents = UnityYaml.ParseFile(matPath);
        } catch (Exception exception) {
            Logged?.Invoke($"WARNING: Could not read {Path.GetFileName(matPath)}: {exception.Message}");
            return null;
        }
        var body = documents.FirstOrDefault(d => d.ClassId == CLASS_MATERIAL)?.Body;
        if (body == null) return null;
        var data = new MaterialData { Name = UnityYaml.GetString(body, "m_Name", Path.GetFileNameWithoutExtension(matPath)) };
        var keywords = new List<string> { UnityYaml.GetString(body, "m_ShaderKeywords") };
        keywords.AddRange((UnityYaml.GetList(body, "m_ValidKeywords") ?? new List<object>()).OfType<string>());
        data.Keywords = string.Join(" ", keywords);
        var properties = UnityYaml.GetMap(body, "m_SavedProperties");
        foreach (var (name, value) in readProperties(properties, "m_TexEnvs")) {
            if (value is Dictionary<string, object> texture) data.Textures[name] = texture;
        }
        foreach (var (name, value) in readProperties(properties, "m_Floats")) {
            if (value is string text) data.Floats[name] = UnityYaml.ParseDouble(text, 0);
        }
        foreach (var (name, value) in readProperties(properties, "m_Colors")) {
            if (value is Dictionary<string, object> color) data.Colors[name] = readColor(color);
        }
        data.Unlit = classifyShader(UnityYaml.GetRef(body, "m_Shader"), data);
        return data;
    }
    // Handles both "- _MainTex: {...}" and the older "- first: {name: _MainTex} second: {...}" layouts
    private static IEnumerable<(string Name, object Value)> readProperties(Dictionary<string, object> properties, string key) {
        foreach (var item in UnityYaml.GetList(properties, key) ?? new List<object>()) {
            if (item is not Dictionary<string, object> entry) continue;
            if (entry.TryGetValue("first", out var first) && first is Dictionary<string, object> firstMap) {
                yield return (UnityYaml.GetString(firstMap, "name"), entry.TryGetValue("second", out var second) ? second : null);
                continue;
            }
            foreach (var (name, value) in entry) yield return (name, value);
        }
    }
    // Built-in and render-pipeline shaders map onto StandardMaterial3D; anything else is marked custom. Returns whether the shader is unlit
    private bool classifyShader(UnityRef shaderRef, MaterialData data) {
        var materialName = data.Name;
        if (shaderRef.IsNull) return false;
        if (shaderRef.Guid == UnityAssetIndex.BUILTIN_DEFAULT_GUID) {
            data.BuiltinShader = shaderRef.FileId;
            return shaderRef.FileId >= 10750 && shaderRef.FileId <= 10755;
        }
        var shaderPath = _index.GetPath(shaderRef.Guid);
        if (shaderPath == null) {
            if (_reportedShaders.Add(shaderRef.Guid)) Logged?.Invoke($"WARNING: Shader {shaderRef.Guid} used by {materialName} is missing from the Unity project");
            return false;
        }
        var fileName = Path.GetFileName(shaderPath);
        if (shaderPath.Replace('\\', '/').Contains("com.unity.render-pipelines") && PIPELINE_SHADERS.Contains(fileName)) return fileName.Contains("Unlit");
        data.CustomShader = shaderPath;
        return fileName.Contains("Unlit", StringComparison.OrdinalIgnoreCase);
    }
    
    // Custom shaders: a .gdshader in _Shaders named after the Unity shader replaces the standard material
    private Shader customShader(MaterialData data) {
        var unityPath = data.CustomShader;
        if (_customShaders.TryGetValue(unityPath, out var cached)) return cached;
        var relative = Path.GetRelativePath(_paths.ProjectRoot, unityPath).Replace('\\', '/');
        var names = shaderFileNames(unityPath);
        var godotPath = names.Select(n => findUserShader(n)).FirstOrDefault(p => p != null);
        Shader shader = null;
        if (godotPath == null) {
            UnconvertedShaders[relative] = $"add {_paths.OutputDir}/{CUSTOM_SHADER_FOLDER}/{names[0]}.gdshader (or {string.Join(", ", names.Skip(1).Select(n => n + ".gdshader"))})";
            Logged?.Invoke($"Custom shader {Path.GetFileName(unityPath)} (first used by {data.Name}) approximated with a standard material; no {names[0]}.gdshader in {CUSTOM_SHADER_FOLDER}");
        } else {
            shader = ResourceLoader.Load<Shader>(godotPath, null, ResourceLoader.CacheMode.Replace);
            if (shader == null) {
                UnconvertedShaders[relative] = $"{godotPath} could not be loaded";
                Logged?.Invoke($"WARNING: {godotPath} could not be loaded; materials using {Path.GetFileName(unityPath)} get a standard material");
            } else {
                UnconvertedShaders.Remove(relative);
                Logged?.Invoke($"Custom shader {Path.GetFileName(unityPath)} uses {godotPath}");
            }
        }
        _customShaders[unityPath] = shader;
        return shader;
    }
    public string CustomShaderPath(string matPath) {
        var data = readMaterial(matPath);
        return data?.CustomShader == null ? null : customShader(data)?.ResourcePath;
    }
    // Candidates in order: the full shader name ("Toon/Vegetation" -> Toon_Vegetation), its last part, the .shader file's name
    private static List<string> shaderFileNames(string unityPath) {
        var names = new List<string>();
        var declared = unityPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ? declaredShaderName(unityPath) : null;
        if (declared != null) {
            names.Add(sanitizeShaderName(declared));
            names.Add(sanitizeShaderName(declared.Split('/').Last()));
        }
        names.Add(sanitizeShaderName(Path.GetFileNameWithoutExtension(unityPath)));
        return names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static string declaredShaderName(string unityPath) {
        try {
            using var reader = new StreamReader(unityPath);
            for (var i = 0; i < 50 && reader.ReadLine() is { } line; i++) {
                var match = SHADER_NAME_REGEX.Match(line);
                if (match.Success) return match.Groups[1].Value;
            }
        } catch (IOException) {
        }
        return null;
    }
    private static string sanitizeShaderName(string name) {
        var builder = new System.Text.StringBuilder();
        foreach (var c in name) builder.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
        return builder.ToString().Trim('_');
    }
    // Searched in any subfolder of _Shaders, ignoring case
    private string findUserShader(string name) {
        if (_userShaders == null) {
            _userShaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var folder = ProjectSettings.GlobalizePath($"{_paths.OutputDir}/{CUSTOM_SHADER_FOLDER}");
            if (Directory.Exists(folder)) {
                foreach (var file in Directory.EnumerateFiles(folder, "*.gdshader", SearchOption.AllDirectories)) {
                    var resPath = $"{_paths.OutputDir}/{CUSTOM_SHADER_FOLDER}/{Path.GetRelativePath(folder, file).Replace('\\', '/')}";
                    _userShaders.TryAdd(Path.GetFileNameWithoutExtension(file), resPath);
                }
            }
        }
        return _userShaders.TryGetValue(name, out var path) ? path : null;
    }
    // Uniforms take the Unity property of the same name (ignoring case and underscores); NAME_ST gets a texture's tiling and offset
    private ShaderMaterial customMaterial(Shader shader, MaterialData data) {
        var material = new ShaderMaterial { ResourceName = data.Name, Shader = shader };
        var textures = byKey(data.Textures.Keys);
        var floats = byKey(data.Floats.Keys);
        var colors = byKey(data.Colors.Keys);
        foreach (var uniform in shader.GetShaderUniformList()) {
            var info = uniform.AsGodotDictionary();
            var name = info["name"].AsString();
            var type = (Variant.Type)info["type"].AsInt32();
            if (name.EndsWith("_ST", StringComparison.OrdinalIgnoreCase) && type == Variant.Type.Vector4 && textures.TryGetValue(key(name[..^3]), out var tiled)) {
                material.SetShaderParameter(name, godotTiling(data.Textures[tiled]));
            } else if (type == Variant.Type.Object && textures.TryGetValue(key(name), out var textureName) && hasTexture(data, textureName)) {
                var texture = loadTexture(data, textureName);
                if (texture != null) material.SetShaderParameter(name, texture);
            } else if (type is Variant.Type.Float or Variant.Type.Int or Variant.Type.Bool && floats.TryGetValue(key(name), out var floatName)) {
                var number = data.Floats[floatName];
                material.SetShaderParameter(name, type == Variant.Type.Float ? Variant.From((float)number) : type == Variant.Type.Int ? Variant.From((int)number) : Variant.From(number != 0));
            } else if (type is Variant.Type.Color or Variant.Type.Vector4 or Variant.Type.Vector3 && colors.TryGetValue(key(name), out var colorName)) {
                var color = data.Colors[colorName];
                material.SetShaderParameter(name, type == Variant.Type.Color ? color : type == Variant.Type.Vector4 ? new Vector4(color.R, color.G, color.B, color.A) : new Vector3(color.R, color.G, color.B));
            }
        }
        return material;
    }
    private static Dictionary<string, string> byKey(IEnumerable<string> names) {
        var map = new Dictionary<string, string>();
        foreach (var name in names) map.TryAdd(key(name), name);
        return map;
    }
    private static string key(string name) {
        return name.Replace("_", "").ToLowerInvariant();
    }
    
    // Properties
    private void applyAlbedo(StandardMaterial3D material, MaterialData data) {
        var name = ALBEDO_TEXTURES.FirstOrDefault(n => hasTexture(data, n)) ?? data.Textures.Keys.FirstOrDefault(n => ALBEDO_HINTS.Any(h => n.ToLowerInvariant().Contains(h)) && hasTexture(data, n));
        if (name != null) {
            material.AlbedoTexture = loadTexture(data, name);
            applyTiling(material, data.Textures[name]);
        }
        var colorName = ALBEDO_COLORS.FirstOrDefault(data.Colors.ContainsKey);
        if (colorName != null) material.AlbedoColor = data.Colors[colorName];
    }
    private void applySurface(StandardMaterial3D material, MaterialData data) {
        material.Metallic = (float)floatOr(data, "_Metallic", 0);
        var smoothness = SMOOTHNESS_FLOATS.Where(data.Floats.ContainsKey).Select(n => data.Floats[n]).DefaultIfEmpty(0.5).First();
        material.Roughness = (float)(1 - smoothness);
        if (!hasTexture(data, "_MetallicGlossMap")) return;
        material.MetallicTexture = loadTexture(data, "_MetallicGlossMap");
        material.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Red;
        material.Metallic = 1;
    }
    private void applyNormal(StandardMaterial3D material, MaterialData data) {
        var name = NORMAL_TEXTURES.FirstOrDefault(n => hasTexture(data, n));
        if (name == null) return;
        material.NormalEnabled = true;
        material.NormalTexture = loadTexture(data, name);
        material.NormalScale = (float)floatOr(data, "_BumpScale", 1);
    }
    private void applyEmission(StandardMaterial3D material, MaterialData data) {
        var hasMap = hasTexture(data, "_EmissionMap");
        data.Colors.TryGetValue("_EmissionColor", out var color);
        var lit = color.R + color.G + color.B > 0.001f;
        if (!data.Keywords.Contains("_EMISSION") && !hasMap && !lit) return;
        if (!lit && !hasMap) return;
        material.EmissionEnabled = true;
        var energy = Math.Max(1f, Math.Max(color.R, Math.Max(color.G, color.B)));
        material.Emission = new Color(color.R / energy, color.G / energy, color.B / energy);
        material.EmissionEnergyMultiplier = energy;
        if (hasMap) material.EmissionTexture = loadTexture(data, "_EmissionMap");
    }
    private void applyOcclusion(StandardMaterial3D material, MaterialData data) {
        if (!hasTexture(data, "_OcclusionMap")) return;
        material.AOEnabled = true;
        material.AOTexture = loadTexture(data, "_OcclusionMap");
        material.AOTextureChannel = BaseMaterial3D.TextureChannel.Green;
    }
    private static void applyTransparency(StandardMaterial3D material, MaterialData data) {
        if (ALPHA_CLIP_KEYWORDS.Any(data.Keywords.Contains) || floatOr(data, "_AlphaClip", 0) == 1) {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = (float)floatOr(data, "_Cutoff", 0.5);
        } else if (ALPHA_BLEND_KEYWORDS.Any(data.Keywords.Contains) || floatOr(data, "_Surface", 0) == 1) {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }
    }
    // Unity's UV origin is bottom-left, Godot's is top-left, so the vertical offset is mirrored
    private static Vector4 godotTiling(Dictionary<string, object> texture) {
        var scale = UnityYaml.GetMap(texture, "m_Scale");
        var offset = UnityYaml.GetMap(texture, "m_Offset");
        var scaleY = (float)UnityYaml.GetDouble(scale, "y", 1);
        return new Vector4((float)UnityYaml.GetDouble(scale, "x", 1), scaleY, (float)UnityYaml.GetDouble(offset, "x", 0), 1 - scaleY - (float)UnityYaml.GetDouble(offset, "y", 0));
    }
    private static void applyTiling(StandardMaterial3D material, Dictionary<string, object> texture) {
        var scale = UnityYaml.GetMap(texture, "m_Scale");
        var offset = UnityYaml.GetMap(texture, "m_Offset");
        var scaleX = (float)UnityYaml.GetDouble(scale, "x", 1);
        var scaleY = (float)UnityYaml.GetDouble(scale, "y", 1);
        var offsetX = (float)UnityYaml.GetDouble(offset, "x", 0);
        var offsetY = (float)UnityYaml.GetDouble(offset, "y", 0);
        material.Uv1Scale = new Vector3(scaleX, scaleY, 1);
        material.Uv1Offset = new Vector3(offsetX, 1 - scaleY - offsetY, 0);
    }
    
    // Sky
    private static (int Layout, string Kind) skyLayout(int width, int height) {
        bool near(float ratio) => Math.Abs(width - height * ratio) <= height * ratio / 16f;
        if (near(2)) return (0, "panorama sky");
        if (near(4f / 3f)) return (1, "cubemap sky (horizontal cross)");
        if (near(3f / 4f)) return (2, "cubemap sky (vertical cross)");
        if (near(6)) return (3, "cubemap sky (horizontal strip)");
        if (near(1f / 6f)) return (4, "cubemap sky (vertical strip)");
        return (-1, null);
    }
    private static void setSkyCommon(ShaderMaterial material, MaterialData data, float exposure) {
        material.SetShaderParameter("exposure", exposure);
        material.SetShaderParameter("rotation", (float)floatOr(data, "_Rotation", 0));
        material.SetShaderParameter("tint", data.Colors.TryGetValue("_Tint", out var tint) ? tint : new Color(0.5f, 0.5f, 0.5f));
    }
    private Shader skyShader() {
        if (_skyShader != null) return _skyShader;
        var path = _paths.OutputDir + "/" + SKY_SHADER_FILE;
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        const string code = @"shader_type sky;

// Unity's skybox lookups, evaluated in Unity's left-handed space
uniform int mode = 0;
uniform int layout = 0;
uniform bool image_180 = false;
uniform bool mirror_on_back = false;
uniform int layout_3d = 0;
uniform sampler2D panorama : source_color, filter_linear, repeat_enable;
uniform sampler2D front : source_color, filter_linear, repeat_disable;
uniform sampler2D back : source_color, filter_linear, repeat_disable;
uniform sampler2D left : source_color, filter_linear, repeat_disable;
uniform sampler2D right : source_color, filter_linear, repeat_disable;
uniform sampler2D up : source_color, filter_linear, repeat_disable;
uniform sampler2D down : source_color, filter_linear, repeat_disable;
uniform vec4 tint : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform float exposure = 1.0;
uniform float rotation = 0.0;

vec3 rotate_y(vec3 v, float degrees) {
    float a = radians(degrees);
    vec2 xz = mat2(vec2(cos(a), sin(a)), vec2(-sin(a), cos(a))) * v.xz;
    return vec3(xz.x, v.y, xz.y);
}

// Cubemap faces in Unity's (Direct3D) convention, packed as a cross or strip in one image
vec3 cube_layout(vec3 d) {
    vec3 a = abs(d);
    int index;
    vec2 st;
    float m;
    if (a.x >= a.y && a.x >= a.z) {
        m = a.x;
        index = d.x > 0.0 ? 0 : 1;
        st = vec2(d.x > 0.0 ? -d.z : d.z, -d.y);
    } else if (a.y >= a.z) {
        m = a.y;
        index = d.y > 0.0 ? 2 : 3;
        st = vec2(d.x, d.y > 0.0 ? d.z : -d.z);
    } else {
        m = a.z;
        index = d.z > 0.0 ? 4 : 5;
        st = vec2(d.z > 0.0 ? d.x : -d.x, -d.y);
    }
    vec2 uv = st / m * 0.5 + 0.5;
    vec2 cell;
    vec2 grid;
    if (layout == 1) {
        grid = vec2(4.0, 3.0);
        vec2 cells[6] = vec2[](vec2(2, 1), vec2(0, 1), vec2(1, 0), vec2(1, 2), vec2(1, 1), vec2(3, 1));
        cell = cells[index];
    } else if (layout == 2) {
        grid = vec2(3.0, 4.0);
        vec2 cells[6] = vec2[](vec2(2, 1), vec2(0, 1), vec2(1, 0), vec2(1, 2), vec2(1, 1), vec2(1, 3));
        cell = cells[index];
        if (index == 5) uv = 1.0 - uv;
    } else if (layout == 3) {
        grid = vec2(6.0, 1.0);
        cell = vec2(float(index), 0.0);
    } else {
        grid = vec2(1.0, 6.0);
        cell = vec2(0.0, float(index));
    }
    // Half a texel in from the cell edge so filtering never reaches the neighbouring face
    vec2 margin = 0.5 * grid / vec2(textureSize(panorama, 0));
    uv = clamp(uv, margin, 1.0 - margin);
    return textureLod(panorama, (cell + uv) / grid, 0.0).rgb;
}

vec3 face(sampler2D tex, vec3 d, vec3 forward, vec3 right_axis, vec3 up_axis) {
    float k = dot(d, forward);
    return texture(tex, vec2(0.5 + 0.5 * dot(d, right_axis) / k, 0.5 - 0.5 * dot(d, up_axis) / k)).rgb;
}

void sky() {
    vec3 d = rotate_y(normalize(vec3(-EYEDIR.x, EYEDIR.y, EYEDIR.z)), -rotation);
    vec3 color;
    if (mode == 0) {
        float latitude = acos(clamp(d.y, -1.0, 1.0));
        float longitude = atan(d.z, d.x);
        vec2 uv = vec2(0.5 - longitude / (2.0 * PI), latitude / PI);
        bool hidden = false;
        if (image_180) {
            hidden = !mirror_on_back && uv.x > 0.5;
            uv.x = fract(uv.x * 2.0);
        }
        // 3D panoramas show the left eye: the left half, or the top half
        if (layout_3d == 1) uv.x *= 0.5;
        if (layout_3d == 2) uv.y *= 0.5;
        color = hidden ? vec3(0.0) : texture(panorama, uv).rgb;
    } else if (mode == 2) {
        color = cube_layout(d);
    } else {
        vec3 a = abs(d);
        if (a.x >= a.y && a.x >= a.z) {
            color = d.x > 0.0 ? face(left, d, vec3(1, 0, 0), vec3(0, 0, -1), vec3(0, 1, 0)) : face(right, d, vec3(-1, 0, 0), vec3(0, 0, 1), vec3(0, 1, 0));
        } else if (a.y >= a.z) {
            color = d.y > 0.0 ? face(up, d, vec3(0, 1, 0), vec3(1, 0, 0), vec3(0, 0, -1)) : face(down, d, vec3(0, -1, 0), vec3(1, 0, 0), vec3(0, 0, 1));
        } else {
            color = d.z > 0.0 ? face(front, d, vec3(0, 0, 1), vec3(1, 0, 0), vec3(0, 1, 0)) : face(back, d, vec3(0, 0, -1), vec3(-1, 0, 0), vec3(0, 1, 0));
        }
    }
    // Unity doubles the tint (4.59 once converted to linear light)
    COLOR = color * tint.rgb * 4.59 * exposure;
}
";
        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write)) file.StoreString(code);
        _skyShader = ResourceLoader.Load<Shader>(path, null, ResourceLoader.CacheMode.Replace) ?? new Shader { Code = code };
        return _skyShader;
    }
    
    // Textures
    private static bool hasTexture(MaterialData data, string name) {
        return data.Textures.TryGetValue(name, out var texture) && !UnityYaml.GetRef(texture, "m_Texture").IsNull;
    }
    private Texture2D loadTexture(MaterialData data, string name) {
        return LoadTexture(UnityYaml.GetRef(data.Textures[name], "m_Texture"), data.Name);
    }
    
    // Sprites
    private Image spriteImage(UnityRef sprite, string unityPath, string usedBy) {
        if (_spriteImages.TryGetValue(sprite.Guid, out var cached)) return cached;
        var image = LoadTexture(sprite, usedBy)?.GetImage();
        if (image != null) {
            if (image.IsCompressed()) image.Decompress();
            image.ClearMipmaps();
            image.Convert(Image.Format.Rgba8);
        }
        _spriteImages[sprite.Guid] = image;
        return image;
    }
    // Sprite rects live in the texture's .meta, measured from the bottom left; references name them by internal id
    private static Rect2I? spriteRect(string texturePath, long fileId, int width, int height) {
        var metaPath = texturePath + ".meta";
        if (!File.Exists(metaPath)) return null;
        var importer = UnityYaml.ParseMapping(File.ReadAllText(metaPath)).Values.OfType<Dictionary<string, object>>().FirstOrDefault(m => m.ContainsKey("spriteMode") || m.ContainsKey("spriteSheet"));
        if (importer == null || UnityYaml.GetLong(importer, "spriteMode", 1) != SPRITE_MODE_MULTIPLE) return null;
        var names = new Dictionary<long, string>();
        foreach (var (id, name) in UnityYaml.GetMap(importer, "fileIDToRecycleName") ?? new Dictionary<string, object>()) {
            if (long.TryParse(id, out var parsed)) names[parsed] = name?.ToString();
        }
        foreach (var entry in (UnityYaml.GetList(importer, "internalIDToNameTable") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            var first = UnityYaml.GetMap(entry, "first");
            var id = first?.Values.Select(v => long.TryParse(v?.ToString(), out var parsed) ? parsed : 0).FirstOrDefault() ?? 0;
            if (id != 0) names[id] = UnityYaml.GetString(entry, "second");
        }
        foreach (var (name, id) in UnityYaml.GetMap(importer, "nameFileIdTable") ?? new Dictionary<string, object>()) {
            if (long.TryParse(id?.ToString(), out var parsed)) names[parsed] = name;
        }
        var wanted = names.TryGetValue(fileId, out var found) ? found : null;
        foreach (var sprite in (UnityYaml.GetList(UnityYaml.GetMap(importer, "spriteSheet"), "sprites") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            if (UnityYaml.GetLong(sprite, "internalID") != fileId && UnityYaml.GetString(sprite, "name") != wanted) continue;
            var rect = UnityYaml.GetMap(sprite, "rect");
            var x = (int)Math.Round(UnityYaml.GetDouble(rect, "x"));
            var w = (int)Math.Round(UnityYaml.GetDouble(rect, "width"));
            var h = (int)Math.Round(UnityYaml.GetDouble(rect, "height"));
            var y = height - (int)Math.Round(UnityYaml.GetDouble(rect, "y")) - h;
            var clipped = new Rect2I(x, y, w, h).Intersection(new Rect2I(0, 0, width, height));
            return clipped.Area > 0 ? clipped : null;
        }
        return null;
    }
    private static string stableHash(string text) {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return hash.ToString("x8");
    }
    private static double floatOr(MaterialData data, string name, double fallback) {
        return data.Floats.TryGetValue(name, out var value) ? value : fallback;
    }
    private static Color readColor(Dictionary<string, object> map) {
        return new Color(
            (float)UnityYaml.GetDouble(map, "r", 1),
            (float)UnityYaml.GetDouble(map, "g", 1),
            (float)UnityYaml.GetDouble(map, "b", 1),
            (float)UnityYaml.GetDouble(map, "a", 1));
    }
}
#endif
