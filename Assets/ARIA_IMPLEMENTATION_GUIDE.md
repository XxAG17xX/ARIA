# ARIA Implementation Guide
## For Claude Code — Read this at the start of every session

This file supplements ARIA_CONTEXT.md. Where ARIA_CONTEXT.md describes *what* ARIA is,
this file describes *how to build it* — architecture decisions, Meta SDK specifics,
hard implementation problems with their solutions, and exactly which things are
Building Blocks vs. custom code. Written after deep research and design reasoning
sessions. Treat everything here as settled decisions unless the user explicitly
overrides them.

---

## Project Identity

**ARIA** = Adaptive Room Intelligence Assistant  
**Platform:** Meta Quest 3 (Mixed Reality)  
**Engine:** Unity (project already created with Mixed Reality template)  
**Repo:** exists on GitHub, ARIAOrchestrator.cs + 4 stubs already committed  
**Module:** CSU44054/CS7GV4 Extended Reality, Trinity College Dublin  
**Student:** Kartik, ID 24371670  
**Supervisors:** Gareth Young, Binh-Son Hua  
**Deadline:** Group project ARIA = 60% of module grade  

---

## What ARIA Does (one sentence)

User says "make this corner a reading nook" → ARIA understands the room via MRUK,
reasons about placement via Claude API, generates a reference image via Gemini,
generates a 3D model via HiTEM3D, loads the GLB via GLTFast, and places it
physically correctly in the real room with proper scale, lighting match, and
grabbable interaction.

---

## The Full Pipeline (in order)

```
[User speaks] 
    → Voice SDK (Wit.ai) transcribes 
    → ARIAOrchestrator.ProcessVoiceCommand(transcript)
    → Capture passthrough frame (WebCamTexture → JPEG bytes)
    → Serialize MRUK room data to JSON
    → Claude API call (multimodal: frame + room JSON + transcript)
    → Parse JSON placement array from Claude response
    → Gemini API call → reference image PNG
    → HiTEM3D API (auth → submit → poll) → GLB bytes
    → GLTFast → instantiate GameObject in scene
    → SemanticPlacementEngine → position from MRUK data
    → ScaleInferenceSystem → correct world-space size
    → SphericalHarmonicsLightingEstimator → match room lighting
    → ShadowReceiverSetup → virtual shadows on real surfaces
    → Add Rigidbody + auto-sized collider
    → Add HandGrabInteractable (Interaction SDK)
    → Object is live in scene, grabbable with hands
```

---

## Architecture Decisions (SETTLED — do not change without user approval)

### 1. Direct Anthropic API, NOT the LLM Building Block
The Building Blocks panel has an "LLM" block with Replicate → anthropic → claude-4-sonnet.
**Do not use it.** Reasons:
- Routes through Replicate's API (cost markup, extra latency)
- Cannot pass multimodal input (passthrough frame + room JSON)
- Cannot control exact system prompt or enforce JSON schema output
- Our pipeline requires a structured JSON array back from Claude, not free text

**Implementation:** Raw `UnityWebRequest` POST to `https://api.anthropic.com/v1/messages`
with `anthropic-version: 2023-06-01` header and API key from `config.json`.
This is already in ARIAOrchestrator.cs.

### 2. Voice Input: Use Voice SDK Building Block
The Voice Building Block IS appropriate for our use case.
- Handles microphone permissions on Quest
- Provides Wit.ai cloud ASR
- Exposes `OnFullTranscription` event we subscribe to

**What it creates in the hierarchy:** GameObject named `App Voice Experience`
**Key component:** `AppVoiceExperience` (namespace `Oculus.Voice`)
**How to wire:** Subscribe to `_voiceExperience.VoiceEvents.OnFullTranscription`
and pipe the string to `ARIAOrchestrator.ProcessVoiceCommand(transcript)`.

**Note:** Voice SDK is NOT in the Building Blocks panel — it's created via
`Assets > Create > Voice SDK > Add App Voice Experience to Scene`

### 3. Camera Rig: OVRCameraRig ONLY, never XR Origin
All Building Blocks depend on OVRCameraRig. Never use Unity's XR Origin.
The Mixed Reality template already has OVRCameraRig in the scene.

### 4. Passthrough Camera for API Frame Capture
`OVRPassthroughLayer` gives ZERO pixel access — it's OS-composited.
For sending a frame to Claude API, use `WebCamTexture`:

```csharp
WebCamTexture _webcam = new WebCamTexture();
_webcam.Play();
// Capture:
Texture2D snap = new Texture2D(_webcam.width, _webcam.height);
snap.SetPixels(_webcam.GetPixels());
snap.Apply();
byte[] jpeg = snap.EncodeToJPG(75);
string b64 = Convert.ToBase64String(jpeg);
```

**Limitation:** WebCamTexture only works on Quest 3/3S hardware (Quest 2 = no).
In editor Play mode it will fail silently — orchestrator must handle null gracefully
and send a text-only Claude request when frame is unavailable.

### 5. Interaction: Interaction SDK (NOT OVRGrabbable)
`OVRGrabbable` is deprecated. Use the **Interaction SDK** (`Oculus.Interaction`).

**Required components on a runtime-spawned grabbable object:**
- `Rigidbody` (useGravity: false initially, isKinematic: false)
- `Collider` (sized to mesh bounds — see ScaleInferenceSystem)
- `Grabbable` (namespace `Oculus.Interaction`)
- `HandGrabInteractable` (namespace `Oculus.Interaction.HandGrab`)

**The scene must already have the interaction rig** — added via
Building Blocks → Interaction → "Hand Grab Interaction" block,
which creates `OVRInteractionComprehensive` with HandGrabInteractors on each hand.
Claude Code cannot add Building Blocks — Kartik adds them manually in Unity.
Claude Code writes the scripts that reference the components they install.

### 6. MRUK for Scene Understanding
Use `Meta.XR.MRUtilityKit` (MRUK), NOT the deprecated `OVRSceneManager`.

**Initialization pattern:**
```csharp
MRUK.Instance.SceneLoadedEvent.AddListener(OnSceneReady);
void OnSceneReady() {
    MRUKRoom room = MRUK.Instance.GetCurrentRoom();
    // room data now available
}
```

**In editor Play mode:** MRUK returns null — always guard:
```csharp
var room = MRUK.Instance?.GetCurrentRoom();
if (room == null) { UseMockRoomData(); return; }
```

**Mock room data for editor testing:** Hardcode a JSON struct representing
a simple rectangular room (4 walls 3m high, 5x4m floor, one TABLE anchor).
This lets the pipeline run fully in editor.

### 7. Spatial Anchors: Implement After Core Pipeline Works
`OVRSpatialAnchor` lets placed objects persist between sessions (survive headset
restart). This is a nice-to-have for the demo. Implement after the placement
pipeline works end-to-end. Lifecycle: Create → SaveAsync → store UUID in
PlayerPrefs → LoadUnboundAnchorsAsync → LocalizeAsync → BindTo.

### 8. Config File Location
`config.json` exists in `Assets/` folder (Kartik confirmed this when setting up).
At runtime on Quest, use `Application.persistentDataPath` instead.
The orchestrator already handles both paths.

---

## The Four Hard Systems — Implementation Plans

These are research/math problems, not drag-and-drop. Claude Code should implement
them using the approaches described here.

### A. SemanticPlacementEngine

**Problem:** Where in the 3D room should the object be placed?

**Input:**
- Claude's JSON placement hint (e.g. `"placement": "floor near north wall"`,
  `"surface": "TABLE"`, `"position_hint": "corner"`)
- MRUK room data (anchor positions, normals, labels, sizes)

**Algorithm:**
1. Parse `surface` field from Claude's JSON → map to `MRUKAnchor.SceneLabels`
2. Find matching anchors: `room.Anchors.Where(a => a.HasAnyLabel(targetLabel))`
3. For floor placement: use `room.GenerateRandomPositionOnSurface(MRUK.SurfaceType.FACING_UP, clearanceRadius, labelFilter, out pos, out normal)`
4. For "near wall": raycast from room centre toward each wall, offset 0.3m from wall surface
5. For table/couch surface: use `anchor.transform.position` (which is top-centre of volume) + small upward offset
6. For "corner": find intersection of two nearest WallAnchors, offset inward by object half-width

**Surface normal convention:** For MRUKAnchor quads (walls, floor, ceiling),
`anchor.transform.forward` IS the surface normal. For volumes,
`anchor.transform.position` is the top centre.

**Wall normal = anchor.transform.forward (Z+). Use this for orienting objects
placed against walls so they face into the room.**

**Editor fallback:** If MRUK unavailable, place at `Camera.main.transform.position
+ Camera.main.transform.forward * 1.5f` (1.5m in front of user).

### B. ScaleInferenceSystem

**Problem:** What real-world size should the spawned model be?

**Input:**
- The object category (from Claude's JSON, e.g. `"object": "floor lamp"`)
- Room dimensions from MRUK (floor-to-ceiling height = known reference)
- The GLB's native bounding box after import

**Algorithm:**
1. Maintain a `Dictionary<string, float>` of canonical heights in metres:
   ```
   "lamp" → 1.5, "chair" → 0.9, "table" → 0.75, "bookshelf" → 1.8,
   "plant" → 0.6, "sofa" → 0.85, "desk" → 0.75, "bed" → 0.5 (mattress height),
   "monitor" → 0.45, "tv" → 0.6, "vase" → 0.3, "mug" → 0.1, "book" → 0.03
   ```
2. Get canonical height `h_canonical` for this object category.
3. Get GLB's native bounding box: `Bounds bounds = CalculateMeshBounds(spawnedGO)`
   (recurse all MeshRenderers, encapsulate). Get `h_native = bounds.size.y`.
4. Scale factor: `float scale = h_canonical / h_native`
5. Apply: `spawnedGO.transform.localScale = Vector3.one * scale`
6. Clamp to [0.05, 3.0] to prevent absurd scales.
7. Room-relative sanity check: if `h_canonical > roomHeight * 0.8`, scale down.

**After scaling, resize collider to match new bounds** — get bounds again post-scale
and set collider size accordingly.

**Helper method:**
```csharp
static Bounds CalculateMeshBounds(GameObject root) {
    var renderers = root.GetComponentsInChildren<Renderer>();
    if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.one * 0.1f);
    Bounds b = renderers[0].bounds;
    foreach (var r in renderers) b.Encapsulate(r.bounds);
    return b;
}
```

### C. SphericalHarmonicsLightingEstimator

**IMPORTANT — What happens when, and what the LLM is NOT involved in:**

```
AT SPAWN TIME (one API call, one camera capture):
  1. Capture passthrough frame → estimate ambient SH → set RenderSettings.ambientProbe
  2. Check MRUK anchors for LAMP/WINDOW → derive dominant light direction
  3. Set scene DirectionalLight direction accordingly
  4. Place a ReflectionProbe at the spawned object's position

EVERY FRAME (pure Unity GPU, zero API calls, zero LLM):
  - Shadow maps recalculated automatically (object casts real-time shadows)
  - ReflectionProbe updates (metallic/shiny surfaces reflect passthrough environment)
  - Object responds to directional light from all angles as user moves around it
  - This is identical to how lighting works in any Unity game
```

The LLM is NEVER involved in per-frame lighting. It only reasons about placement.
The passthrough camera is captured ONCE per spawn command, not continuously.

**Problem:** Make the spawned object look lit by the real room's ambient light,
not Unity's default directional light pointing arbitrarily.

**What spherical harmonics (SH) are:** A mathematical representation of
ambient light as frequency coefficients over a sphere. Unity's `RenderSettings.ambientProbe`
accepts an `SphericalHarmonicsL2` struct — if we estimate it from the room,
virtual objects get correct soft fill light from all directions, matching the room.

**Implementation — camera-based SH estimation (runs once at spawn):**
1. Reuse the passthrough JPEG already captured for the Claude API call (no extra capture)
2. Decode pixel data, sample in a 4×4 grid (16 directions across the frame)
3. Map each pixel position to a hemisphere direction in world space
4. Project samples into SH basis (order 2 = 9 coefficients per colour channel)
5. Assign to `RenderSettings.ambientProbe`

**SH projection formula (order 2, mathematically correct constants):**
For each sample with direction `d = (x,y,z)` normalised, colour `c`:
```
sh[0,0] += c * 0.282095f                     // L00 — DC/constant term
sh[1,0] += c * 0.488603f * y                 // L1,-1
sh[1,1] += c * 0.488603f * z                 // L1,0
sh[1,2] += c * 0.488603f * x                 // L1,1
sh[2,0] += c * 1.092548f * x*y              // L2,-2
sh[2,1] += c * 1.092548f * y*z              // L2,-1
sh[2,2] += c * 0.315392f * (3*z*z - 1)     // L2,0
sh[2,3] += c * 1.092548f * x*z              // L2,1
sh[2,4] += c * 0.546274f * (x*x - y*y)     // L2,2
```
Normalise by dividing all coefficients by total sample count.

**Unity API:**
```csharp
SphericalHarmonicsL2 sh = new SphericalHarmonicsL2();
sh.Clear();
// For each sample direction dir (Vector3) and colour col (Color):
sh.AddDirectionalLight(dir, col, weight);
// OR build manually via sh[channel, coefficient] indexing
RenderSettings.ambientProbe = sh;
RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
```

**Directional light direction (from MRUK, set once at spawn):**
```csharp
// Check for LAMP anchors first
var lampAnchor = room.Anchors.FirstOrDefault(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.LAMP));
Vector3 lightDir;
if (lampAnchor != null) {
    // Light comes FROM the lamp position TOWARD the floor
    lightDir = (Vector3.down * 0.8f + (lampAnchor.transform.position.normalized) * 0.2f).normalized;
} else {
    lightDir = Vector3.down; // assume overhead
}
_sceneDirectionalLight.transform.rotation = Quaternion.LookRotation(lightDir);
```

**Reflection Probe (placed once, updates every frame automatically):**
```csharp
var probeGO = new GameObject("ARIA_ReflectionProbe");
probeGO.transform.position = spawnedObject.transform.position + Vector3.up * 0.5f;
var probe = probeGO.AddComponent<ReflectionProbe>();
probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame;
probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
probe.resolution = 64; // low res is fine for ambient reflections
probe.size = new Vector3(4, 3, 4);
```

**Fallback if camera unavailable (editor):**
```csharp
SphericalHarmonicsL2 sh = new SphericalHarmonicsL2();
sh.AddAmbientLight(new Color(0.4f, 0.38f, 0.35f)); // neutral warm grey
RenderSettings.ambientProbe = sh;
```

### D. VirtualLightSourceSystem (bonus — high demo impact)

**Concept:** When ARIA spawns a light-emitting object (lamp, lantern, candle, screen),
it automatically adds a Unity PointLight/SpotLight at the correct position on the model.
This light illuminates ALL subsequently spawned virtual objects correctly, and casts
shadows from them onto EffectMesh surfaces. This is one of the most visually impressive
demo moments: spawn a lamp, then spawn a vase next to it — vase is lit from lamp's side,
shadow stretches away across the real floor.

**Claude JSON contract — add these fields to the placement response schema:**
```json
{
  "object": "floor lamp",
  "placement": "corner near wall",
  "emits_light": true,
  "light_type": "point",
  "light_color": [1.0, 0.85, 0.6],
  "light_intensity": 1.2,
  "light_range": 3.0,
  "light_offset": [0.0, 1.4, 0.0]
}
```
`light_offset` is where on the model the bulb is (relative to object root).
Claude can estimate this from the object category — a floor lamp bulb is ~1.4m up.

**Implementation in ARIAOrchestrator — after GLTFast instantiation:**
```csharp
if (placement.emits_light) {
    var lightGO = new GameObject("VirtualLight");
    lightGO.transform.SetParent(spawnedObject.transform);
    lightGO.transform.localPosition = placement.light_offset;

    var light = lightGO.AddComponent<Light>();
    light.type = placement.light_type == "spot" ? LightType.Spot : LightType.Point;
    light.color = new Color(placement.light_color[0], placement.light_color[1], placement.light_color[2]);
    light.intensity = placement.light_intensity;
    light.range = placement.light_range;
    light.shadows = LightShadows.Soft;
    light.shadowStrength = 0.8f;

    // Make bulb mesh emissive — find renderer closest to light_offset
    var renderers = spawnedObject.GetComponentsInChildren<Renderer>();
    // (find nearest renderer to light_offset position and add emissive material)
}
```

**Virtual-to-real light blending (polish effect):**
When a light-emitting object spawns, draw a soft radial gradient additive overlay
on the passthrough in the region around the virtual light source. This fakes the
impression that the virtual lamp is illuminating the real floor/walls.
Implementation: a world-space `Canvas` with a `RawImage` using an additive blend
material, positioned at floor level under the virtual lamp, with a soft circular
gradient texture. Fade alpha based on distance from light source.
This is the technique Meta's own demo apps use for light bleed convincingness.

**What a virtual lamp CANNOT do (important for presentation honesty):**
It cannot physically illuminate the real floor — the passthrough is a camera feed
and Unity has no way to modify real photons. The virtual-to-real blend is a
perceptual trick, not true illumination. The lamp DOES correctly illuminate all
other virtual objects spawned in the scene. This distinction is worth explaining
in the demo: "virtual objects interact with each other's lighting correctly, and we
use a screen-space approximation to suggest light bleed onto real surfaces."

### E. ShadowReceiverSetup

**Problem:** Virtual objects should cast shadows onto real floors/walls so they
don't look like they're floating. Real surfaces are not Unity meshes, so standard
shadow casting won't work.

**Approach — MRUK EffectMesh with shadow-receiver material:**
1. MRUK's `EffectMesh` component generates real Unity meshes for room surfaces.
   Add `EffectMesh` to a GameObject, set `Labels = FLOOR | WALL_FACE`, enable
   `GenerateOnStart = true`. This creates invisible mesh geometry matching real surfaces.
2. Assign a **transparent shadow-receiver shader** to these meshes:
   - Standard approach: `Shader.Find("Universal Render Pipeline/Lit")` with
     surface type Transparent, Receive Shadows = true, alpha = 0
   - Or write a custom URP shader: sample `_ShadowCoord`, output black with
     shadow attenuation as alpha, blend onto passthrough

**Custom URP shadow-receiver shader (minimal):**
```hlsl
Shader "ARIA/ShadowReceiver" {
    Properties { _ShadowStrength("Shadow Strength", Range(0,1)) = 0.7 }
    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-1" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Pass {
            Name "ShadowReceiver"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            float _ShadowStrength;
            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }
            half4 frag(Varyings IN) : SV_Target {
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float shadow = mainLight.shadowAttenuation;
                float alpha = (1.0 - shadow) * _ShadowStrength;
                return half4(0, 0, 0, alpha);
            }
            ENDHLSL
        }
    }
}
```

Apply this shader to the EffectMesh surfaces. Virtual objects cast shadows,
surfaces show darkening, passthrough shows through where alpha = 0.

---

## What Claude Code CAN and CANNOT Do

### Claude Code CAN:
- Write and edit C# scripts in Assets/Scripts/
- Read existing scripts
- Run bash commands (git, etc.)
- Search the web for documentation
- Do math and implement algorithms from scratch
- Generate HLSL/shader code
- Read files you upload to the chat

### Claude Code CANNOT:
- Open the Unity Editor UI
- Drag Building Blocks into the scene hierarchy
- Click buttons in Unity
- See what's in the Hierarchy/Inspector unless told
- Add packages via Package Manager UI
- Configure AndroidManifest.xml through UI

### Therefore, Kartik manually does:
1. **Add Building Blocks** from the BB panel (Camera Rig, Passthrough,
   Hand Grab Interaction, Scene Understanding/MRUK)
2. **Wire Inspector references** (drag components to serialized fields)
3. **Set Android build target** + permissions in Player Settings
4. **Press Build and Run** for APK deployment

### Claude Code handles:
- All C# logic, all shader code, all math
- Script connections via `[SerializeField]` fields that Kartik then wires
- Editor testing infrastructure (UI button fallback, mock data)

---

## Scene Hierarchy — Target State

After all Building Blocks are added manually, scene should look like:

```
SampleScene
├── OVRCameraRig                          ← Camera Rig Building Block
│   └── TrackingSpace
│       ├── CenterEyeAnchor (Camera)
│       ├── LeftHandAnchor
│       └── RightHandAnchor
├── [BuildingBlock] Passthrough           ← Passthrough BB (depends on Camera Rig)
├── [BuildingBlock] Hand Grab Interaction ← Interaction BB (hand tracking + grab)
├── MRUK                                  ← MRUK prefab (from package, not BB panel)
├── App Voice Experience                  ← Voice SDK (not a BB, created via Assets menu)
├── ARIA_Manager                          ← NEW: empty GameObject, holds our scripts
│   ├── ARIAOrchestrator.cs
│   ├── SemanticPlacementEngine.cs
│   ├── ScaleInferenceSystem.cs
│   ├── SphericalHarmonicsLightingEstimator.cs
│   └── ShadowReceiverSetup.cs
└── ARIACanvas (World Space)              ← UI feedback (status text, loading indicator)
```

---

## Editor Testing Strategy

**Goal:** Test the full API pipeline (Claude → Gemini → HiTEM3D → GLB) in
Unity Editor Play mode WITHOUT a headset.

**ARIADebugUI.cs** — create this script. It adds:
- A simple `OnGUI` text field + button
- Typing a command and pressing "Send" calls `ProcessVoiceCommand(text)`
- Pressing "Mock Room" calls a method that loads fake MRUK data
- Status label showing current pipeline stage

**Mock MRUK room JSON** (hardcode as const string in orchestrator):
```json
{
  "room_width": 5.0, "room_depth": 4.0, "room_height": 2.8,
  "anchors": [
    {"label": "FLOOR", "position": [0,-0.05,0], "size": [5,4]},
    {"label": "CEILING", "position": [0,2.8,0], "size": [5,4]},
    {"label": "WALL_FACE", "position": [2.5,1.4,0], "normal": [-1,0,0], "size": [4,2.8]},
    {"label": "WALL_FACE", "position": [-2.5,1.4,0], "normal": [1,0,0], "size": [4,2.8]},
    {"label": "WALL_FACE", "position": [0,1.4,2.0], "normal": [0,0,-1], "size": [5,2.8]},
    {"label": "WALL_FACE", "position": [0,1.4,-2.0], "normal": [0,0,1], "size": [5,2.8]},
    {"label": "TABLE", "position": [1.0,0.75,-0.5], "size": [1.2,0.8], "height": 0.75}
  ]
}
```

When MRUK.Instance returns null (editor), parse this JSON instead.

---

## Meta XR Simulator — for MRUK Testing Without Headset

The **Meta XR Simulator** is a separate tool that can simulate a full Quest
environment including MRUK room data:

1. Install via: Package Manager → Add package from registry → search "Meta XR Simulator"
   OR it may already be in the project from the Mixed Reality template.
2. Activate: `Meta > Meta XR Simulator > Activate` in menu bar
3. Press Play — simulator opens as separate window
4. Has pre-built synthetic rooms (Living Room, Bedroom, etc.) with MRUK data

**This means MRUK.Instance will NOT return null in editor when simulator is active.**
This is the preferred testing path once scene is set up, before deploying to headset.

---

## API Keys and Config

`config.json` exists in `Assets/` folder. Structure expected by orchestrator:
```json
{
  "anthropic_api_key": "sk-ant-...",
  "gemini_api_key": "AIza...",
  "hitem3d_username": "...",
  "hitem3d_password": "..."
}
```

On Quest at runtime: file is copied to `Application.persistentDataPath/config.json`
(or the orchestrator reads from StreamingAssets — confirm which path it uses).
**Never commit this file to GitHub.** Ensure `.gitignore` includes `Assets/config.json`.

---

## GLTFast Warning Fix

The console shows:
`GltfImportBase.LoadGltfBinary(byte[], Uri, ImportSettings, CancellationToken) is Obsolete`

Fix in ARIAOrchestrator.cs — change the LoadGltfBinary call to use
the generic Load method instead. The new API:
```csharp
var gltf = new GltfImport();
bool success = await gltf.Load(glbBytes); // generic Load, not LoadGltfBinary
if (success) {
    await gltf.InstantiateMainSceneAsync(parent.transform);
}
```

---

## Presentation Points (What to Explain at Demo)

These are the "how did you do it" questions the assessors will ask:

1. **Why direct API instead of LLM Building Block?** — Multimodal input requirement,
   structured JSON output, cost and latency control.

2. **How does semantic placement work?** — MRUK `SceneLabels` enum, surface normals
   from anchor.transform.forward for quads, using `GenerateRandomPositionOnSurface`
   for floor placement, raycasting for wall proximity.

3. **How does scale inference work?** — Canonical height dictionary keyed by object
   category, GLB native bounding box measurement, scale = canonical/native, clamped
   and room-height-validated.

4. **How does lighting matching work?** — Spherical harmonics basis functions project
   camera pixel samples into frequency-domain ambient representation,
   fed to Unity's `RenderSettings.ambientProbe` so virtual objects respond to
   real-room light distribution.

5. **How do shadows work?** — MRUK EffectMesh generates real Unity geometry for
   room surfaces, custom URP shader receives Unity shadow maps and renders
   dark semi-transparent overlay on passthrough.

6. **How does interaction work?** — Interaction SDK `HandGrabInteractable` added
   at runtime post-spawn, collider auto-sized to mesh bounds post-scale,
   Quest hand tracking feeds through OVRHand → IHand → HandGrabInteractor.

---

## Known Issues and Gotchas

- **Blender 5.0.1 FCurves:** If animating anything from Blender, fcurves are at
  `action.layers[i].strips[j].channelbags[k].fcurves` not `action.fcurves`.
  (From Lab 9 experience — may not apply to ARIA.)

- **MRUK init is async:** SceneLoadedEvent fires AFTER the scene loads from the
  headset. Do NOT poll MRUK.Instance.GetCurrentRoom() in Start() or Awake().
  Always use the event callback.

- **WebCamTexture on Quest:** First frame may be black. Add a 2-frame delay before
  capturing. Also dispose WebCamTexture when not needed (it keeps camera open).

- **HiTEM3D polling:** ~20s for grey mesh, ~90s for textured. The grey mesh spawn
  is important for UX — show *something* while texture generates.

- **GLTFast and Quest:** GLTFast works on Android/Quest, but test build size.
  The GLTFast package adds ~5MB to build.

- **Interaction SDK version:** Meta XR SDK v62+ includes "Quick Actions" for
  adding grab interactions via right-click context menu. If this is available,
  it generates the correct component hierarchy automatically. Check SDK version.

- **Hand tracking vs controller:** Building Blocks → Hand Grab Interaction installs
  BOTH hand and controller interactors by default. On Quest 3, users can switch
  between hands and controllers at runtime — the SDK handles this automatically
  via InteractorGroup priority switching.

- **AndroidManifest permissions required:**
  - `com.oculus.permission.USE_SCENE` (for MRUK)
  - `android.permission.RECORD_AUDIO` (for Voice SDK)
  - `com.oculus.permission.ACCESS_PASSTHROUGH_CAMERA` (for PCA/WebCamTexture)
  These are added automatically by the SDK if you use the Building Blocks,
  but verify in `Assets/Plugins/Android/AndroidManifest.xml`.

---

## Next Steps in Order

1. **Verify console is clean** (done — no errors, 1 GLTFast warning)
2. **Add Building Blocks manually in Unity:**
   - Camera Rig (may already exist from template)
   - Passthrough
   - Hand Grab Interaction
   - MRUK prefab (from package, not BB panel: `Packages/MR Utility Kit/Prefabs/MRUK.prefab`)
3. **Create App Voice Experience** via Assets menu
4. **Create ARIA_Manager GameObject**, add all 5 scripts
5. **Create ARIADebugUI.cs** for editor testing button
6. **Fix GLTFast warning** in ARIAOrchestrator.cs
7. **Implement SemanticPlacementEngine** using algorithm above
8. **Implement ScaleInferenceSystem** using algorithm above
9. **Test pipeline in editor** with debug UI (text input → API → model)
10. **Install Meta XR Simulator**, test with synthetic room
11. **Implement SH lighting** and shadow receiver
12. **Wire Voice SDK** → orchestrator
13. **Build APK and test on headset**
14. **Add Spatial Anchors** for persistence (nice-to-have)
