#if TOOLS
namespace UnityPrefabConverter;

// Particle shader that runs Unity's particle modules; written once to the output folder and shared by every converted system
public static class ParticleShader {
// ----- VALUES -----
    public const string CODE = @"shader_type particles;

// Unity's particle modules. Shapes are evaluated in Unity's emitter space and brought over by shape_basis (which mirrors X)
uniform int shape_type = -1;
uniform vec3 shape_position = vec3(0.0);
uniform mat3 shape_basis = mat3(1.0);
uniform float shape_radius = 1.0;
uniform float shape_thickness = 1.0;
uniform float shape_angle = 25.0;
uniform float shape_arc = 360.0;
uniform float shape_length = 5.0;
uniform float shape_donut_radius = 0.2;
uniform float random_direction = 0.0;
uniform float spherize_direction = 0.0;
uniform sampler2D emission_points : filter_nearest;
uniform sampler2D emission_normals : filter_nearest;
uniform int emission_point_count = 0;

uniform vec2 lifetime_range = vec2(5.0);
uniform vec2 speed_range = vec2(5.0);
uniform vec2 size_range = vec2(1.0);
uniform vec3 size_axes = vec3(1.0);
uniform vec3 rotation_min = vec3(0.0);
uniform vec3 rotation_max = vec3(0.0);
uniform float flip_rotation = 0.0;
uniform int start_color_mode = 0;
uniform vec4 start_color_a = vec4(1.0);
uniform vec4 start_color_b = vec4(1.0);
uniform vec3 acceleration = vec3(0.0);

// Each row of curves holds a lifetime curve (min rows and max rows are blended by a per-particle random)
uniform sampler2D curves : filter_linear, repeat_disable;
uniform int curve_flags = 0;

uniform bool noise_enabled = false;
uniform vec2 noise_strength = vec2(1.0);
uniform vec3 noise_axes = vec3(1.0);
uniform float noise_frequency = 0.5;
uniform float noise_scroll = 0.0;
uniform int noise_octaves = 1;
uniform float noise_octave_multiplier = 0.5;
uniform float noise_octave_scale = 2.0;

uniform float limit_dampen = 0.0;

uniform int sheet_mode = 0;
uniform float sheet_frames = 1.0;
uniform float sheet_rows = 1.0;
uniform float sheet_total = 1.0;
uniform float sheet_cycles = 1.0;
uniform float sheet_fps = 30.0;
uniform vec2 sheet_start = vec2(0.0);
uniform bool sheet_random_row = false;
uniform float sheet_row = 0.0;

// 0 billboard (angle in CUSTOM.x), 1 oriented mesh, 2 aligned to velocity, 3 horizontal
uniform int render_kind = 0;

// Long-lived systems: Godot's cycle is shorter than a particle's life and a slot only respawns once its particle has died,
// so particles come out at Unity's rate. Prewarm starts each slot's first particle part way through its life instead of simulating the whole lifetime up front
uniform bool rate_slots = false;
uniform bool prewarm = false;
uniform float cycle_length = 1.0;
uniform float slot_period = 1.0;
uniform float slot_count = 1.0;
const int PREWARM_STEPS = 24;

const int ROWS = 15;
const int CURVE_SIZE = 1;
const int CURVE_FORCE = 2;
const int CURVE_VELOCITY = 4;
const int CURVE_ANGULAR = 8;
const int CURVE_NOISE = 16;
const int CURVE_SHEET = 32;
const int CURVE_LIMIT = 64;
const int CURVE_COLOR = 128;
const int CURVE_START_GRADIENT = 256;

float hash(uint x) {
	x = ((x >> 16u) ^ x) * 0x45d9f3bu;
	x = ((x >> 16u) ^ x) * 0x45d9f3bu;
	x = (x >> 16u) ^ x;
	return float(x) / 4294967295.0;
}

float rand(uint seed, uint salt) {
	return hash(seed * 1664525u + salt * 1013904223u);
}

vec4 curve(int row, float t) {
	return texture(curves, vec2(t, (float(row) + 0.5) / float(ROWS)));
}

vec3 random_unit(uint seed, uint salt) {
	float z = rand(seed, salt) * 2.0 - 1.0;
	float a = rand(seed, salt + 1u) * TAU;
	float r = sqrt(max(0.0, 1.0 - z * z));
	return vec3(r * cos(a), r * sin(a), z);
}

float lattice(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Value noise in -1..1 with its gradient
vec4 value_noise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	vec3 u = f * f * (3.0 - 2.0 * f);
	vec3 du = 6.0 * f * (1.0 - f);
	float a = lattice(i);
	float b = lattice(i + vec3(1, 0, 0));
	float c = lattice(i + vec3(0, 1, 0));
	float d = lattice(i + vec3(1, 1, 0));
	float e = lattice(i + vec3(0, 0, 1));
	float g = lattice(i + vec3(1, 0, 1));
	float h = lattice(i + vec3(0, 1, 1));
	float k = lattice(i + vec3(1, 1, 1));
	float k1 = b - a;
	float k2 = c - a;
	float k3 = e - a;
	float k4 = a - b - c + d;
	float k5 = a - c - e + h;
	float k6 = a - b - e + g;
	float k7 = -a + b + c - d + e - g - h + k;
	float value = a + k1 * u.x + k2 * u.y + k3 * u.z + k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x + k7 * u.x * u.y * u.z;
	vec3 gradient = du * vec3(k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z, k2 + k5 * u.z + k4 * u.x + k7 * u.z * u.x, k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y);
	return vec4(value, gradient) * 2.0 - vec4(1.0, 0.0, 0.0, 0.0);
}

// Unity's noise is curl noise: it swirls particles around without spreading them out. Scaled so each axis stays near -1..1
vec3 noise_field(vec3 p) {
	vec3 gx = vec3(0.0);
	vec3 gy = vec3(0.0);
	vec3 gz = vec3(0.0);
	float amplitude = 1.0;
	float scale = 1.0;
	float weight = 0.0;
	for (int i = 0; i < max(noise_octaves, 1); i++) {
		vec3 q = p * scale;
		gx += amplitude * scale * value_noise(q).yzw;
		gy += amplitude * scale * value_noise(q + vec3(31.4, 17.3, 5.9)).yzw;
		gz += amplitude * scale * value_noise(q + vec3(-11.7, 43.1, 27.2)).yzw;
		weight += amplitude;
		amplitude *= noise_octave_multiplier;
		scale *= noise_octave_scale;
	}
	return clamp(vec3(gz.y - gy.z, gx.z - gz.x, gy.x - gx.y) / weight * 0.5, -1.0, 1.0);
}

mat3 euler(vec3 r) {
	float cx = cos(r.x), sx = sin(r.x), cy = cos(r.y), sy = sin(r.y), cz = cos(r.z), sz = sin(r.z);
	mat3 x = mat3(vec3(1, 0, 0), vec3(0, cx, sx), vec3(0, -sx, cx));
	mat3 y = mat3(vec3(cy, 0, -sy), vec3(0, 1, 0), vec3(sy, 0, cy));
	mat3 z = mat3(vec3(cz, sz, 0), vec3(-sz, cz, 0), vec3(0, 0, 1));
	return y * x * z;
}

void emit_shape(uint seed, out vec3 position, out vec3 direction) {
	float u1 = rand(seed, 11u);
	float u2 = rand(seed, 12u);
	float u3 = rand(seed, 13u);
	float arc = radians(shape_arc) * u2;
	position = vec3(0.0);
	direction = vec3(0.0, 0.0, 1.0);
	if (shape_type == 0 || shape_type == 1 || shape_type == 2 || shape_type == 3) {
		direction = random_unit(seed, 14u);
		if (shape_type >= 2) direction.z = abs(direction.z);
		float r = shape_type == 1 || shape_type == 3 ? 1.0 : 1.0 - shape_thickness * (1.0 - pow(u1, 1.0 / 3.0));
		position = direction * shape_radius * r;
	} else if (shape_type == 4 || shape_type == 7 || shape_type == 8 || shape_type == 9) {
		float r = shape_type == 7 || shape_type == 9 ? 1.0 : 1.0 - shape_thickness * (1.0 - sqrt(u1));
		vec2 around = vec2(cos(arc), sin(arc));
		float tilt = radians(shape_angle) * r;
		position = vec3(around * shape_radius * r, 0.0);
		direction = vec3(around * sin(tilt), cos(tilt));
		if (shape_type >= 8) position += direction * shape_length * u3;
	} else if (shape_type == 5 || shape_type == 15 || shape_type == 16) {
		position = vec3(u1, u2, u3) - 0.5;
	} else if (shape_type == 10 || shape_type == 11) {
		float r = shape_type == 11 ? 1.0 : 1.0 - shape_thickness * (1.0 - sqrt(u1));
		direction = vec3(cos(arc), sin(arc), 0.0);
		position = direction * shape_radius * r;
	} else if (shape_type == 12) {
		position = vec3((u1 * 2.0 - 1.0) * shape_radius, 0.0, 0.0);
		direction = vec3(0.0, 1.0, 0.0);
	} else if (shape_type == 17) {
		vec3 ring = vec3(cos(arc), sin(arc), 0.0);
		vec3 tube = random_unit(seed, 15u) * shape_donut_radius * pow(u1, 1.0 / 3.0);
		position = ring * shape_radius + tube;
		direction = normalize(tube + ring * 0.001);
	} else if (shape_type == 18) {
		position = vec3(u1 - 0.5, u2 - 0.5, 0.0);
	} else if (shape_type == 6 && emission_point_count > 0) {
		int index = int(u1 * float(emission_point_count)) % emission_point_count;
		position = texelFetch(emission_points, ivec2(index, 0), 0).xyz;
		direction = texelFetch(emission_normals, ivec2(index, 0), 0).xyz;
	}
	if (spherize_direction > 0.0 && length(position) > 0.0001) direction = mix(direction, normalize(position), spherize_direction);
	if (random_direction > 0.0) direction = mix(direction, random_unit(seed, 16u), random_direction);
	position = shape_basis * position + shape_position;
	direction = shape_basis * direction;
	direction = length(direction) > 0.0001 ? normalize(direction) : vec3(0.0, 0.0, 1.0);
}

// One step of Unity's per-frame modules. Velocity holds what builds up (start speed, gravity, forces, drag); the returned extra moves the particle without building up (velocity over lifetime, noise)
vec3 simulate(vec3 position, inout vec3 velocity, inout vec3 rotation, float t, float time, float dt, vec4 r1, vec4 r3) {
	vec3 accel = acceleration;
	if ((curve_flags & CURVE_FORCE) != 0) accel += mix(curve(2, t).xyz, curve(3, t).xyz, r1.x);
	velocity += accel * dt;
	if ((curve_flags & CURVE_LIMIT) != 0) {
		vec4 limit = curve(10, t);
		float top = mix(limit.r, limit.g, r1.y);
		float speed = length(velocity);
		if (top >= 0.0 && speed > top) velocity *= mix(1.0, top / speed, limit_dampen);
		velocity *= max(0.0, 1.0 - mix(limit.b, limit.a, r1.y) * dt);
	}
	vec3 extra = vec3(0.0);
	if ((curve_flags & CURVE_VELOCITY) != 0) extra += mix(curve(4, t).xyz, curve(5, t).xyz, r3.xyz);
	if (noise_enabled) {
		float strength = (curve_flags & CURVE_NOISE) != 0 ? mix(curve(8, t).r, curve(8, t).g, r1.z) : mix(noise_strength.x, noise_strength.y, r1.z);
		extra += noise_field(position * noise_frequency + vec3(time * noise_scroll)) * noise_axes * strength;
	}
	if ((curve_flags & CURVE_ANGULAR) != 0) rotation += mix(curve(6, t).xyz, curve(7, t).xyz, r1.w) * dt;
	return extra;
}

void start() {
	uint seed = NUMBER * 747796405u + RANDOM_SEED;
	bool first = USERDATA5.x == 0.0;
	USERDATA5.x = 1.0;
	bool spawn = true;
	if (rate_slots) {
		// A slot whose particle is still alive keeps it; before its first turn comes round the slot stays empty
		if (!first && CUSTOM.w > 0.0 && CUSTOM.y < CUSTOM.w) {
			spawn = false;
		} else {
			float chance = USERDATA5.y;
			USERDATA5.y += 1.0;
			float slot = float(NUMBER) / slot_count;
			if (!prewarm && (chance + slot) * cycle_length < slot * slot_period - cycle_length * 0.5) {
				USERDATA5.x = 0.0;
				CUSTOM = vec4(0.0);
				ACTIVE = false;
				spawn = false;
			}
		}
	}
	if (spawn) {
		USERDATA1 = vec4(rand(seed, 1u), rand(seed, 2u), rand(seed, 3u), rand(seed, 4u));
		USERDATA3 = vec4(rand(seed, 5u), rand(seed, 6u), rand(seed, 7u), rand(seed, 8u));
		CUSTOM = vec4(0.0);
		CUSTOM.w = mix(lifetime_range.x, lifetime_range.y, rand(seed, 9u));
		vec3 rotation = mix(rotation_min, rotation_max, vec3(rand(seed, 20u), rand(seed, 21u), rand(seed, 22u)));
		if (rand(seed, 23u) < flip_rotation) rotation = -rotation;
		float pick = rand(seed, 24u);
		if (start_color_mode == 1) USERDATA4 = mix(start_color_a, start_color_b, pick);
		else if (start_color_mode == 2) USERDATA4 = curve(13, pick);
		else if (start_color_mode == 3) USERDATA4 = mix(curve(14, pick), curve(13, pick), rand(seed, 25u));
		else USERDATA4 = start_color_a;
		COLOR = USERDATA4;
		vec3 position;
		vec3 direction;
		emit_shape(seed, position, direction);
		vec3 velocity = (mat3(EMISSION_TRANSFORM) * direction) * mix(speed_range.x, speed_range.y, rand(seed, 10u));
		position = (EMISSION_TRANSFORM * vec4(position, 1.0)).xyz;
		if (rate_slots && prewarm && first) {
			float age = rand(seed, 26u) * CUSTOM.w;
			float dt = age / float(PREWARM_STEPS);
			for (int i = 0; i < PREWARM_STEPS; i++) {
				float elapsed = float(i) * dt;
				vec3 extra = simulate(position, velocity, rotation, elapsed / CUSTOM.w, TIME - age + elapsed, dt, USERDATA1, USERDATA3);
				position += (velocity + extra) * dt;
			}
			CUSTOM.y = age;
		}
		USERDATA2 = vec4(rotation, 0.0);
		CUSTOM.x = rotation.z;
		VELOCITY = velocity;
		TRANSFORM = mat4(1.0);
		TRANSFORM[3].xyz = position;
	}
}

void process() {
	CUSTOM.y += DELTA;
	float life = max(CUSTOM.w, 0.001);
	float t = clamp(CUSTOM.y / life, 0.0, 1.0);
	if (CUSTOM.y >= life) ACTIVE = false;
	vec4 r1 = USERDATA1;
	vec4 r3 = USERDATA3;
	vec3 velocity = VELOCITY;
	vec3 rotation = USERDATA2.xyz;
	vec3 extra = simulate(TRANSFORM[3].xyz, velocity, rotation, t, TIME, DELTA, r1, r3);
	VELOCITY = velocity;
	TRANSFORM[3].xyz += extra * DELTA;
	USERDATA2.xyz = rotation;
	CUSTOM.x = rotation.z;

	vec3 size = mix(size_range.x, size_range.y, r3.w) * size_axes;
	if ((curve_flags & CURVE_SIZE) != 0) size *= mix(curve(0, t).xyz, curve(1, t).xyz, r3.w);
	size = max(size, vec3(0.0001));

	mat3 basis = mat3(1.0);
	if (render_kind == 1) {
		basis = euler(rotation);
	} else if (render_kind == 2) {
		vec3 moving = velocity + extra;
		if (length(moving) > 0.0001) {
			vec3 z = normalize(moving);
			vec3 up = abs(z.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
			vec3 x = normalize(cross(up, z));
			basis = mat3(x, cross(z, x), z) * euler(vec3(0.0, 0.0, rotation.z));
		}
	} else if (render_kind == 3) {
		basis = euler(vec3(0.0, rotation.z, 0.0));
	}
	TRANSFORM[0].xyz = basis[0] * size.x;
	TRANSFORM[1].xyz = basis[1] * size.y;
	TRANSFORM[2].xyz = basis[2] * size.z;

	COLOR = USERDATA4;
	if ((curve_flags & CURVE_COLOR) != 0) COLOR *= mix(curve(11, t), curve(12, t), r1.w);

	if (sheet_mode > 0) {
		float frame = sheet_mode == 1
			? ((curve_flags & CURVE_SHEET) != 0 ? mix(curve(9, t).r, curve(9, t).g, r3.x) : t) * sheet_frames * sheet_cycles
			: CUSTOM.y * sheet_fps;
		frame = mod(floor(frame + mix(sheet_start.x, sheet_start.y, r3.y)), sheet_frames);
		float row = sheet_random_row ? floor(r3.z * sheet_rows) : sheet_row;
		CUSTOM.z = (frame + row * sheet_frames + 0.5) / sheet_total;
	}
}
";
}
#endif
