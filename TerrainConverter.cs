#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace UnityPrefabConverter;

// Builds a mesh, heightmap collision, splat material and tree instances from a Unity TerrainData asset
public sealed class TerrainConverter {
// ----- VALUES -----
    public bool Overwrite = true;
    public event Action<string> Logged;
    public event Action<string> Traced;
    private readonly UnityAssetIndex _index;
    private readonly OutputPaths _paths;
    private readonly MaterialConverter _materials;
    private readonly Dictionary<string, TerrainAssets> _terrains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Shader> _shaders = new();
    private const int CLASS_TEXTURE_2D = 28;
    private const int CLASS_TERRAIN_DATA = 156;
    private const int MAX_MESH_RESOLUTION = 513;
    private const int MAX_COLLISION_RESOLUTION = 1025;
    private const int MAX_LAYERS = 12;
    private const float HEIGHT_RANGE = 32766f;
    private const string SHADER_FILE = "_Terrain/unity_terrain.gdshader";
    
    public sealed class TerrainAssets {
        public Mesh Mesh;
        public Material Material;
        public HeightMapShape3D Shape;
        public Vector3 ShapeScale;
        public Vector3 ShapeOffset;
        public Vector3 Size;
        public List<UnityRef> TreePrototypes = new();
        public List<(Vector3 Position, float Rotation, float Width, float Height, int Prototype)> Trees = new();
        public int DetailPrototypes;
        public string Summary;
    }
    private sealed class Heightmap {
        public int Resolution;
        public float[] Heights;
        public Vector3 Scale;
        public float At(int x, int z) {
            return Heights[Math.Clamp(z, 0, Resolution - 1) * Resolution + Math.Clamp(x, 0, Resolution - 1)];
        }
    }
    private sealed class TerrainLayer {
        public Texture2D Diffuse;
        public Vector2 TileSize = new(15, 15);
        public Vector2 TileOffset;
    }
    
// ----- PUBLIC CALLS -----
    // Setup
    public TerrainConverter(UnityAssetIndex index, OutputPaths paths, MaterialConverter materials) {
        _index = index;
        _paths = paths;
        _materials = materials;
    }
    
    // Conversion
    public TerrainAssets Convert(UnityRef terrainDataRef) {
        var unityPath = _index.GetPath(terrainDataRef.Guid);
        if (unityPath == null) {
            Logged?.Invoke($"WARNING: Terrain data {terrainDataRef.Guid} not found in the Unity project");
            return null;
        }
        if (_terrains.TryGetValue(unityPath, out var cached)) return cached;
        TerrainAssets assets = null;
        try {
            assets = build(unityPath);
        } catch (Exception exception) {
            Logged?.Invoke($"WARNING: Could not convert terrain {Path.GetFileName(unityPath)}: {exception.Message}");
        }
        _terrains[unityPath] = assets;
        return assets;
    }
    
// ----- HELPERS -----
    // Terrain data
    private TerrainAssets build(string unityPath) {
        Traced?.Invoke($"Converting terrain {Path.GetFileName(unityPath)}");
        var documents = new Dictionary<long, UnityDocument>();
        foreach (var document in UnityYaml.ParseFile(unityPath)) documents[document.FileId] = document;
        if (documents.Count == 0) throw new InvalidDataException("the file is empty or not a Unity asset");
        var data = documents.Values.FirstOrDefault(d => d.ClassId == CLASS_TERRAIN_DATA)?.Body ?? throw new InvalidDataException($"no TerrainData in the file (found {string.Join(", ", documents.Values.Select(d => d.TypeName).Distinct())})");
        var heightmap = readHeightmap(UnityYaml.GetMap(data, "m_Heightmap"));
        var basePath = _paths.ResPathWithoutExtension(unityPath);
        var assets = new TerrainAssets {
            Size = new Vector3(heightmap.Scale.X * (heightmap.Resolution - 1), heightmap.Scale.Y, heightmap.Scale.Z * (heightmap.Resolution - 1))
        };
        assets.Mesh = save(buildMesh(heightmap), basePath + "_Mesh.res");
        buildCollision(heightmap, assets);
        assets.Shape = save(assets.Shape, basePath + "_Collision.res");
        var splat = UnityYaml.GetMap(data, "m_SplatDatabase");
        assets.Material = save(buildMaterial(splat, documents, basePath, Path.GetFileName(unityPath)), basePath + "_Material.tres");
        readTrees(UnityYaml.GetMap(data, "m_DetailDatabase"), assets);
        var layerCount = (assets.Material as ShaderMaterial)?.GetShaderParameter("layer_count").AsInt32() ?? 0;
        assets.Summary = $"{heightmap.Resolution}x{heightmap.Resolution} heightmap, {assets.Size.X:0.#}x{assets.Size.Z:0.#} m, {layerCount} layer(s), {assets.Trees.Count} tree(s)";
        return assets;
    }
    // Heights are 16-bit values (0..32766) stored as a hex string, or as a plain list in older files
    private static Heightmap readHeightmap(Dictionary<string, object> map) {
        var resolution = (int)UnityYaml.GetLong(map, "m_Resolution", UnityYaml.GetLong(map, "m_Width"));
        if (resolution < 2) throw new InvalidDataException($"no heightmap resolution (m_Heightmap has: {string.Join(", ", map?.Keys ?? Enumerable.Empty<string>())})");
        var scaleMap = UnityYaml.GetMap(map, "m_Scale");
        var heightmap = new Heightmap {
            Resolution = resolution,
            Heights = new float[resolution * resolution],
            Scale = new Vector3((float)UnityYaml.GetDouble(scaleMap, "x", 1), (float)UnityYaml.GetDouble(scaleMap, "y", 600), (float)UnityYaml.GetDouble(scaleMap, "z", 1))
        };
        map.TryGetValue("m_Heights", out var raw);
        if (raw is string or byte[]) {
            var bytes = raw as byte[] ?? hexToBytes((string)raw);
            for (var i = 0; i < heightmap.Heights.Length && i * 2 + 1 < bytes.Length; i++) {
                heightmap.Heights[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / HEIGHT_RANGE * heightmap.Scale.Y;
            }
        } else if (raw is List<object> list) {
            for (var i = 0; i < heightmap.Heights.Length && i < list.Count; i++) {
                heightmap.Heights[i] = (float)UnityYaml.ParseDouble(list[i] as string, 0) / HEIGHT_RANGE * heightmap.Scale.Y;
            }
        }
        return heightmap;
    }
    
    // Geometry (Unity terrain spans +X and +Z from its origin; mirrored on X like everything else)
    private static Mesh buildMesh(Heightmap heightmap) {
        var step = Math.Max(1, (int)Math.Ceiling((heightmap.Resolution - 1) / (double)(MAX_MESH_RESOLUTION - 1)));
        var samples = (heightmap.Resolution - 1) / step + 1;
        var vertices = new Vector3[samples * samples];
        var normals = new Vector3[vertices.Length];
        var uvs = new Vector2[vertices.Length];
        var worldUvs = new Vector2[vertices.Length];
        for (var z = 0; z < samples; z++) {
            for (var x = 0; x < samples; x++) {
                var hx = Math.Min(x * step, heightmap.Resolution - 1);
                var hz = Math.Min(z * step, heightmap.Resolution - 1);
                var i = z * samples + x;
                var unityX = hx * heightmap.Scale.X;
                var unityZ = hz * heightmap.Scale.Z;
                vertices[i] = new Vector3(-unityX, heightmap.At(hx, hz), unityZ);
                var slopeX = (heightmap.At(hx + 1, hz) - heightmap.At(hx - 1, hz)) / (2 * heightmap.Scale.X);
                var slopeZ = (heightmap.At(hx, hz + 1) - heightmap.At(hx, hz - 1)) / (2 * heightmap.Scale.Z);
                normals[i] = new Vector3(slopeX, 1, -slopeZ).Normalized();
                uvs[i] = new Vector2(hx / (float)(heightmap.Resolution - 1), hz / (float)(heightmap.Resolution - 1));
                worldUvs[i] = new Vector2(unityX, unityZ);
            }
        }
        var indices = new int[(samples - 1) * (samples - 1) * 6];
        var n = 0;
        // Godot treats triangles that are clockwise when seen from the front as front faces
        for (var z = 0; z < samples - 1; z++) {
            for (var x = 0; x < samples - 1; x++) {
                var a = z * samples + x;
                var b = a + 1;
                var c = a + samples;
                var d = c + 1;
                indices[n++] = a;
                indices[n++] = c;
                indices[n++] = b;
                indices[n++] = b;
                indices[n++] = c;
                indices[n++] = d;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = worldUvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
    private static void buildCollision(Heightmap heightmap, TerrainAssets assets) {
        var step = Math.Max(1, (int)Math.Ceiling((heightmap.Resolution - 1) / (double)(MAX_COLLISION_RESOLUTION - 1)));
        var samples = (heightmap.Resolution - 1) / step + 1;
        var spacingX = heightmap.Scale.X * step;
        var spacingZ = heightmap.Scale.Z * step;
        // Heightmap shapes only scale uniformly, so with square cells the heights are divided by the cell size instead
        var uniform = Math.Abs(spacingX - spacingZ) < 1e-4f;
        var divisor = uniform ? spacingX : 1f;
        var data = new float[samples * samples];
        for (var row = 0; row < samples; row++) {
            for (var column = 0; column < samples; column++) {
                data[row * samples + column] = heightmap.At((samples - 1 - column) * step, row * step) / divisor;
            }
        }
        assets.Shape = new HeightMapShape3D { MapWidth = samples, MapDepth = samples, MapData = data };
        assets.ShapeScale = uniform ? new Vector3(spacingX, spacingX, spacingX) : new Vector3(spacingX, 1, spacingZ);
        assets.ShapeOffset = new Vector3(-(samples - 1) * spacingX / 2, 0, (samples - 1) * spacingZ / 2);
    }
    
    // Splat material
    private Material buildMaterial(Dictionary<string, object> splat, Dictionary<long, UnityDocument> documents, string basePath, string terrainName) {
        var layers = readLayers(splat, terrainName);
        if (layers.Count > MAX_LAYERS) Logged?.Invoke($"WARNING: {terrainName} has {layers.Count} terrain layers; only the first {MAX_LAYERS} are blended");
        // Terrains with up to 8 layers share one shader; larger ones get a variant with more control maps, which costs 5 more texture lookups per pixel
        var controlCount = Math.Clamp((layers.Count + 3) / 4, 2, MAX_LAYERS / 4);
        var material = new ShaderMaterial { Shader = terrainShader(controlCount) };
        var controls = (UnityYaml.GetList(splat, "m_AlphaTextures") ?? new List<object>()).Select(UnityYaml.ToRef).ToList();
        for (var i = 0; i < controlCount; i++) {
            Texture2D control = null;
            if (i < controls.Count && documents.TryGetValue(controls[i].FileId, out var texture)) control = save(readEmbeddedTexture(texture.Body), $"{basePath}_Splat{i}.res");
            if (i == 0 && control == null) control = ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, new byte[] { 255, 0, 0, 0 }));
            if (control != null) material.SetShaderParameter("control" + i, control);
        }
        for (var i = 0; i < Math.Min(layers.Count, MAX_LAYERS); i++) {
            var layer = layers[i];
            if (layer.Diffuse != null) material.SetShaderParameter("layer" + i, layer.Diffuse);
            var tile = new Vector2(Math.Max(layer.TileSize.X, 0.001f), Math.Max(layer.TileSize.Y, 0.001f));
            material.SetShaderParameter("tiling" + i, new Vector4(1 / tile.X, 1 / tile.Y, layer.TileOffset.X / tile.X, layer.TileOffset.Y / tile.Y));
        }
        material.SetShaderParameter("layer_count", Math.Min(layers.Count, MAX_LAYERS));
        return material;
    }
    // Unity 2018.3+ references TerrainLayer assets; older versions store splat prototypes inline
    private List<TerrainLayer> readLayers(Dictionary<string, object> splat, string terrainName) {
        var layers = new List<TerrainLayer>();
        foreach (var reference in (UnityYaml.GetList(splat, "m_TerrainLayers") ?? new List<object>()).Select(UnityYaml.ToRef)) {
            var layerPath = _index.GetPath(reference.Guid);
            var body = layerPath == null ? null : UnityYaml.ParseFile(layerPath).FirstOrDefault()?.Body;
            layers.Add(body == null ? new TerrainLayer() : readLayer(body, "m_DiffuseTexture", terrainName));
        }
        foreach (var item in (UnityYaml.GetList(splat, "m_Splats") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            layers.Add(readLayer(item, "texture", terrainName));
        }
        return layers;
    }
    private TerrainLayer readLayer(Dictionary<string, object> body, string textureKey, string terrainName) {
        var size = UnityYaml.GetMap(body, body.ContainsKey("m_TileSize") ? "m_TileSize" : "tileSize");
        var offset = UnityYaml.GetMap(body, body.ContainsKey("m_TileOffset") ? "m_TileOffset" : "tileOffset");
        return new TerrainLayer {
            Diffuse = _materials.LoadTexture(UnityYaml.GetRef(body, textureKey), terrainName),
            TileSize = new Vector2((float)UnityYaml.GetDouble(size, "x", 15), (float)UnityYaml.GetDouble(size, "y", 15)),
            TileOffset = new Vector2((float)UnityYaml.GetDouble(offset, "x", 0), (float)UnityYaml.GetDouble(offset, "y", 0))
        };
    }
    // Control maps are Texture2D sub-assets of the TerrainData file with their pixels inline; row 0 is the terrain's z = 0 edge
    private static ImageTexture readEmbeddedTexture(Dictionary<string, object> texture) {
        var width = (int)UnityYaml.GetLong(texture, "m_Width");
        var height = (int)UnityYaml.GetLong(texture, "m_Height");
        var format = UnityYaml.GetLong(texture, "m_TextureFormat", 4);
        var bytes = texture.TryGetValue("image data", out var raw) && raw is byte[] binary ? binary : hexToBytes(UnityYaml.GetString(texture, "_typelessdata"));
        if (width <= 0 || height <= 0 || bytes.Length < width * height * 3) return null;
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++) {
            switch (format) {
                case 5:
                    rgba[i * 4] = bytes[i * 4 + 1];
                    rgba[i * 4 + 1] = bytes[i * 4 + 2];
                    rgba[i * 4 + 2] = bytes[i * 4 + 3];
                    rgba[i * 4 + 3] = bytes[i * 4];
                    break;
                case 3:
                    rgba[i * 4] = bytes[i * 3];
                    rgba[i * 4 + 1] = bytes[i * 3 + 1];
                    rgba[i * 4 + 2] = bytes[i * 3 + 2];
                    rgba[i * 4 + 3] = 255;
                    break;
                default:
                    if (i * 4 + 3 >= bytes.Length) break;
                    Array.Copy(bytes, i * 4, rgba, i * 4, 4);
                    break;
            }
        }
        return ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba));
    }
    private Shader terrainShader(int controlCount) {
        if (_shaders.TryGetValue(controlCount, out var cached)) return cached;
        var layerCount = controlCount * 4;
        var path = _paths.OutputDir + "/" + (controlCount == 2 ? SHADER_FILE : SHADER_FILE.Replace(".gdshader", $"_{layerCount}.gdshader"));
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        var code = "shader_type spatial;\n\n";
        for (var i = 0; i < controlCount; i++) code += $"uniform sampler2D control{i} : hint_default_black, filter_linear, repeat_disable;\n";
        code += "uniform int layer_count = 1;\n";
        for (var i = 0; i < layerCount; i++) {
            code += $"uniform sampler2D layer{i} : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;\n";
            code += $"uniform vec4 tiling{i} = vec4(0.0667, 0.0667, 0.0, 0.0);\n";
        }
        code += "\nvoid fragment() {\n";
        for (var i = 0; i < controlCount; i++) code += $"    vec4 c{i} = texture(control{i}, UV);\n";
        var weights = string.Join(", ", Enumerable.Range(0, controlCount).Select(i => $"c{i}.r, c{i}.g, c{i}.b, c{i}.a"));
        code += $"    float weights[{layerCount}] = float[]({weights});\n    vec3 color = vec3(0.0);\n    float total = 0.0;\n";
        for (var i = 0; i < layerCount; i++) {
            code += $"    if (layer_count > {i}) {{ color += texture(layer{i}, UV2 * tiling{i}.xy + tiling{i}.zw).rgb * weights[{i}]; total += weights[{i}]; }}\n";
        }
        code += "    ALBEDO = color / max(total, 0.0001);\n    ROUGHNESS = 0.9;\n}\n";
        using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write)) file.StoreString(code);
        var shader = ResourceLoader.Load<Shader>(path, null, ResourceLoader.CacheMode.Replace) ?? new Shader { Code = code };
        _shaders[controlCount] = shader;
        return shader;
    }
    
    // Trees
    private static void readTrees(Dictionary<string, object> details, TerrainAssets assets) {
        foreach (var item in (UnityYaml.GetList(details, "m_TreePrototypes") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            assets.TreePrototypes.Add(UnityYaml.GetRef(item, item.ContainsKey("m_Prefab") ? "m_Prefab" : "prefab"));
        }
        foreach (var item in (UnityYaml.GetList(details, "m_TreeInstances") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            var position = UnityYaml.GetMap(item, "position");
            var unity = new Vector3(
                (float)UnityYaml.GetDouble(position, "x") * assets.Size.X,
                (float)UnityYaml.GetDouble(position, "y") * assets.Size.Y,
                (float)UnityYaml.GetDouble(position, "z") * assets.Size.Z);
            assets.Trees.Add((new Vector3(-unity.X, unity.Y, unity.Z), -(float)UnityYaml.GetDouble(item, "rotation"),
                (float)UnityYaml.GetDouble(item, "widthScale", 1), (float)UnityYaml.GetDouble(item, "heightScale", 1), (int)UnityYaml.GetLong(item, "index")));
        }
        assets.DetailPrototypes = (UnityYaml.GetList(details, "m_DetailPrototypes") ?? new List<object>()).Count;
    }
    
    // Utilities
    private T save<T>(T resource, string path) where T : Resource {
        if (resource == null) return null;
        if (!Overwrite && Godot.FileAccess.FileExists(path)) return ResourceLoader.Load<T>(path) ?? resource;
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        if (ResourceSaver.Save(resource, path) != Error.Ok) return resource;
        return ResourceLoader.Load<T>(path, null, ResourceLoader.CacheMode.Replace) ?? resource;
    }
    private static byte[] hexToBytes(string hex) {
        hex = hex?.Trim() ?? "";
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((hexValue(hex[i * 2]) << 4) | hexValue(hex[i * 2 + 1]));
        return bytes;
    }
    private static int hexValue(char c) {
        return c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
    }
}
#endif
