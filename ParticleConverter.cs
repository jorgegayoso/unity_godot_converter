#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnityPrefabConverter;

// Turns a Unity ParticleSystem and its renderer into a GPUParticles3D driven by a shared particle shader that runs Unity's modules.
// Unity emits a rate per second for a looping duration; Godot emits Amount particles per Lifetime, so the two are matched through rate * lifetime
public sealed class ParticleConverter {
// ----- VALUES -----
    public event Action<string> Logged;
    private readonly Func<UnityRef, ParticleMaterialOptions, Material> _material;
    private readonly Func<UnityRef, string, Mesh> _mesh;
    private readonly Func<List<UnityRef>, string, (Texture2D Texture, string Key)> _sprites;
    private readonly string _shaderPath;
    private Shader _shader;
    private const string SHADER_FILE = "_Particles/unity_particles.gdshader";
    private const float UNITY_GRAVITY = 9.81f;
    // Longer-lived looping systems run on a short Godot cycle so prewarming them takes a fraction of the time
    private const float RATE_SLOT_CYCLE = 2f;
    private const int MESH_EMISSION_POINTS = 1024;
    private const int CURVE_WIDTH = 64;
    private const int CURVE_ROWS = 15;
    private const int ROW_SIZE = 0;
    private const int ROW_FORCE = 2;
    private const int ROW_VELOCITY = 4;
    private const int ROW_ANGULAR = 6;
    private const int ROW_NOISE = 8;
    private const int ROW_SHEET = 9;
    private const int ROW_LIMIT = 10;
    private const int ROW_COLOR = 11;
    private const int ROW_START_GRADIENT = 13;
    private const int CURVE_SIZE = 1;
    private const int CURVE_FORCE = 2;
    private const int CURVE_VELOCITY = 4;
    private const int CURVE_ANGULAR = 8;
    private const int CURVE_NOISE = 16;
    private const int CURVE_SHEET = 32;
    private const int CURVE_LIMIT = 64;
    private const int CURVE_COLOR = 128;
    private static readonly Vector3 MIRROR = new(-1, 1, 1);
    private static readonly (string Module, string Label)[] UNSUPPORTED_MODULES = {
        ("TrailModule", "trails"), ("CollisionModule", "collision"), ("TriggerModule", "triggers"), ("SubModule", "sub emitters"), ("LightsModule", "lights"),
        ("CustomDataModule", "custom data"), ("ExternalForcesModule", "external forces"), ("InheritVelocityModule", "inherit velocity"), ("SizeBySpeedModule", "size by speed"),
        ("ColorBySpeedModule", "color by speed"), ("RotationBySpeedModule", "rotation by speed"), ("LifetimeByEmitterSpeedModule", "lifetime by emitter speed")
    };
    
    public sealed record ParticleMaterialOptions(BaseMaterial3D.BillboardModeEnum Billboard, int FramesX, int FramesY, bool Mesh, Texture2D Sprites, string SpritesKey);
    
    private enum RenderMode { Billboard = 0, Stretch = 1, Horizontal = 2, Vertical = 3, Mesh = 4, None = 5 }
    private enum RenderKind { Billboard = 0, Oriented = 1, Velocity = 2, Horizontal = 3 }
    private sealed class Sheet {
        public int FramesX = 1;
        public int FramesY = 1;
        public Texture2D Sprites;
        public string SpritesKey;
    }
    
// ----- PUBLIC CALLS -----
    // Setup
    public ParticleConverter(Func<UnityRef, ParticleMaterialOptions, Material> material, Func<UnityRef, string, Mesh> mesh, Func<List<UnityRef>, string, (Texture2D, string)> sprites, string outputDir) {
        _material = material;
        _mesh = mesh;
        _sprites = sprites;
        _shaderPath = outputDir + "/" + SHADER_FILE;
    }
    
    // Conversion
    public GpuParticles3D Convert(Dictionary<string, object> system, Dictionary<string, object> renderer, string ownerName) {
        var initial = UnityYaml.GetMap(system, "InitialModule");
        var process = new ShaderMaterial { Shader = shader() };
        var particles = new GpuParticles3D { Name = "Particles", ProcessMaterial = process };
        var curves = Image.CreateEmpty(CURVE_WIDTH, CURVE_ROWS, false, Image.Format.Rgbah);
        var notes = new List<string>();
        var lifetime = range(UnityYaml.GetMap(initial, "startLifetime"), 5);
        particles.Lifetime = Math.Max(lifetime.Max, 0.01f);
        process.SetShaderParameter("lifetime_range", new Vector2(lifetime.Min, lifetime.Max));
        particles.OneShot = !UnityYaml.GetBool(system, "looping", true);
        particles.Emitting = UnityYaml.GetBool(system, "playOnAwake", true);
        particles.SpeedScale = UnityYaml.GetDouble(system, "simulationSpeed", 1);
        particles.LocalCoords = UnityYaml.GetLong(system, "moveWithTransform") == 0;
        var prewarm = UnityYaml.GetBool(system, "prewarm");
        if (prewarm) particles.Preprocess = particles.Lifetime;
        applyEmission(system, initial, particles, process, lifetime.Average, prewarm);
        applyStart(initial, process, curves);
        var extent = applyShape(UnityYaml.GetMap(system, "ShapeModule"), process, ownerName);
        var flags = applyOverLifetime(system, process, curves, notes);
        var noiseReach = applyNoise(UnityYaml.GetMap(system, "NoiseModule"), process, curves, ref flags, lifetime.Max);
        var sheet = applyTextureSheet(UnityYaml.GetMap(system, "UVModule"), process, curves, ref flags, ownerName);
        var extraMeshes = applyRenderer(renderer, particles, process, sheet, notes);
        process.SetShaderParameter("curves", ImageTexture.CreateFromImage(curves));
        process.SetShaderParameter("curve_flags", flags);
        var speed = range(UnityYaml.GetMap(initial, "startSpeed"), 5).Max;
        var gravity = Math.Abs(range(UnityYaml.GetMap(initial, "gravityModifier"), 0).Max) * UNITY_GRAVITY;
        var life = lifetime.Max;
        var pushed = gravity + vectorReach(system, "ForceModule");
        var reach = Math.Min(200, Math.Max(4, extent + speed * life + pushed * life * life / 2 + vectorReach(system, "VelocityModule") * life + noiseReach + range(UnityYaml.GetMap(initial, "startSize"), 1).Max));
        particles.VisibilityAabb = new Aabb(new Vector3(-reach, -reach, -reach), new Vector3(reach, reach, reach) * 2);
        splitMeshes(particles, extraMeshes);
        process.SetShaderParameter("slot_count", (float)particles.Amount);
        foreach (var (module, label) in UNSUPPORTED_MODULES) {
            if (UnityYaml.GetBool(UnityYaml.GetMap(system, module), "enabled")) notes.Add(label);
        }
        if (notes.Count > 0) Logged?.Invoke($"particles on '{ownerName}': {string.Join(", ", notes)} not converted");
        return particles;
    }
    
// ----- HELPERS -----
    // Main settings
    private static void applyEmission(Dictionary<string, object> system, Dictionary<string, object> initial, GpuParticles3D particles, ShaderMaterial process, float averageLife, bool prewarm) {
        var emission = UnityYaml.GetMap(system, "EmissionModule");
        var maxParticles = Math.Max(1, (int)UnityYaml.GetLong(initial, "maxNumParticles", 1000));
        var rate = UnityYaml.GetBool(emission, "enabled", true) ? range(UnityYaml.GetMap(emission, "rateOverTime"), 10).Average : 0;
        var bursts = 0f;
        foreach (var burst in (UnityYaml.GetList(emission, "m_Bursts") ?? new List<object>()).OfType<Dictionary<string, object>>()) {
            var count = burst.ContainsKey("countCurve") ? range(UnityYaml.GetMap(burst, "countCurve"), 30).Average : (float)(UnityYaml.GetDouble(burst, "minCount", 30) + UnityYaml.GetDouble(burst, "maxCount", 30)) / 2;
            bursts += count * Math.Max(1, (int)UnityYaml.GetLong(burst, "cycleCount", 1));
        }
        // A slot waits on average half a cycle after its particle dies, so the slot count covers a life plus that wait
        if (!particles.OneShot && rate > 0 && bursts == 0 && particles.Lifetime > RATE_SLOT_CYCLE) {
            var period = averageLife + RATE_SLOT_CYCLE / 2;
            particles.Lifetime = RATE_SLOT_CYCLE;
            particles.Amount = Math.Clamp((int)Math.Ceiling(rate * period), 1, maxParticles);
            particles.Preprocess = prewarm ? RATE_SLOT_CYCLE : 0;
            process.SetShaderParameter("rate_slots", true);
            process.SetShaderParameter("prewarm", prewarm);
            process.SetShaderParameter("cycle_length", RATE_SLOT_CYCLE);
            process.SetShaderParameter("slot_period", period);
            return;
        }
        var amount = rate * particles.Lifetime + bursts;
        particles.Amount = Math.Clamp((int)Math.Ceiling(amount), 1, maxParticles);
        if (rate <= 0 && bursts > 0) particles.Explosiveness = 1;
    }
    private static void applyStart(Dictionary<string, object> initial, ShaderMaterial process, Image curves) {
        var speed = range(UnityYaml.GetMap(initial, "startSpeed"), 5);
        process.SetShaderParameter("speed_range", new Vector2(speed.Min, speed.Max));
        var size = range(UnityYaml.GetMap(initial, "startSize"), 1);
        process.SetShaderParameter("size_range", new Vector2(size.Min, size.Max));
        if (UnityYaml.GetBool(initial, "size3D") && Math.Abs(size.Average) > 0.0001f) {
            process.SetShaderParameter("size_axes", new Vector3(1, range(UnityYaml.GetMap(initial, "startSizeY"), 1).Average / size.Average, range(UnityYaml.GetMap(initial, "startSizeZ"), 1).Average / size.Average));
        }
        // Unity stores rotation in radians; mirroring X turns rotations about Y and Z the other way
        var z = range(UnityYaml.GetMap(initial, "startRotation"), 0);
        var x = UnityYaml.GetBool(initial, "rotation3D") ? range(UnityYaml.GetMap(initial, "startRotationX"), 0) : (0, 0, 0);
        var y = UnityYaml.GetBool(initial, "rotation3D") ? range(UnityYaml.GetMap(initial, "startRotationY"), 0) : (0, 0, 0);
        process.SetShaderParameter("rotation_min", new Vector3(x.Min, -y.Max, -z.Max));
        process.SetShaderParameter("rotation_max", new Vector3(x.Max, -y.Min, -z.Min));
        process.SetShaderParameter("flip_rotation", (float)UnityYaml.GetDouble(initial, "randomizeRotationDirection"));
        var gravity = range(UnityYaml.GetMap(initial, "gravityModifier"), 0).Average;
        process.SetShaderParameter("acceleration", new Vector3(0, -UNITY_GRAVITY * gravity, 0));
        // Unity's gradient modes: colour, gradient, two colours, two gradients, random colour
        var color = UnityYaml.GetMap(initial, "startColor");
        switch (UnityYaml.GetLong(color, "minMaxState")) {
            case 1:
            case 4:
                writeGradientRow(curves, ROW_START_GRADIENT, readGradient(UnityYaml.GetMap(color, "maxGradient")));
                process.SetShaderParameter("start_color_mode", 2);
                break;
            case 2:
                process.SetShaderParameter("start_color_mode", 1);
                process.SetShaderParameter("start_color_a", readColor(UnityYaml.GetMap(color, "minColor")));
                process.SetShaderParameter("start_color_b", readColor(UnityYaml.GetMap(color, "maxColor")));
                break;
            case 3:
                writeGradientRow(curves, ROW_START_GRADIENT, readGradient(UnityYaml.GetMap(color, "maxGradient")));
                writeGradientRow(curves, ROW_START_GRADIENT + 1, readGradient(UnityYaml.GetMap(color, "minGradient")));
                process.SetShaderParameter("start_color_mode", 3);
                break;
            default:
                process.SetShaderParameter("start_color_a", readColor(UnityYaml.GetMap(color, "maxColor")));
                break;
        }
    }
    
    // Shape (Unity emits along local +Z; the shader works in Unity's space and shape_basis mirrors X)
    private float applyShape(Dictionary<string, object> shape, ShaderMaterial process, string ownerName) {
        if (!UnityYaml.GetBool(shape, "enabled")) return 0;
        var rotationMap = UnityYaml.GetMap(shape, "m_Rotation");
        var rotation = new Basis(Vector3.Up, Mathf.DegToRad((float)UnityYaml.GetDouble(rotationMap, "y"))) * new Basis(Vector3.Right, Mathf.DegToRad((float)UnityYaml.GetDouble(rotationMap, "x"))) * new Basis(Vector3.Back, Mathf.DegToRad((float)UnityYaml.GetDouble(rotationMap, "z")));
        var scaleMap = UnityYaml.GetMap(shape, "m_Scale");
        var scale = new Vector3((float)UnityYaml.GetDouble(scaleMap, "x", 1), (float)UnityYaml.GetDouble(scaleMap, "y", 1), (float)UnityYaml.GetDouble(scaleMap, "z", 1));
        var mirror = Basis.FromScale(MIRROR);
        var basis = mirror * rotation * Basis.FromScale(scale);
        var position = UnityYaml.GetMap(shape, "m_Position");
        var offset = new Vector3(-(float)UnityYaml.GetDouble(position, "x"), (float)UnityYaml.GetDouble(position, "y"), (float)UnityYaml.GetDouble(position, "z"));
        var radius = (float)UnityYaml.GetDouble(UnityYaml.GetMap(shape, "radius"), "value", UnityYaml.GetDouble(shape, "radius", 1));
        var type = (int)UnityYaml.GetLong(shape, "type", 4);
        if (type == 19 || type == 20) type = 18;
        if (type == 6 || type == 13 || type == 14) {
            if (applyMeshShape(UnityYaml.GetRef(shape, "m_Mesh"), process, ownerName)) {
                type = 6;
                basis = mirror * rotation * Basis.FromScale(scale) * mirror;
            } else {
                Logged?.Invoke($"particles on '{ownerName}': mesh emission shape has no readable mesh; emitting from a point");
                type = -1;
            }
        } else if (type > 18) {
            Logged?.Invoke($"particles on '{ownerName}': emission shape type {type} isn't supported; emitting from a point");
            type = -1;
        }
        process.SetShaderParameter("shape_type", type);
        process.SetShaderParameter("shape_basis", basis);
        process.SetShaderParameter("shape_position", offset);
        process.SetShaderParameter("shape_radius", radius);
        process.SetShaderParameter("shape_thickness", (float)UnityYaml.GetDouble(shape, "radiusThickness", 1));
        process.SetShaderParameter("shape_angle", (float)UnityYaml.GetDouble(shape, "angle", 25));
        process.SetShaderParameter("shape_arc", (float)UnityYaml.GetDouble(UnityYaml.GetMap(shape, "arc"), "value", UnityYaml.GetDouble(shape, "arc", 360)));
        process.SetShaderParameter("shape_length", (float)UnityYaml.GetDouble(shape, "length", 5));
        process.SetShaderParameter("shape_donut_radius", (float)UnityYaml.GetDouble(shape, "donutRadius", 0.2));
        process.SetShaderParameter("random_direction", (float)Math.Clamp(UnityYaml.GetDouble(shape, "randomDirectionAmount"), 0, 1));
        process.SetShaderParameter("spherize_direction", (float)Math.Clamp(UnityYaml.GetDouble(shape, "sphericalDirectionAmount"), 0, 1));
        var largest = Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
        return offset.Length() + largest * (type == 5 || type == 18 ? 1 : radius + (type >= 7 && type <= 9 ? (float)UnityYaml.GetDouble(shape, "length", 5) : 0));
    }
    // Points spread over the mesh's triangles by area, with normals so particles leave along the surface
    private bool applyMeshShape(UnityRef meshRef, ShaderMaterial process, string ownerName) {
        var mesh = meshRef.IsNull ? null : _mesh(meshRef, ownerName);
        var faces = mesh?.GetFaces();
        if (faces == null || faces.Length < 3) return false;
        var areas = new float[faces.Length / 3];
        var total = 0f;
        for (var i = 0; i < areas.Length; i++) {
            total += (faces[i * 3 + 1] - faces[i * 3]).Cross(faces[i * 3 + 2] - faces[i * 3]).Length() / 2;
            areas[i] = total;
        }
        if (total <= 0) return false;
        var random = new Random(1);
        var points = Image.CreateEmpty(MESH_EMISSION_POINTS, 1, false, Image.Format.Rgbf);
        var normals = Image.CreateEmpty(MESH_EMISSION_POINTS, 1, false, Image.Format.Rgbf);
        for (var i = 0; i < MESH_EMISSION_POINTS; i++) {
            var pick = (float)random.NextDouble() * total;
            var face = Math.Min(Array.BinarySearch(areas, pick) is var found && found < 0 ? ~found : found, areas.Length - 1);
            var a = faces[face * 3];
            var b = faces[face * 3 + 1];
            var c = faces[face * 3 + 2];
            var u = (float)random.NextDouble();
            var v = (float)random.NextDouble();
            if (u + v > 1) {
                u = 1 - u;
                v = 1 - v;
            }
            var point = a + (b - a) * u + (c - a) * v;
            var normal = (c - a).Cross(b - a).Normalized();
            points.SetPixel(i, 0, new Color(point.X, point.Y, point.Z));
            normals.SetPixel(i, 0, new Color(normal.X, normal.Y, normal.Z));
        }
        process.SetShaderParameter("emission_points", ImageTexture.CreateFromImage(points));
        process.SetShaderParameter("emission_normals", ImageTexture.CreateFromImage(normals));
        process.SetShaderParameter("emission_point_count", MESH_EMISSION_POINTS);
        return true;
    }
    
    // Over lifetime
    private static int applyOverLifetime(Dictionary<string, object> system, ShaderMaterial process, Image curves, List<string> notes) {
        var flags = 0;
        var size = UnityYaml.GetMap(system, "SizeModule");
        if (UnityYaml.GetBool(size, "enabled")) {
            var x = UnityYaml.GetMap(size, "curve");
            var separate = UnityYaml.GetBool(size, "separateAxes");
            writeRows(curves, ROW_SIZE, new[] { x, separate ? UnityYaml.GetMap(size, "y") : x, separate ? UnityYaml.GetMap(size, "z") : x }, Vector3.One, 1);
            flags |= CURVE_SIZE;
        }
        var color = UnityYaml.GetMap(system, "ColorModule");
        if (UnityYaml.GetBool(color, "enabled")) {
            var gradient = UnityYaml.GetMap(color, "gradient");
            switch (UnityYaml.GetLong(gradient, "minMaxState")) {
                case 0:
                    fillRow(curves, ROW_COLOR, readColor(UnityYaml.GetMap(gradient, "maxColor")));
                    fillRow(curves, ROW_COLOR + 1, readColor(UnityYaml.GetMap(gradient, "maxColor")));
                    break;
                case 2:
                    fillRow(curves, ROW_COLOR, readColor(UnityYaml.GetMap(gradient, "minColor")));
                    fillRow(curves, ROW_COLOR + 1, readColor(UnityYaml.GetMap(gradient, "maxColor")));
                    break;
                case 3:
                    writeGradientRow(curves, ROW_COLOR, readGradient(UnityYaml.GetMap(gradient, "minGradient")));
                    writeGradientRow(curves, ROW_COLOR + 1, readGradient(UnityYaml.GetMap(gradient, "maxGradient")));
                    break;
                default:
                    writeGradientRow(curves, ROW_COLOR, readGradient(UnityYaml.GetMap(gradient, "maxGradient")));
                    writeGradientRow(curves, ROW_COLOR + 1, readGradient(UnityYaml.GetMap(gradient, "maxGradient")));
                    break;
            }
            flags |= CURVE_COLOR;
        }
        var rotation = UnityYaml.GetMap(system, "RotationModule");
        if (UnityYaml.GetBool(rotation, "enabled")) {
            var separate = UnityYaml.GetBool(rotation, "separateAxes");
            writeRows(curves, ROW_ANGULAR, new[] { separate ? UnityYaml.GetMap(rotation, "x") : null, separate ? UnityYaml.GetMap(rotation, "y") : null, UnityYaml.GetMap(rotation, "curve") }, new Vector3(1, -1, -1), 0);
            flags |= CURVE_ANGULAR;
        }
        var force = UnityYaml.GetMap(system, "ForceModule");
        if (UnityYaml.GetBool(force, "enabled")) {
            writeRows(curves, ROW_FORCE, new[] { UnityYaml.GetMap(force, "x"), UnityYaml.GetMap(force, "y"), UnityYaml.GetMap(force, "z") }, MIRROR, 0);
            flags |= CURVE_FORCE;
        }
        var velocity = UnityYaml.GetMap(system, "VelocityModule");
        if (UnityYaml.GetBool(velocity, "enabled")) {
            writeRows(curves, ROW_VELOCITY, new[] { UnityYaml.GetMap(velocity, "x"), UnityYaml.GetMap(velocity, "y"), UnityYaml.GetMap(velocity, "z") }, MIRROR, 0);
            flags |= CURVE_VELOCITY;
            if (new[] { "orbitalX", "orbitalY", "orbitalZ", "radial" }.Any(k => range(UnityYaml.GetMap(velocity, k), 0).Max != 0 || range(UnityYaml.GetMap(velocity, k), 0).Min != 0)) notes.Add("orbital and radial velocity");
        }
        // Unity's drag slows in proportion to speed each second; dampen is the share of excess speed removed when over the limit
        var clamp = UnityYaml.GetMap(system, "ClampVelocityModule");
        if (UnityYaml.GetBool(clamp, "enabled")) {
            var magnitude = UnityYaml.GetMap(clamp, UnityYaml.GetBool(clamp, "separateAxis") ? "x" : "magnitude");
            var drag = UnityYaml.GetMap(clamp, "drag");
            for (var i = 0; i < CURVE_WIDTH; i++) {
                var t = i / (float)(CURVE_WIDTH - 1);
                curves.SetPixel(i, ROW_LIMIT, new Color(evaluate(magnitude, t, true, 1), evaluate(magnitude, t, false, 1), evaluate(drag, t, true, 0), evaluate(drag, t, false, 0)));
            }
            process.SetShaderParameter("limit_dampen", (float)Math.Clamp(UnityYaml.GetDouble(clamp, "dampen", 0), 0, 1));
            flags |= CURVE_LIMIT;
        }
        return flags;
    }
    // Unity samples one noise field for every particle, so neighbours drift together; strength is a speed in units per second
    private static float applyNoise(Dictionary<string, object> noise, ShaderMaterial process, Image curves, ref int flags, float lifetime) {
        if (!UnityYaml.GetBool(noise, "enabled")) return 0;
        process.SetShaderParameter("noise_enabled", true);
        var strengthMap = UnityYaml.GetMap(noise, "strength");
        var strength = range(strengthMap, 1);
        var amount = range(UnityYaml.GetMap(noise, "positionAmount"), 1).Average;
        var state = UnityYaml.GetLong(strengthMap, "minMaxState");
        if (state == 1 || state == 2) {
            for (var i = 0; i < CURVE_WIDTH; i++) {
                var t = i / (float)(CURVE_WIDTH - 1);
                curves.SetPixel(i, ROW_NOISE, new Color(evaluate(strengthMap, t, true, 1) * amount, evaluate(strengthMap, t, false, 1) * amount, 0));
            }
            flags |= CURVE_NOISE;
        }
        process.SetShaderParameter("noise_strength", new Vector2(strength.Min, strength.Max) * amount);
        if (UnityYaml.GetBool(noise, "separateAxes") && Math.Abs(strength.Average) > 0.0001f) {
            process.SetShaderParameter("noise_axes", new Vector3(1, range(UnityYaml.GetMap(noise, "strengthY"), 1).Average / strength.Average, range(UnityYaml.GetMap(noise, "strengthZ"), 1).Average / strength.Average));
        }
        var frequency = (float)UnityYaml.GetDouble(noise, "frequency", 0.5);
        process.SetShaderParameter("noise_frequency", frequency);
        process.SetShaderParameter("noise_scroll", range(UnityYaml.GetMap(noise, "scrollSpeed"), 0).Average);
        process.SetShaderParameter("noise_octaves", Math.Clamp((int)UnityYaml.GetLong(noise, "octaves", 1), 1, 4));
        process.SetShaderParameter("noise_octave_multiplier", (float)UnityYaml.GetDouble(noise, "octaveMultiplier", 0.5));
        process.SetShaderParameter("noise_octave_scale", (float)UnityYaml.GetDouble(noise, "octaveScale", 2));
        var top = Math.Max(Math.Abs(strength.Min), Math.Abs(strength.Max)) * amount;
        return Math.Min(top * lifetime, 2 * top / Math.Max(frequency, 0.05f));
    }
    
    // Sprite sheets: a grid of tiles, or a list of sprites that gets packed into a strip
    private Sheet applyTextureSheet(Dictionary<string, object> uv, ShaderMaterial process, Image curves, ref int flags, string ownerName) {
        var sheet = new Sheet();
        if (!UnityYaml.GetBool(uv, "enabled")) return sheet;
        float frames;
        var total = 1f;
        if (UnityYaml.GetLong(uv, "mode") == 1) {
            var sprites = (UnityYaml.GetList(uv, "sprites") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(s => UnityYaml.GetRef(s, "sprite")).Where(r => !r.IsNull).ToList();
            if (sprites.Count == 0) return sheet;
            var (texture, key) = _sprites(sprites, ownerName);
            if (texture == null) {
                Logged?.Invoke($"particles on '{ownerName}': sprite frames couldn't be read; using the material's texture");
                return sheet;
            }
            sheet.Sprites = texture;
            sheet.SpritesKey = key;
            sheet.FramesX = sprites.Count;
            frames = total = sprites.Count;
            process.SetShaderParameter("sheet_frames", frames);
        } else {
            sheet.FramesX = Math.Max(1, (int)UnityYaml.GetLong(uv, "tilesX", 1));
            sheet.FramesY = Math.Max(1, (int)UnityYaml.GetLong(uv, "tilesY", 1));
            total = sheet.FramesX * sheet.FramesY;
            var singleRow = UnityYaml.GetLong(uv, "animationType") == 1;
            frames = singleRow ? sheet.FramesX : total;
            process.SetShaderParameter("sheet_frames", frames);
            process.SetShaderParameter("sheet_rows", singleRow ? (float)sheet.FramesY : 1f);
            process.SetShaderParameter("sheet_random_row", singleRow && UnityYaml.GetBool(uv, "randomRow", true) && UnityYaml.GetLong(uv, "rowMode", 1) == 1);
            process.SetShaderParameter("sheet_row", singleRow ? (float)UnityYaml.GetLong(uv, "rowIndex") : 0f);
        }
        if (total <= 1) return sheet;
        process.SetShaderParameter("sheet_total", total);
        process.SetShaderParameter("sheet_cycles", (float)UnityYaml.GetDouble(uv, "cycles", 1));
        var start = range(UnityYaml.GetMap(uv, "startFrame"), 0);
        process.SetShaderParameter("sheet_start", new Vector2(start.Min, start.Max));
        if (UnityYaml.GetLong(uv, "timeMode") == 2) {
            process.SetShaderParameter("sheet_mode", 2);
            process.SetShaderParameter("sheet_fps", (float)UnityYaml.GetDouble(uv, "fps", 30));
        } else {
            var frameOverTime = UnityYaml.GetMap(uv, "frameOverTime");
            for (var i = 0; i < CURVE_WIDTH; i++) {
                var t = i / (float)(CURVE_WIDTH - 1);
                curves.SetPixel(i, ROW_SHEET, new Color(evaluate(frameOverTime, t, true, 0), evaluate(frameOverTime, t, false, 0), 0));
            }
            process.SetShaderParameter("sheet_mode", 1);
            flags |= CURVE_SHEET;
        }
        return sheet;
    }
    
    // Renderer
    private List<Mesh> applyRenderer(Dictionary<string, object> renderer, GpuParticles3D particles, ShaderMaterial process, Sheet sheet, List<string> notes) {
        var mode = (RenderMode)(renderer == null ? 0 : UnityYaml.GetLong(renderer, "m_RenderMode"));
        if (renderer != null && !UnityYaml.GetBool(renderer, "m_Enabled", true)) mode = RenderMode.None;
        var alignment = renderer == null ? 0 : UnityYaml.GetLong(renderer, "m_RenderAlignment");
        var materials = UnityYaml.GetList(renderer, "m_Materials") ?? new List<object>();
        var materialRef = materials.Count > 0 ? UnityYaml.ToRef(materials[0]) : default;
        particles.CastShadow = renderer != null && UnityYaml.GetLong(renderer, "m_CastShadows") != 0 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        // View and facing alignment turn to the camera; world and local keep the particle's own rotation; velocity points along the motion
        var kind = alignment switch {
            1 or 2 => RenderKind.Oriented,
            4 => RenderKind.Velocity,
            _ => RenderKind.Billboard
        };
        var billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        Mesh drawMesh;
        var extraMeshes = new List<Mesh>();
        switch (mode) {
            case RenderMode.Mesh:
                drawMesh = _mesh(UnityYaml.GetRef(renderer, "m_Mesh"), particles.Name) ?? new BoxMesh { Size = Vector3.One };
                foreach (var key in new[] { "m_Mesh1", "m_Mesh2", "m_Mesh3" }) {
                    var meshRef = UnityYaml.GetRef(renderer, key);
                    if (!meshRef.IsNull && _mesh(meshRef, particles.Name) is { } extra) extraMeshes.Add(extra);
                }
                if (kind == RenderKind.Billboard) billboard = BaseMaterial3D.BillboardModeEnum.Particles;
                break;
            case RenderMode.Stretch:
                drawMesh = new QuadMesh { Size = new Vector2(1, Math.Max(1, (float)UnityYaml.GetDouble(renderer, "m_LengthScale", 2))) };
                particles.TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity;
                kind = RenderKind.Billboard;
                break;
            case RenderMode.Horizontal:
                drawMesh = new QuadMesh { Size = Vector2.One, Orientation = PlaneMesh.OrientationEnum.Y };
                kind = RenderKind.Horizontal;
                break;
            case RenderMode.Vertical:
                drawMesh = new QuadMesh { Size = Vector2.One };
                billboard = BaseMaterial3D.BillboardModeEnum.FixedY;
                kind = RenderKind.Billboard;
                break;
            default:
                drawMesh = new QuadMesh { Size = Vector2.One };
                if (kind == RenderKind.Billboard) billboard = BaseMaterial3D.BillboardModeEnum.Particles;
                break;
        }
        process.SetShaderParameter("render_kind", (int)kind);
        if (mode == RenderMode.None) {
            particles.Visible = false;
            notes.Add("renderer (disabled or set to None)");
        }
        var options = new ParticleMaterialOptions(billboard, sheet.FramesX, sheet.FramesY, mode == RenderMode.Mesh, sheet.Sprites, sheet.SpritesKey);
        var material = materialRef.IsNull ? null : _material(materialRef, options);
        if (material != null) particles.MaterialOverride = material;
        particles.DrawPass1 = drawMesh;
        return extraMeshes;
    }
    // Unity picks one of up to four meshes per particle; Godot draws every pass on every particle, so each mesh gets its own emitter with a share of the particles
    private static void splitMeshes(GpuParticles3D particles, List<Mesh> extraMeshes) {
        if (extraMeshes.Count == 0) return;
        particles.Amount = Math.Max(1, (int)Math.Ceiling(particles.Amount / (float)(extraMeshes.Count + 1)));
        for (var i = 0; i < extraMeshes.Count; i++) {
            var copy = (GpuParticles3D)particles.Duplicate();
            copy.Name = $"Particles{i + 2}";
            copy.DrawPass1 = extraMeshes[i];
            particles.AddChild(copy);
        }
    }
    
    // Shader
    private Shader shader() {
        if (_shader != null) return _shader;
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(_shaderPath.GetBaseDir()));
        var current = Godot.FileAccess.FileExists(_shaderPath) ? Godot.FileAccess.GetFileAsString(_shaderPath) : null;
        if (current != ParticleShader.CODE) {
            using var file = Godot.FileAccess.Open(_shaderPath, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(ParticleShader.CODE);
        }
        _shader = ResourceLoader.Load<Shader>(_shaderPath, null, ResourceLoader.CacheMode.Replace) ?? new Shader { Code = ParticleShader.CODE };
        return _shader;
    }
    
    // Curve rows (min in one row, max in the next)
    private static float vectorReach(Dictionary<string, object> system, string module) {
        var map = UnityYaml.GetMap(system, module);
        if (!UnityYaml.GetBool(map, "enabled")) return 0;
        return new[] { "x", "y", "z" }.Select(axis => range(UnityYaml.GetMap(map, axis), 0)).Select(r => Math.Max(Math.Abs(r.Min), Math.Abs(r.Max))).Sum();
    }
    private static void writeRows(Image image, int row, Dictionary<string, object>[] axes, Vector3 sign, float fallback) {
        for (var i = 0; i < CURVE_WIDTH; i++) {
            var t = i / (float)(CURVE_WIDTH - 1);
            var low = new Vector3(axisValue(axes[0], t, true, fallback), axisValue(axes[1], t, true, fallback), axisValue(axes[2], t, true, fallback)) * sign;
            var high = new Vector3(axisValue(axes[0], t, false, fallback), axisValue(axes[1], t, false, fallback), axisValue(axes[2], t, false, fallback)) * sign;
            image.SetPixel(i, row, new Color(low.X, low.Y, low.Z));
            image.SetPixel(i, row + 1, new Color(high.X, high.Y, high.Z));
        }
    }
    private static float axisValue(Dictionary<string, object> curve, float t, bool lower, float fallback) {
        return curve == null ? fallback : evaluate(curve, t, lower, fallback);
    }
    private static void writeGradientRow(Image image, int row, Gradient gradient) {
        for (var i = 0; i < CURVE_WIDTH; i++) image.SetPixel(i, row, gradient.Sample(i / (float)(CURVE_WIDTH - 1)));
    }
    private static void fillRow(Image image, int row, Color color) {
        for (var i = 0; i < CURVE_WIDTH; i++) image.SetPixel(i, row, color);
    }
    
    // Unity curves and gradients
    private static float evaluate(Dictionary<string, object> minMax, float t, bool lower, float fallback) {
        if (minMax == null) return fallback;
        var scalar = (float)UnityYaml.GetDouble(minMax, "scalar", fallback);
        switch (UnityYaml.GetLong(minMax, "minMaxState")) {
            case 1:
                return evaluateCurve(UnityYaml.GetMap(minMax, "maxCurve"), t) * scalar;
            case 2:
                return evaluateCurve(UnityYaml.GetMap(minMax, lower ? "minCurve" : "maxCurve"), t) * scalar;
            case 3:
                return lower ? (float)UnityYaml.GetDouble(minMax, "minScalar", scalar) : scalar;
            default:
                return scalar;
        }
    }
    // Hermite segments between keys, as Unity's AnimationCurve does (an infinite tangent holds the value until the next key)
    private static float evaluateCurve(Dictionary<string, object> curve, float t) {
        var keys = (UnityYaml.GetList(curve, "m_Curve") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
        if (keys.Count == 0) return 0;
        if (t <= UnityYaml.GetDouble(keys[0], "time")) return (float)UnityYaml.GetDouble(keys[0], "value");
        for (var i = 1; i < keys.Count; i++) {
            var t1 = (float)UnityYaml.GetDouble(keys[i], "time");
            if (t > t1) continue;
            var t0 = (float)UnityYaml.GetDouble(keys[i - 1], "time");
            var v0 = (float)UnityYaml.GetDouble(keys[i - 1], "value");
            var v1 = (float)UnityYaml.GetDouble(keys[i], "value");
            var span = t1 - t0;
            var outSlope = (float)UnityYaml.GetDouble(keys[i - 1], "outSlope");
            var inSlope = (float)UnityYaml.GetDouble(keys[i], "inSlope");
            if (span <= 0) return v1;
            if (!float.IsFinite(outSlope) || !float.IsFinite(inSlope)) return v0;
            var s = (t - t0) / span;
            var s2 = s * s;
            var s3 = s2 * s;
            return (2 * s3 - 3 * s2 + 1) * v0 + (s3 - 2 * s2 + s) * outSlope * span + (-2 * s3 + 3 * s2) * v1 + (s3 - s2) * inSlope * span;
        }
        return (float)UnityYaml.GetDouble(keys[^1], "value");
    }
    private static (float Min, float Max, float Average) range(Dictionary<string, object> curve, float fallback) {
        if (curve == null) return (fallback, fallback, fallback);
        var scalar = (float)UnityYaml.GetDouble(curve, "scalar", fallback);
        var minScalar = (float)UnityYaml.GetDouble(curve, "minScalar", scalar);
        switch (UnityYaml.GetLong(curve, "minMaxState")) {
            case 3:
                return (Math.Min(minScalar, scalar), Math.Max(minScalar, scalar), (minScalar + scalar) / 2);
            case 1:
            case 2:
                var values = keyValues(UnityYaml.GetMap(curve, "maxCurve")).Concat(UnityYaml.GetLong(curve, "minMaxState") == 2 ? keyValues(UnityYaml.GetMap(curve, "minCurve")) : Enumerable.Empty<float>()).Select(v => v * scalar).ToList();
                if (values.Count == 0) return (scalar, scalar, scalar);
                return (values.Min(), values.Max(), values.Average());
            default:
                return (scalar, scalar, scalar);
        }
    }
    private static IEnumerable<float> keyValues(Dictionary<string, object> curve) {
        return (UnityYaml.GetList(curve, "m_Curve") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(k => (float)UnityYaml.GetDouble(k, "value"));
    }
    // Unity keeps up to 8 colour keys and 8 alpha keys with times out of 65535; Godot needs them merged into one list
    private static Gradient readGradient(Dictionary<string, object> map) {
        var colorKeys = Math.Clamp((int)UnityYaml.GetLong(map, "m_NumColorKeys", 2), 1, 8);
        var alphaKeys = Math.Clamp((int)UnityYaml.GetLong(map, "m_NumAlphaKeys", 2), 1, 8);
        var colors = Enumerable.Range(0, colorKeys).Select(i => ((float)UnityYaml.GetDouble(map, $"ctime{i}") / 65535f, readColor(UnityYaml.GetMap(map, $"key{i}")))).OrderBy(k => k.Item1).ToList();
        var alphas = Enumerable.Range(0, alphaKeys).Select(i => ((float)UnityYaml.GetDouble(map, $"atime{i}") / 65535f, readColor(UnityYaml.GetMap(map, $"key{i}")).A)).OrderBy(k => k.Item1).ToList();
        var fixedMode = UnityYaml.GetLong(map, "m_Mode") == 1;
        var times = colors.Select(c => c.Item1).Concat(alphas.Select(a => a.Item1)).Distinct().OrderBy(t => t).ToList();
        var gradient = new Gradient();
        gradient.Offsets = times.ToArray();
        gradient.Colors = times.Select(t => {
            var color = sample(colors.Select(c => (c.Item1, new Vector4(c.Item2.R, c.Item2.G, c.Item2.B, 0))).ToList(), t, fixedMode);
            var alpha = sample(alphas.Select(a => (a.Item1, new Vector4(a.Item2, 0, 0, 0))).ToList(), t, fixedMode).X;
            return new Color(color.X, color.Y, color.Z, alpha);
        }).ToArray();
        if (fixedMode) gradient.InterpolationMode = Gradient.InterpolationModeEnum.Constant;
        return gradient;
    }
    private static Vector4 sample(List<(float Time, Vector4 Value)> keys, float time, bool fixedMode) {
        if (time <= keys[0].Time) return keys[0].Value;
        for (var i = 1; i < keys.Count; i++) {
            if (time > keys[i].Time) continue;
            if (fixedMode) return keys[i].Value;
            var span = keys[i].Time - keys[i - 1].Time;
            return span <= 0 ? keys[i].Value : keys[i - 1].Value.Lerp(keys[i].Value, (time - keys[i - 1].Time) / span);
        }
        return keys[^1].Value;
    }
    private static Color readColor(Dictionary<string, object> map) {
        return new Color((float)UnityYaml.GetDouble(map, "r", 1), (float)UnityYaml.GetDouble(map, "g", 1), (float)UnityYaml.GetDouble(map, "b", 1), (float)UnityYaml.GetDouble(map, "a", 1));
    }
}
#endif
