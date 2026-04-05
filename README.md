# ARIA — Adaptive Reality and Intelligent Authoring

> AI-driven spatial interior design with perceptual lighting in Mixed Reality

**Author:** Kartik Gupta (guptak1@tcd.ie) | **Platform:** Meta Quest 3 | **Unity 6000.3.8f1 LTS, URP**

---

## What Is ARIA?

ARIA places AI-generated 3D furniture in your real room through Mixed Reality. Speak a command, and ARIA uses multimodal AI to understand your room's layout, style, and lighting — then generates, places, scales, and lights virtual objects so they blend naturally with the real environment.

Two modes:
- **Full pipeline:** Voice command -> Claude spatial reasoning -> Gemini image -> Tripo3D mesh -> contextual placement + lighting
- **Demo mode:** Pre-generated 3D models placed instantly using room scan data, with optional Claude-powered refinement via passthrough image analysis

---

## Pipeline Architecture

```
Voice command (Wit.ai STT)
  |
  v
Claude API (multimodal spatial reasoning)
  - Receives: passthrough camera image + MRUK room scan data + voice transcript
  - Decides: what objects, what style/color/material, what dimensions
  - Outputs: PlacementInstructions (surface, scale, category, light properties)
  |
  v
Gemini (reference image generation)
  |
  v
Claude refinement (optional — compares reference image to room context)
  |
  v
Tripo3D / HiTEM3D (textured 3D GLB mesh generation)
  |
  v
Runtime placement + lighting:
  - EnvironmentRaycastManager  -> depth-based surface placement (Meta SDK)
  - SemanticPlacementEngine    -> MRUK anchor-aware placement for pipeline objects
  - ScaleInferenceSystem       -> proportional scaling from Claude dimensions
  - Multi-light detection      -> passthrough brightness analysis, ceiling projection
  - PTRL HighlightsAndShadows  -> shadows + highlights on real room surfaces (Meta SDK)
  - Claude post-spawn adjustment -> voice + passthrough refinement of position/scale
```

---

## Key Features

### Placement
- **EnvironmentRaycastManager** (Meta Depth API) for accurate surface detection — objects land on floors, walls, tables at the exact gaze point
- **MRUK-aware collision avoidance** — objects fit within room boundaries, don't overlap furniture
- **Gaze-directed** — look at where you want the object, 3-2-1 countdown, it appears there

### Lighting (Primary Research Contribution)
- **Multi-light source detection** from passthrough camera: 16x16 brightness grid -> greedy clustering -> projected to 3D ceiling positions via MRUK height data
- **SH ambient probe** (Ramamoorthi & Hanrahan 2001) computed from passthrough frame for fill light
- **PTRL HighlightsAndShadows shader** (Meta SDK) renders shadows and highlights on real room surfaces from detected virtual lights
- **Per-object directional light** from nearest detected ceiling light for correct shadow direction
- **ARIA vs Default toggle** for before/after comparison

### AI Integration
- **Claude multimodal reasoning** — sees your room (passthrough + MRUK) and decides contextually appropriate placement
- **Voice-assisted adjustment** — speak what you want ("move this to the table, make it smaller"), Claude adjusts with visual + spatial context
- **All spawned objects listed** in Claude context so it reasons about the full scene, not just one object

### Quest 3 Features
- VR Canvas UI with gaze pointer (Y to toggle, trigger to click)
- Passthrough camera access for lighting estimation
- MRUK room scan with EffectMesh visualization
- Occlusion via EnvironmentDepthManager
- Boundary suppression for MR passthrough

---

## Original Engineering Contributions

| Contribution | Description |
|---|---|
| **Multi-light detection** | Detects multiple real light source positions from passthrough camera analysis. Existing tools (PTRL, QuestCameraKit) only provide manual light placement or single-direction estimation. Our clustering algorithm finds distinct sources and projects them to 3D world positions. |
| **Claude spatial reasoning pipeline** | Multimodal AI that sees the room (image + scan data + voice) and reasons about furniture style, scale, placement. No prior Quest 3 project combines LLM reasoning with MRUK spatial data for interior design. |
| **Voice-driven adjustment** | Post-spawn refinement where user speaks intent ("put this on the table corner") and Claude re-evaluates with fresh passthrough capture. |
| **End-to-end generation** | Voice -> AI reasoning -> image generation -> 3D mesh -> contextual placement -> perceptual lighting. Complete pipeline from natural language to placed, lit object. |

---

## Tech Stack

| Component | Technology |
|---|---|
| Platform | Meta Quest 3, Unity 6 URP |
| Room scanning | Meta MRUK + EffectMesh + EnvironmentRaycastManager |
| Surface placement | EnvironmentRaycastManager (depth API) + SemanticPlacementEngine |
| Shadow rendering | PTRL HighlightsAndShadows shader (Meta SDK) |
| Light detection | Custom multi-light clustering from passthrough frames |
| Spatial reasoning | Claude API (claude-sonnet-4-6, multimodal) |
| Image generation | Gemini 2.5 Flash |
| 3D generation | Tripo3D / HiTEM3D (switchable) |
| Runtime GLB loading | GLTFast 6.18 |
| Voice input | Meta Voice SDK (Wit.ai) |
| Ambient lighting | Spherical Harmonics L2 probe |

---

## Project Structure

```
Assets/Scripts/ARIA/
  ARIAOrchestrator.cs              <- Pipeline controller, API calls, spawn logic
  SemanticPlacementEngine.cs       <- MRUK surface placement (pipeline objects)
  ScaleInferenceSystem.cs          <- Proportional real-world scaling
  SphericalHarmonicsLightingEstimator.cs  <- Multi-light detection + SH probe
  ShadowReceiverSetup.cs           <- PTRL shader on EffectMesh surfaces
  ARIADebugUI.cs                   <- VR Canvas UI (gaze pointer, buttons)
  VoiceSDKConnector.cs             <- Wit.ai bridge to orchestrator

Assets/StreamingAssets/
  GLBCache/                        <- Pre-generated demo models (bed, lamp, wall_art)
  config.json                      <- API keys (gitignored)
```

---

## How to Run

### Quest 3 (primary target)
1. Create `Assets/StreamingAssets/config.json` with API keys
2. Build via File -> Build Profiles -> Meta Quest
3. Deploy APK via Meta Quest Developer Hub
4. In headset: press **Y** to open menu
5. Demo spawn: tap Spawn Bed/Lamp/Wall Art -> 3-2-1 countdown -> look at target
6. Adjust: tap "Adjust with Claude" -> speak intent -> look at target
7. Lighting: tap "Apply Lighting" -> "ARIA vs Default" to compare

### Editor (testing)
1. Open `ARIATestScene.unity`, ensure `Assets/config.json` exists
2. Hit Play -> bottom-left debug panel -> type command -> Send

---

## Config

Create `Assets/StreamingAssets/config.json` (gitignored):
```json
{
  "claude_key": "sk-ant-...",
  "gemini_key": "AIza...",
  "hitem_access_key": "ak_...",
  "hitem_secret_key": "sk_...",
  "tripo_key": "tsk_..."
}
```

Automatically copied to device persistent storage on first launch.

---

## Acknowledgements

- Meta MRUK, PTRL, EnvironmentRaycastManager, Depth API
- [MRRealLightCapture](https://github.com/hellomixedworld/MRRealLightCapture) — cubemap lighting concepts
- [QuestCameraKit](https://github.com/xrdevrob/QuestCameraKit) — passthrough camera reference
- [Unity-MRMotifs](https://github.com/oculus-samples/Unity-MRMotifs) — grounding shadow shader reference
- Anthropic Claude, Google Gemini, Tripo3D APIs
