# ARIA — Adaptive Reality and Intelligent Authoring

> Voice-driven spatial 3D content generation with perceptual lighting in Mixed Reality


---

## What Is ARIA?

Speak a natural language command — *"make this corner feel like a reading nook"* — and ARIA generates multiple contextually appropriate 3D objects, places them on real room surfaces at physically correct scale, lights them to match your actual room illumination, and makes them immediately interactive with bare hands.

No buttons. No menus. Just speak.

---

## The Problem

Virtual content in MR environments looks synthetic. Wrong lighting direction, no shadows, arbitrary scale, physically absurd placement. The closest prior work — *Say It, See It* (IEEE VR 2025) — generates a single object at gaze point with no room awareness, no surface anchoring, no lighting coherence, and no post-spawn interaction. ARIA addresses every gap.

---

## Original Engineering Contributions

| System | Description |
|---|---|
| SemanticPlacementEngine | Maps object categories to valid MRUK surfaces, corrects invalid LLM placements |
| ScaleInferenceSystem | Derives real-world scale from mesh bounding box + LLM height estimate |
| SphericalHarmonicsLightingEstimator | Samples passthrough camera → SH coefficients → real room lighting on virtual objects |
| ShadowReceiverSetup | MRUK scene mesh receives physically correct shadows from virtual objects |
| ARIAOrchestrator | Async multi-object queue, progressive placement, full pipeline coordination |

---

## Pipeline

```
Voice → Claude (spatial reasoning over room scan + passthrough image)
      → Gemini Nano Banana 2 (reference image)
      → HiTEM3D API (textured GLB mesh)
      → GLTFast (runtime loading)
      → Placement + scale + lighting + shadows + hand interaction
```

All processing via HTTPS over WiFi. No local server. Works in any room.

---

## Tech Stack

| Component | Technology |
|---|---|
| Platform | Meta Quest 3, Unity 6 URP |
| Room scanning | Meta MRUK |
| Spatial reasoning | Claude API (claude-sonnet-4-6) |
| Image generation | Gemini Nano Banana 2 |
| 3D generation | HiTEM3D API v1.5 |
| Runtime loading | GLTFast |
| Hand interaction | Meta Interaction SDK |
| Lighting | Spherical Harmonics (Ramamoorthi & Hanrahan 2001) |

---

## Setup

Create `config.json` in project root (gitignored):
```json
{
  "claude_key": "YOUR_KEY",
  "gemini_key": "YOUR_KEY",
  "hitem_access_key": "YOUR_KEY",
  "hitem_secret_key": "YOUR_KEY"
}
```

Build and deploy APK to Quest 3. Passthrough camera and MRUK require device build.

---

## Known Limitations

- SH lighting is view-dependent — approximation documented honestly
- ~60-90s generation latency per object — mitigated by progressive placement

---

