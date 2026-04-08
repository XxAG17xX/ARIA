# ARIA — Adaptive Reality and Intelligent Authoring

> Voice-driven generative interior design with spatial AI reasoning in Mixed Reality

**Author:** Kartik Gupta (guptak1@tcd.ie) | **Platform:** Meta Quest 3 | **Unity 6000.3.8f1 LTS, URP**

---

## What Is ARIA?

ARIA generates and places 3D furniture in your real room through Mixed Reality on Meta Quest 3. Speak a natural command — "put a red lamp near the door" — and ARIA uses multimodal AI to understand your room, generate a contextually appropriate 3D object, place it on the correct real-world surface at the right scale, and light it to match your environment. You can then physically grab objects, move them between surfaces, and they auto-resize to fit.

**Full pipeline:** Voice → Claude spatial reasoning (sees annotated passthrough + room scan) → Gemini image → Tripo3D mesh → anchor-aware placement + canonical scaling + PTRL lighting

**Demo mode:** Pre-bundled 3D models placed instantly via room scan data — zero API credits

---

## Pipeline Architecture

```
Voice command (Meta Voice SDK / Wit.ai)
  │
  v
Annotated Room Capture
  - Passthrough camera frame + yellow anchor labels (WALL_0, TABLE_1...)
  - Red gaze crosshair at raycast hit point
  - Virtual objects visible, wireframe hidden
  - MRUK room JSON with indexed anchor IDs + dimensions + viewport coords
  │
  v
Claude API (multimodal spatial reasoning)
  - Receives: annotated image + room JSON + voice transcript + user position/gaze
  - Resolves deictic references: "that wall" → WALL_2 (nearest to gaze dot)
  - Decides: object style, dimensions, surface_label, anchor_id, near_anchor_id
  - Voice commands override gaze (explicit prompt priority rule)
  │
  v
Gemini (reference image generation)
  │
  v
Claude refinement (optional — compares reference image to room context, adjusts dimensions)
  │
  v
Tripo3D (textured 3D GLB mesh generation)
  │
  v
Runtime Systems:
  - ScaleInferenceSystem       → uniform scaling from Claude dimensions, canonical cap
  - SemanticPlacementEngine    → anchor-aware MRUK placement, collision-aware spiral
  - FitToAvailableSpace        → room-level shrink (walls, ceiling), surface-aware Y
  - FitToSurface               → surface-specific proportional resize + canonical max
  - ARIAInteractable           → gravity drop (floor items) / wall magnet snap (wall items)
  - PTRL HighlightsAndShadows  → shadows on real surfaces (directional + point light modes)
  - Claude post-spawn adjustment → voice + annotated capture refinement
```

---

## Key Features

### Anchor-Aware Spatial Placement
- **Deictic reference resolution** — "put a painting on THAT wall" → Claude sees gaze dot near WALL_2 label, returns `anchor_id: "WALL_2"`
- **MRUK collision-aware placement** — golden-angle spiral search avoids real furniture + virtual objects
- **Surface-specific placement** — floor (gaze raycast), wall (plane intersection + PlaneRect clamping), table/couch (volume top surface)
- **Proximity hints** — "near the door" → `near_anchor_id: "DOOR_0"` nudges position toward that anchor
- **Gaze-directed** — 3-2-1 countdown, object appears where crosshair points

### Smart Scaling & Canonical Dimensions
- **Uniform proportional scaling** — height-based, never squashes/stretches objects
- **35+ category canonical dictionary** — real-world dimensions (bed: 1.4×0.5×2.0m, lamp: 0.3×1.5×0.3m)
- **Surface-aware fit** — objects shrink proportionally to fit tables, couches, beds, or any volume surface
- **Never grow beyond real-world size** — a bed can shrink to fit a table, but never expands beyond bed-sized

### Physical Interaction
- **Grab with right grip** — hold to move objects with controller, release to drop
- **Floor items** (bed, lamp, chair) — fall with gravity, land on surfaces, auto-resize + upright correction
- **Wall items** (painting, clock, mirror) — magnet-snap to nearest wall within 15cm, or fall if too far
- **Right thumbstick** — rotate grabbed objects (X=turn, Y=tilt)
- **Left thumbstick** — scale grabbed objects (forward=bigger, backward=smaller)
- **A button** — reset rotation to upright while holding
- **Surface detection** — DetectSurfaceBelow checks all volume anchors (TABLE, BED, COUCH, OTHER, STORAGE)

### Lighting (PTRL-based)
- **Manual light sphere placement** — left grip spawns sphere, grab + position at real ceiling light, confirm → passthrough color sampling
- **Dual shadow modes** — Directional (sharp parallel shadows) / Point Light (per-object cubemap shadows)
- **PTRL shadow surfaces** — invisible floor, wall, and ceiling planes with Meta's HighlightsAndShadows shader
- **Point light shadow boosting** — ShadowIntensity 6.0, intensity height×4, range height×3 for visible cubemap shadows
- **Wireframe independent** — EffectMesh visibility and PTRL toggle are separate controls

### Voice-Driven Adjustment
- **Conversational refinement** — "make it bigger", "move it left", "put it on that wall"
- **Words override gaze** — explicit prompt priority prevents relocation when user wants positional offset
- **Annotated capture** — Claude sees virtual objects + anchor labels + gaze dot for spatial context
- **Scale factor capped** — Claude's adjustments bounded by canonical real-world dimensions

### Quest 3 Platform
- VR Canvas UI with gaze pointer (Y to toggle, trigger to click)
- Passthrough camera for room capture + lighting estimation
- MRUK room scan with EffectMesh wireframe visualization
- Environment depth occlusion enabled at startup
- One-shot voice recording (no ambient speech capture)

---

## Tech Stack

| Component | Technology |
|---|---|
| Platform | Meta Quest 3, Unity 6000.3.8f1 LTS, URP |
| Room scanning | Meta MRUK + EffectMesh (DeviceWithPrefabFallback) |
| Surface placement | SemanticPlacementEngine (MRUK anchors, collision-aware spiral) |
| Shadow rendering | PTRL HighlightsAndShadows shader (Meta SDK) |
| Lighting | Manual light spheres + passthrough color sampling |
| Spatial reasoning | Claude API (claude-sonnet-4-6, multimodal vision) |
| Image generation | Gemini 2.5 Flash |
| 3D generation | Tripo3D (GLB mesh with textures) |
| Runtime GLB loading | GLTFast 6.18 |
| Voice input | Meta Voice SDK (Wit.ai, one-shot mode) |
| Interaction | Controller grip grab + ARIAInteractable (gravity/wall snap) |

---

## Project Structure

```
Assets/Scripts/ARIA/
  ARIAOrchestrator.cs              <- Pipeline controller, API calls, PTRL, anchor registry
  ARIADebugUI.cs                   <- VR Canvas UI, gaze pointer, controller grab, voice input
  ARIAInteractable.cs              <- Post-grab behavior: gravity drop / wall magnet snap
  SemanticPlacementEngine.cs       <- MRUK placement, FitToSurface, FindNearestWall, DetectSurfaceBelow
  ScaleInferenceSystem.cs          <- Uniform scaling, canonical dimensions dictionary
  VoiceSDKConnector.cs             <- Wit.ai bridge, one-shot recording mode
  ShadowReceiverSetup.cs           <- PTRL material creation from HighlightsAndShadows shader
  SphericalHarmonicsLightingEstimator.cs  <- Legacy SH probe (kept for reference)
  BillboardFaceCamera.cs           <- Anchor label billboarding

Assets/StreamingAssets/GLBCache/   <- Pre-bundled demo models (bed.glb, lamp.glb, wall_art.glb)
```

---

## How to Run

### Quest 3 (primary target)
1. API keys are hardcoded in ARIAOrchestrator.cs (stripped before git push)
2. Build via File → Build Profiles → Meta Quest 3
3. Deploy APK via Meta Quest Developer Hub
4. In headset:
   - **Y** to open menu
   - **"Speak to ARIA"** → voice command → 3-2-1 countdown → look at target surface
   - **Demo spawns** → Spawn Bed / Lamp / Wall Art (zero credits)
   - **"Adjust with Claude"** → speak adjustment → close UI (Y) → look at object
   - **Left grip** → place light sphere → position → Confirm → PTRL toggle for shadows
   - **Right grip** → grab objects → move → release (gravity or wall snap)
   - **"Toggle Anchors"** → see MRUK anchor labels in room
   - **"Shadow Mode"** → cycle Directional ↔ Point Light

---

## Controller Reference

| Button | Action |
|--------|--------|
| Y (left) | Toggle UI menu |
| Left grip | Place light sphere at crosshair |
| Right grip (hold) | Grab nearest object or light |
| Right thumbstick | Rotate grabbed object (X=turn, Y=tilt) |
| Left thumbstick Y | Scale grabbed object (fwd=bigger, back=smaller) |
| A button | Reset grabbed object rotation to upright |
| X button | Cycle selection / delete selected object |
| Right trigger | UI button click (gaze pointer) |

---

## Acknowledgements

- Meta MRUK, PTRL HighlightsAndShadows, Depth API, Voice SDK
- Anthropic Claude API, Google Gemini, Tripo3D
- [GLTFast](https://github.com/atteneder/glTFast) — runtime GLB loading
- [Unity-MRMotifs](https://github.com/oculus-samples/Unity-MRMotifs) — grounding shadow shader reference
