# ARIA — Adaptive Reality and Intelligent Authoring

> Voice-driven spatial 3D content generation with perceptual lighting in Mixed Reality

**Author:** Kartik Gupta (guptak1@tcd.ie) | **Platform:** Meta Quest 3 | **Unity 6000.3.8f1 LTS, URP**

---

## What Is ARIA?

Speak a natural language command — *"put a reading lamp in the corner"* — and ARIA generates contextually appropriate 3D objects, places them on real room surfaces at physically correct scale, lights them to match your actual room illumination, and makes them immediately interactive with bare hands.

No buttons. No menus. Just speak.

---

## The Problem

Virtual content in MR environments looks synthetic. Wrong lighting direction, no shadows, arbitrary scale, physically absurd placement. The closest prior work — *Say It, See It* (IEEE VR 2025) — generates a single object at gaze point with no room awareness, no surface anchoring, no lighting coherence, and no post-spawn interaction. ARIA addresses every gap.

---

## Pipeline

```
Voice command
  |
  v
Claude API (spatial reasoning over MRUK room scan + passthrough camera image)
  - Decides what objects to generate, where to place them
  - Contextual: style, color, material matched to room aesthetic
  - Outputs dimensions proportional to room size (height + width + depth)
  |
  v
Gemini (AI reference image generation — studio-lit, white background)
  |
  v
Claude refinement pass (optional — sees reference image + room layout)
  - Adjusts dimensions if image proportions differ from plan
  - Refines style description before 3D generation
  |
  v
HiTEM3D or Tripo3D (textured 3D GLB mesh — switchable in Inspector)
  - Cheapest settings for testing (low poly, basic texture)
  |
  v
GLTFast (runtime GLB loading into scene)
  |
  v
Five ARIA systems:
  - SemanticPlacementEngine  -> correct MRUK surface (floor/wall/table/ceiling)
  - ScaleInferenceSystem     -> proportional scaling (H x W x D, not just height)
  - SH LightingEstimator     -> matches real room illumination
  - ShadowReceiverSetup      -> MRUK mesh receives shadows from virtual objects
  - ARIAOrchestrator         -> coordinates everything, concurrent object processing
```

All processing via HTTPS over WiFi. No local server. Works in any room.

---

## Current Status

### Working (confirmed end-to-end in editor)
- Full pipeline: voice command -> Claude -> Gemini -> 3D API -> GLTFast -> placed + scaled in scene
- Claude contextual reasoning (object style/color/dimensions from room context)
- Claude refinement pass (sees Gemini image, adjusts dimensions before 3D generation)
- Two 3D providers: HiTEM3D and Tripo3D (switchable, saves credits)
- GLB cache (skips entire generation pipeline for previously generated categories)
- Proportional scaling (width + depth, not just height)
- Demo toggles: lighting, physics, Claude refinement all toggleable in Inspector
- Debug UI with "Apply Lighting" button for staged demos

### Not yet tested
- On-device MRUK (requires Quest 3 APK build)
- Voice SDK integration (debug UI bypasses it for now)
- Meta XR Simulator
- Hand grab interaction (code ready, needs Interaction SDK Building Block in scene)

### Next steps
1. Test with Tripo3D provider (cheaper, faster)
2. Meta XR Simulator testing with synthetic room data
3. APK build for Quest 3
4. Voice SDK wiring
5. Final README + demo video

---

## Original Engineering Contributions

| System | File | Description |
|---|---|---|
| SemanticPlacementEngine | `SemanticPlacementEngine.cs` | Maps Claude's surface labels to MRUK anchors. Floor: 1.5m in front of camera. Wall: closest wall, object faces room. Table/Couch: top of volume anchor. |
| ScaleInferenceSystem | `ScaleInferenceSystem.cs` | Proportional scaling using all 3 dimensions from Claude. Non-uniform scale when width/depth differ. Canonical height fallback dictionary. Room height sanity check. |
| SH Lighting Estimator | `SphericalHarmonicsLightingEstimator.cs` | Samples passthrough camera frame, computes SH coefficients, updates ambient probe + directional light. |
| Shadow Receiver | `ShadowReceiverSetup.cs` | Custom URP shader on MRUK EffectMesh — transparent surface that only receives shadows. |
| ARIA Orchestrator | `ARIAOrchestrator.cs` | Master pipeline controller. Concurrent object processing, two-pass Claude, provider switching, GLB caching, progressive placement. |

---

## Tech Stack

| Component | Technology |
|---|---|
| Platform | Meta Quest 3, Unity 6 URP |
| Room scanning | Meta MRUK (Mixed Reality Utility Kit) |
| Spatial reasoning | Claude API (claude-sonnet-4-6) |
| Image generation | Gemini 2.5 Flash (stable) + 3.1 Flash Preview (fallback) |
| 3D generation | HiTEM3D v1.5 / Tripo3D (switchable in Inspector) |
| Runtime GLB loading | GLTFast |
| Hand interaction | Meta Interaction SDK |
| Lighting | Spherical Harmonics (Ramamoorthi & Hanrahan 2001) |
| JSON | Newtonsoft.Json |

---

## Project Structure

```
Assets/Scripts/ARIA/
  ARIAOrchestrator.cs              <- Master pipeline controller
  SemanticPlacementEngine.cs       <- MRUK surface placement
  ScaleInferenceSystem.cs          <- Proportional real-world scaling
  SphericalHarmonicsLightingEstimator.cs  <- SH ambient lighting
  ShadowReceiverSetup.cs           <- Shadow receiver on MRUK mesh
  ARIADebugUI.cs                   <- Editor testing UI (bottom-left panel)
  FlyCamera.cs                     <- Editor camera (WASD + right-click look)

Assets/Shaders/ARIA/
  ShadowReceiver.shader            <- Custom URP transparent shadow receiver

Assets/Scenes/
  ARIATestScene.unity              <- Clean test scene with ARIA_Manager wired
```

---

## How to Run (Editor)

1. Open `ARIATestScene.unity`
2. Make sure `Assets/config.json` exists with all 5 keys (see below)
3. Hit Play
4. Bottom-left debug panel: type a command and click **Send Command**
5. Watch console — pipeline logs every stage with checkmarks/timers
6. **Do NOT stop play mode during HiTEM3D/Tripo3D generation** (takes 1-5 min, this is normal)
7. After first successful run, `aria_glb_cache.json` saves URLs for instant reruns

### Inspector toggles on ARIA_Manager:
- **meshProvider** — switch between HiTEM3D and Tripo3D
- **enableClaudeRefinement** — two-pass Claude (adds ~10s, improves accuracy)
- **enableLighting** — toggle SH lighting and reflection probes
- **enablePhysics** — toggle Rigidbody + colliders

---

## Config

Create `Assets/config.json` (gitignored — never commit this):
```json
{
  "claude_key": "sk-ant-...",
  "gemini_key": "AIza...",
  "hitem_access_key": "ak_...",
  "hitem_secret_key": "sk_...",
  "tripo_key": "tsk_..."
}
```

For Quest APK: push via `adb push config.json /sdcard/Android/data/<package>/files/config.json`

---

## Known Limitations

- SH lighting is view-dependent (documented approximation)
- 1-5 min generation latency per object (mitigated by caching + progressive placement)
- Tripo3D free tier: 300 credits/month (~10 models). HiTEM3D: limited credits.
- Editor mode uses mock room data (5x4x2.8m room). Real MRUK requires Quest APK.
