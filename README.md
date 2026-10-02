# Unity to Godot Converter

A Godot 4.7 (C#) editor plugin that converts Unity prefabs, scenes, animations, terrains and particle systems into Godot. It copies the models and textures they use into your Godot project, generates the materials and builds the scenes, in a folder tree that mirrors the Unity project's `Assets` folder. Nothing needs to be copied or converted beforehand, and the Unity editor doesn't need to be open or even installed.

Companion to [unity-godot-material-converter](https://github.com/jorgegayoso/unity-godot-material-converter).

## Setup

You need the **.NET (C#) build of Godot 4.7** and a Godot project that has a C# solution. If the project has none yet, create it with *Project → Tools → C# → Create C# solution*.

1. Copy the `unity_prefab_converter` folder into your Godot project's `addons` folder, so that you end up with `res://addons/unity_prefab_converter/plugin.cfg`. Create `addons` if it doesn't exist.
2. Build the C# project: the hammer button at the top right of the editor, or `dotnet build` in the project folder.
3. Enable **Unity Prefab Converter** in *Project → Project Settings → Plugins*. If it isn't listed or fails to enable, restart Godot and build again.
4. A **Prefab Converter** dock appears.

`addons/unity_prefab_converter/runtime` holds the one script your game uses at runtime (`UnityLodQuality`). Keep that folder in the project when you ship; the rest of the addon is editor-only.

## How to use

1. **Unity project**: the Unity project root, the folder that contains `Assets/`.
2. **Unity folder** (optional): only convert what is in this folder. Empty means all of `Assets/`. Start with one small folder to see what you get.
3. **Output**: a folder inside the Godot project that receives everything.
4. Tick what to convert, then click **Convert**.

It runs in three phases with a progress bar and can be cancelled:

1. **Copy** the models and textures the selected assets use (directly, or through nested prefabs, materials and controllers). PSD and TIFF textures become PNG. Files already copied and unchanged are skipped.
2. **Import**: Godot imports the copied files; the converter waits until it is done.
3. **Convert** materials, animations, prefabs and scenes.

You can convert again at any time. *Overwrite existing files* is on by default and rewrites everything; turn it off once you start editing converted scenes, so your own changes are kept.

### Options

| Option | What it does |
|---|---|
| Prefabs / Scenes / Animations | Which kinds of assets to convert. *Animations* converts every `.anim` and Animator Controller in the folder, even unused ones |
| Textures / Models | Also copy every texture or model in the folder, even ones no prefab uses. Off by default. Use these when the folder only holds textures or models |
| Overwrite existing files | On (default): everything is written again. Off: existing files are kept |
| LODs | **Quality levels** (default): every Unity LOD level stays in the scene but only the most detailed one shows, and Godot's own mesh LOD handles distance. **Switch by distance**: Unity's levels swap by camera distance. **Separate scenes**: one prefab scene per level |
| Hide past cull distance | On (default): with quality levels, objects still disappear at the distance where Unity's LOD Group culls them |
| Fade | Off (default). On: objects fade at LOD and cull distances instead of switching at once; Godot draws them as transparent while they fade, which costs performance |
| LOD distance × | Multiplies every LOD and cull distance (default 1), on top of the LOD bias from Unity's quality settings |
| Save large data as .res files | On (default): meshes, collision shapes, images and animations over 4 KB that are built into a scene are saved as binary `.res` files in a `Name_Data` folder next to the `.tscn`, so scenes stay small and load faster |
| Show log | The log is hidden by default; the status line shows the summary |

### Output

For `Assets/Farm/Prefabs/Barn.prefab` using `Assets/Farm/Models/Barn.fbx`:

```
Output/Farm/Prefabs/Barn.tscn
Output/Farm/Models/Barn.fbx
Output/Farm/Models/Barn_Meshes/...        single meshes pulled out of the model
Output/Farm/Models/Barn_Animations/...    clips pulled out of the model
Output/Farm/Models/Barn_Remapped.tscn     the model with Unity's material remaps applied (only if it has any)
Output/Farm/Materials/Barn.tres
Output/Farm/Animations/Barn.tres          Animator Controller as an AnimationLibrary
Output/Farm/Textures/Barn_Albedo.png
Output/Farm/Scenes/Farm.tscn
Output/_Shaders/                          your Godot versions of custom Unity shaders
Output/_Particles/, Output/_Terrain/      shaders the converter generates
Output/conversion_log.txt
Output/unconverted_shaders.txt
```

## The log

Everything the converter couldn't do is reported, so read the log after the first run.

- **`conversion_log.txt`** in the output folder is written as the conversion happens, with more detail than the dock shows. Lines starting with `WARNING:` are things that were skipped or guessed; the last line counts them.
- The dock's **Show log** button shows the same messages inside the editor.
- If Godot closes in the middle of a conversion, the log tells the next run which asset was being converted. That asset is skipped automatically; **Retry skipped asset(s)** brings it back.

## Custom shaders

**The automatic conversion of custom shaders is very bad.** Unity shader code (`.shader`, Shader Graph, Amplify) is written against Unity's render pipeline and is not translated at all. A material that uses a custom shader is only approximated with a plain `StandardMaterial3D` that picks up the main colour and texture. Toon shading, wind, water, vertex effects and anything else the shader did are lost.

**What I recommend: have an AI rewrite each shader as a `.gdshader`.** It is quick and the result is far better than the approximation:

1. Convert once. `unconverted_shaders.txt` in the output folder lists every custom shader that was approximated and the file name the converter will look for.
2. Give the Unity shader source to an AI assistant and ask for a Godot 4 spatial shader. Ask it to **keep the Unity property names as the uniform names**, so the converter can fill them in.
3. Save the result in the output folder's `_Shaders/` folder (any subfolder works) under the listed name.
4. Convert again. Materials that use that shader are updated, even with *Overwrite* off.

How the names are matched, for `Shader "Toon/CustomToonVegetation"` in `CustomToonVegetation.shader` (case doesn't matter):

- `Toon_CustomToonVegetation.gdshader`: the full shader name, with `/` and other symbols as `_`
- `CustomToonVegetation.gdshader`: the last part of the name, or the `.shader` file's name

Every material using that Unity shader then becomes a `ShaderMaterial` with your shader. Each uniform gets the material's value of the Unity property with the same name, ignoring case and underscores (`_TextureSample`, `texture_sample` and `TextureSample` all match). Textures, floats, ints, bools, colours and vectors are filled in. A `vec4` uniform named `<texture>_ST` gets that texture's tiling and offset, already flipped for Godot's UV origin (use it as `UV * name_ST.xy + name_ST.zw`).

Two shaders are generated by the converter itself and need no work from you: `_Particles/unity_particles.gdshader` (all particle systems) and `_Terrain/unity_terrain.gdshader` (terrain splat maps, plus `unity_terrain_12.gdshader` for terrains with more than 8 layers).

## What converts

| Unity | Godot |
|---|---|
| GameObject + Transform | `Node3D` (or `MeshInstance3D` / `RigidBody3D` when that is the main component). Inactive objects become hidden + disabled |
| MeshFilter + MeshRenderer | `MeshInstance3D` with surface materials, shadow casting |
| Built-in Cube/Sphere/Capsule/Cylinder/Plane/Quad | Matching primitive meshes |
| Material (Built-in, URP) | `StandardMaterial3D`: albedo color/texture with tiling, normal map, metallic, smoothness, emission, occlusion, alpha cutout/blend, double-sided, unlit |
| Material with a custom shader (`.shader`, Shader Graph, Amplify) | `ShaderMaterial` with your own shader from `_Shaders/` and the material's values set on matching uniforms (see *Custom shaders*); otherwise approximated with a `StandardMaterial3D` |
| LOD Group | By default each mesh records which Unity levels it belongs to (group `unity_lod`, metadata `unity_lod_levels` and `unity_lod_count`), level 0 is shown and the rest are hidden; see the **LODs** option. With **Switch by distance** every level gets a visibility range instead, worked out from the group's size and LOD heights for a 60° field of view and multiplied by the LOD bias of the Unity project's default desktop quality level (2 for Ultra). With **Separate scenes**, `Name.tscn` shows LOD0 and `Name_LOD1.tscn` and up show the others; scenes (`.unity`) use visibility ranges in that mode |
| Box/Sphere/Capsule/Mesh collider | `CollisionShape3D` under a `StaticBody3D`, or `Area3D` for triggers |
| Rigidbody | `RigidBody3D` (mass, damping, gravity, kinematic). Child colliders merge into it as a compound body |
| Light, Camera | `OmniLight3D`/`SpotLight3D`/`DirectionalLight3D`, `Camera3D` |
| AnimationClip (`.anim`) | `Animation`: position, rotation, Euler and scale curves (sampled with Unity's curve math), object on/off as visibility |
| AnimationClip inside a model | `Animation` taken from Godot's import of the model, cut to the clip's frame range and loop setting |
| Animator Controller / Override Controller | `AnimationLibrary` with every state's clip (blend trees contribute their clips) |
| Animator / legacy Animation component | `AnimationPlayer` named `UnityAnimator`, autoplaying the controller's default state. Besides the controller's clips it holds every clip that belongs to the object, even without a controller: clips inside its model, companion animation files for that model (`Model@Clip.fbx`, or files set to copy its avatar), and `.anim` files from the same top-level folder whose animated paths all exist under the object |
| Animator over skinned meshes from a model | Rebuilt from Godot's import of the model, so the skeleton, skinning and model animations work. Materials, hidden variants and extra objects carry over; objects parented to bones get a `BoneAttachment3D` |
| Nested prefab | Instanced converted scene with overrides (transform, name, active, renderer materials, removed objects) and added children/components |
| Model (FBX/glTF/OBJ/Blend/DAE) placed in a prefab | Instanced imported model, with overrides |
| Prefab variant | Inherited scene |
| Scene (`.unity`) | Scene with all root objects in Unity's order. A scene named like a prefab in the same folder gets a `_Scene` suffix |
| Lighting settings | `WorldEnvironment` with ambient light, fog and the skybox, using Unity's own lookup, rotation, tint and exposure: panoramic skyboxes (360°/180°, 3D layouts show the left eye), cubemap skyboxes whose texture is a 2:1 panorama, a horizontal/vertical cross or a strip, and 6-sided skyboxes. The procedural skybox becomes Godot's procedural sky. The log says which kind each scene got, or why its skybox couldn't be converted |
| Terrain + Terrain Collider | Godot has no terrain node, so the terrain becomes a heightmap `MeshInstance3D` with a splat-map material blending up to 12 terrain layers (terrains with more than 8 use a second, slightly heavier shader), a `HeightMapShape3D` collision, and painted trees as instances of their converted prefabs. Meshes, collision, material and splat maps are saved next to the TerrainData. The log lists each terrain's size and layers, or why it couldn't be converted |
| Particle System + renderer | `GPUParticles3D` driven by a shared particle shader (`_Particles/unity_particles.gdshader`) that runs Unity's modules per particle: per-particle lifetime, speed, size (incl. 3D size), rotation (incl. 3D rotation), start color (all five gradient modes), gravity, rate and bursts (Godot's *Amount* is rate × lifetime, capped at Max Particles), looping/prewarm/play on awake/simulation space, emission shapes (sphere, hemisphere, cone and cone volume, box, circle, edge, donut, rectangle, mesh) with position/rotation/scale and random/spherize direction, size/color/rotation over lifetime, velocity over lifetime, force over lifetime (curves evaluated over the particle's life, not averaged), curl noise (strength, frequency, scroll speed, octaves, separate axes), speed limit and drag, texture sheet animation (grid, single row, or a list of sprites packed into a strip from the texture's sprite rects; lifetime or FPS timing). Billboard, stretched, horizontal, vertical and mesh render modes, with view/world/local/velocity alignment; renderers with several meshes get one emitter per mesh. Looping systems that live longer than 2 s run on a short Godot cycle where a slot only respawns once its particle has died, so they keep Unity's emission rate and prewarm in a fraction of the time (prewarmed particles start part way through their life instead of simulating the whole lifetime when the scene loads). The particle material is saved next to the Unity material as `Name_Particles*.tres` with vertex color, billboarding and additive/alpha/multiply blending. Trails, collision, sub-emitters, lights, orbital velocity and other modules are listed in the log per system |
| MonoBehaviour | Not converted. Script names are stored in node metadata `unity_scripts` |

Anything else is listed in the log.

## Graphics quality from Unity's LODs

With the default **Quality levels** mode, Unity's LOD levels become a graphics setting instead of distance switching. Call this from your settings menu:

```csharp
UnityLodQuality.Apply(GetTree(), 0); // 0 = highest detail, 1, 2... = lower
```

Objects with fewer levels show their last one. Call it again after loading a new scene.

## Performance tips

Converted scenes look like the Unity scene but don't perform like it out of the box, because Unity does several things automatically that Godot leaves to you:

- **Draw calls**: Unity batches static objects; Godot draws each one separately. For props repeated many times (fences, crops, trees), use a `MultiMeshInstance3D`.
- **Shadows**: lower the directional light's shadow distance and turn shadows off on small props.
- **Culling**: bake occlusion culling for scenes with buildings, and keep *Hide past cull distance* on.
- **Lighting**: bake a `LightmapGI` for static scenes instead of relying on real-time lights.
- **Renderer**: on weak computers the Mobile or Compatibility renderer is much cheaper than Forward+. Terrains with more than 8 layers use many textures at once and may not work on Compatibility with old graphics cards.
- **Big scenes**: keep *Save large data as .res files* on; text scenes of several megabytes are slow to load and save.

## Editing converted textures

Some PSD atlases store a cut-out mask as their alpha channel while useful colours sit under the transparent part. Image editors throw those colours away on export by default. In GIMP, tick **Save color values from transparent pixels** in the PNG export dialog.

## Notes

- Animator state machines are not converted: transitions, parameters, layer blending and blend-tree weights have no direct Godot equivalent. Their clips are all available in the `AnimationPlayer`, ready to wire into an `AnimationTree`. Without a controller nothing autoplays, as in Unity.
- Humanoid (Mecanim muscle) clips can't be converted; generic clips and clips inside models work.
- Skinned meshes without an Animator above them are converted as static meshes.
- `.psd`/`.psb` and `.tif`/`.tiff` textures are converted to `.png` at the same place in the output tree (if a PNG/JPG/TGA with the same name sits next to one in Unity, that file is used instead). PSDs use the flattened image Photoshop saves with the file, with Photoshop's white matte removed from semi-transparent pixels. When the fourth channel is one the artist saved by hand (an alpha mask rather than the image's transparency), it becomes the PNG's alpha and the colours are left as they are. TIFFs support uncompressed, LZW, Deflate and PackBits data; JPEG-compressed TIFFs are listed in the log.
- Assets can be saved as text (Unity's default *Force Text* serialization) or in Unity's binary format, as Asset Store packs often ship terrain data. Binary files are read through the type information the Unity editor saves in them. Serialized formats 9 to 22 are supported; binary files re-saved by Unity 6000.5 or later (format 23) can't be read yet and are listed in the log.
- Unity fog never covers the skybox, so the converted fog doesn't either (`fog_sky_affect` is 0). Linear fog uses Godot's depth fog with the same start and end distances.
- Terrain grass/detail layers, holes and terrain normal maps aren't converted; very large heightmaps are reduced to 513×513 for the mesh and 1025×1025 for collision.
- Unity `.asset` meshes, lightmaps, UI layouts and audio are skipped.
- Unity numbers a mesh's material slots by the first polygon that uses each material, while Godot keeps the order the materials are attached in the file. For binary FBX models the converter reads that order from the file and matches it to Godot's surfaces by material name; other model formats use the slot index.
- Characters a Godot node name can't hold (`. : @ / " %`) become underscores, the same way Godot's model importer names nodes, so `Door.L` in Unity is `Door_L` in the converted scene.
- Unity is left-handed. Transforms and animations are mirrored on X, which is how Godot imports the same model files, so meshes line up with their Unity placement.
- Some FBX exports (for example Blender files with axis conversion) can end up with a different root rotation in Unity than in Godot. If a nested model appears rotated, check that model's import settings.
