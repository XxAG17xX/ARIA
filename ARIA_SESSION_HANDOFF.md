# ARIA — Session Handoff Document
*Last updated: 2026-04-03. Read this before touching any code.*

---

## What ARIA Is
Mixed reality Unity app for Meta Quest 3. User speaks a voice command ("put a reading lamp in the corner"), ARIA:
1. Captures passthrough camera frame (WebCamTexture — Quest only, null in editor)
2. Sends frame + MRUK room layout + voice command → Claude API → JSON array of PlacementInstructions
3. For each object concurrently: Gemini generates a reference image → HiTEM3D generates a 3D GLB mesh → GLTFast loads it → placed, scaled, lit in scene

**Deadline: April 10, 2026. Solo project by Kartik Gupta (guptak1@tcd.ie), TCD student.**

---

## Tech Stack
- Unity 6000.3.8f1 LTS, URP, Android/Meta Quest 3
- Meta XR All-in-One SDK (MRUK, OVRCameraRig)
- GLTFast (runtime GLB loading)
- Newtonsoft.Json
- **NEVER use HttpClient** — use UnityWebRequest only (Quest doesn't support HttpClient)
- No async/await with `UnityEngine.Object` — use TaskCompletionSource bridge (`AwaitRequest`)

---

## Key Files
```
Assets/Scripts/ARIA/
  ARIAOrchestrator.cs          ← Master pipeline controller (READ THIS FIRST)
  SemanticPlacementEngine.cs   ← Places objects on MRUK surfaces
  ScaleInferenceSystem.cs      ← Scales GLB to real-world height
  SphericalHarmonicsLightingEstimator.cs  ← SH ambient lighting (runs ONCE at spawn)
  ShadowReceiverSetup.cs       ← MRUK EffectMesh shadow receiver
  ARIADebugUI.cs               ← Editor testing UI (OnGUI panel, bottom-left)

Assets/
  ARIA_IMPLEMENTATION_GUIDE.md ← Original spec, read for full design intent
  config.json                  ← API keys (NOT in git). Keys: claude_key, gemini_key,
                                  hitem_access_key, hitem_secret_key, tripo_key
  aria_glb_cache.json          ← GLB URL cache (NOT in git, auto-created at runtime)
Assets/Shaders/ARIA/
  ShadowReceiver.shader        ← Custom URP transparent shadow receiver
```

---

## API Configuration

### Claude
- Endpoint: `https://api.anthropic.com/v1/messages`
- Model: `claude-sonnet-4-6`
- max_tokens: `2048` (important — 1024 causes JSON truncation)
- Headers: `x-api-key`, `anthropic-version: 2023-06-01`
- timeout: 30s on UnityWebRequest

### Gemini (image generation)
- Primary model: `gemini-2.5-flash-image` (stable)
- Fallback model: `gemini-3.1-flash-image-preview` (Nano Banana 2, newer but flaky)
- Endpoint: `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={gemini_key}`
- **DO NOT use Imagen 3** — it was shut down by Google (that's why it 404s)
- Request body uses `contents`/`parts` format (NOT `instances`/`parameters` — that's Vertex AI)
- Response: `candidates[0].content.parts[].inlineData.data` (camelCase! Use `[JsonProperty("inlineData")]`)
- timeout: 60s on UnityWebRequest

### HiTEM3D (3D mesh generation)
- Auth: `POST https://api.hitem3d.ai/open-api/v1/auth/token` with Basic auth (base64 access:secret)
- Submit: `POST https://api.hitem3d.ai/open-api/v1/submit-task` multipart form
  - requestType=3 → all-in-one (geometry+texture), 512 resolution
- Poll: `GET https://api.hitem3d.ai/open-api/v1/query-task?task_id={id}` every 5s
  - Response field: `data.state` (NOT `data.status`) — "created"/"queueing"/"processing"/"success"/"failed"
  - Timeout: 10 minutes max. Typically 3-5 min per object.

### Tripo3D (alternative 3D mesh generation — 300 free credits/month)
- Auth: Bearer token header `Authorization: Bearer tsk_...`
- Upload: `POST https://api.tripo3d.ai/v2/openapi/upload` multipart → `data.image_token`
- Create task: `POST https://api.tripo3d.ai/v2/openapi/task` JSON body:
  ```json
  {"type": "image_to_model", "file": {"type": "png", "file_token": "..."}, "face_limit": 10000, "texture": true, "pbr": false}
  ```
- Poll: `GET https://api.tripo3d.ai/v2/openapi/task/{task_id}` → `data.status`: "queued"/"running"/"success"/"failed"
- GLB URL: `data.output.model`
- Cost: ~30 credits per textured model (300 free/month = ~10 models)
- Typically faster than HiTEM3D (~1-2 min)
- Switch between providers in Inspector: `meshProvider` dropdown on ARIAOrchestrator

---

## Pipeline Flow (ARIAOrchestrator.cs)

```
ProcessVoiceCommand(transcript)
  → CapturePassthroughFrameAsync()   [Quest: WebCamTexture, Editor: null]
  → SerializeMRUKData()              [Quest: real MRUK, Editor: MockMRUKJson()]
  → CallClaudeAsync(jpeg, mruk, cmd) → List<PlacementInstruction>
  → foreach instruction (concurrent):
      ProcessObjectAsync(instr, jpeg, mrukJson)
        → [cache check] if category in aria_glb_cache.json → SpawnObjectAsync directly
        → [1/4] CallGeminiAsync(prompt) → byte[] png (reference image)
        → [2/4] CallClaudeRefinementAsync(instr, png, mrukJson) → refined instr
              (Claude sees Gemini image + room layout, adjusts dimensions/style)
              Toggle: enableClaudeRefinement in Inspector (default: true)
        → [3/4] GetHiTEMTokenAsync() → token
        → [4/4] SubmitHiTEMTaskAsync(token, png, requestType:3) → taskId
        → PollHiTEMTaskAsync(token, taskId) → glbUrl
        → SpawnObjectAsync(glbUrl, isPreview:false)
        → save glbUrl to GLB cache

SpawnObjectAsync(glbUrl, instr, isPreview, jpeg)
  → UnityWebRequest.Get(glbUrl)
  → GltfImport().Load(bytes) + InstantiateMainSceneAsync()
  → scaleSystem.ApplyScale(root, height, category, width, depth)  ← proportional scaling
  → placementEngine.Place(root, instr.surface_label)
  → if !isPreview: shEstimator.EstimateLighting(), AddReflectionProbe(), AddVirtualLight(), AddPhysicsAndInteraction()
  → if isPreview: store in _previews dict
  → if !isPreview: destroy preview, remove from dict
```

---

## PlacementInstruction Model (Claude outputs this JSON)
```json
{
  "prompt": "A tall modern floor lamp with brushed brass finish, warm tone matching hardwood floors...",
  "surface_label": "FLOOR",
  "height_metres": 1.5,
  "width_metres": 0.3,
  "depth_metres": 0.3,
  "category": "lamp",
  "emits_light": true,
  "light_type": "point",
  "light_color": [1.0, 0.85, 0.6],
  "light_intensity": 2.0,
  "light_range": 3.0,
  "light_offset": [0, 1.4, 0]
}
```
Note: `width_metres` and `depth_metres` are new — Claude sizes objects proportionally to the room and existing furniture. The refinement pass (step 2/4) can further adjust these after seeing the Gemini reference image.

---

## GLB Cache System
- On `Awake`: loads `Assets/aria_glb_cache.json` into `_glbCache` dict (category → URL)
- On successful textured mesh: saves category→URL to cache
- Cache hit: skips entire Gemini+HiTEM3D pipeline, spawns directly from URL
- This saves credits and makes repeat testing instant
- `aria_glb_cache.json` is gitignored

---

## MRUK / Room Data
- **Editor**: Uses `MockMRUKJson()` — hardcoded 5×4×2.8m room with 7 anchors (FLOOR, CEILING, 4×WALL_FACE, TABLE)
- **Quest APK**: Uses real `MRUK.Instance.GetCurrentRoom()` anchors
- All MRUK code is gated: `#if UNITY_ANDROID && !UNITY_EDITOR`
- Camera position/forward is NOT sent to Claude (future improvement)
- Floor placement: 1.5m in front of camera (both editor and device). NOT random.
- Wall placement: closest wall to camera, object faces into room

---

## Placement Logic (SemanticPlacementEngine.cs)
- FLOOR → 1.5m in front of Camera.main, projected onto floor Y
- WALL_FACE → closest wall anchor to camera, offset by object half-depth + 5cm
- TABLE/COUCH → top-centre of volume anchor, offset up by half object height
- CEILING → ceiling anchor Y, offset down
- Editor fallback: same camera-forward logic for all surface types

---

## Current Status (as of 2026-04-03)

### ✅ WORKING (confirmed end-to-end)
- Full pipeline: Voice → Claude → Gemini → HiTEM3D → GLTFast → placed + scaled + lit
- Config loading (config.json)
- Claude API → JSON parsing (StripCodeFences handles any response format)
- Claude contextual reasoning (style/color/material from room + passthrough image)
- Claude refinement pass (sees Gemini reference image, adjusts dimensions — toggle in Inspector)
- Gemini image generation (2.5-flash-image stable + 3.1 preview fallback)
- HiTEM3D auth + submit + poll (all-in-one requestType=3, 512 resolution)
- GLTFast spawn (objects appear in scene with correct materials)
- ScaleInferenceSystem (uniform + proportional width/height/depth scaling)
- SemanticPlacementEngine (floor 1.5m in front of camera, wall/table/ceiling)
- SphericalHarmonicsLightingEstimator (editor fallback working)
- GLB cache (saves credits, instant on cache hit)
- Shadow receiver (editor: creates ARIA_ShadowFloor plane)
- ARIADebugUI (Send Command + Mock Room Test + Spawn Test GLB buttons)
- Virtual light spawning (lamps get point/spot lights)

### ⚠️ NOT YET TESTED
- On-device MRUK (requires Quest APK)
- Voice SDK integration (ARIADebugUI bypasses it for now)
- Meta XR Simulator
- Proportional scaling with real Claude width/depth values (code ready)

### ❌ KNOWN ISSUES
- `gemini-3.1-flash-image-preview` occasionally hangs with no response (60s timeout fires clear error)
- HiTEM3D all-in-one can take 3-5 min under load — do NOT stop play mode

---

## Recent Fixes (2026-04-03)
1. `StripCodeFences` — finds first `[` or `{` to handle any LLM response wrapping
2. `max_tokens` 1024 → 2048 — Claude was truncating JSON mid-array
3. Gemini endpoint — Imagen 3 shutdown → switched to `generateContent` with `gemini-2.5-flash-image`
4. Gemini response parsing — `inlineData` (camelCase) not `inline_data` — added `[JsonProperty]`
5. Claude system prompt — strict object count (only what user asked for)
6. HiTEM3D — switched to all-in-one (requestType=3), reads `data.state` not `data.status`
7. HiTEM3D polling — 600s timeout, logs elapsed time every 15s
8. `UnityWebRequest.timeout` — all API calls (Claude:30s, Gemini:60s, HiTEM3D:15s)
9. GLB cache — skips Gemini+HiTEM3D for already-generated categories
10. Gemini fallback — tries stable model first, preview second
11. Floor placement — camera-forward 1.5m instead of random
12. **Claude contextual reasoning** — prompt/style/dimensions based on room context + passthrough image
13. **Claude refinement pass** — sees Gemini reference image, adjusts dimensions before HiTEM3D
14. **Proportional scaling** — width_metres + depth_metres in PlacementInstruction, non-uniform scale

---

## Testing Instructions
1. Make sure `Assets/config.json` exists with all 4 keys
2. Hit Play in Unity Editor
3. Bottom-left panel shows "Send Command" text field and "Mock Room Test" button
4. Type a command and click Send Command OR click Mock Room Test
5. Watch console — pipeline logs every stage with ✔/✖/⏳ prefix
6. **Do NOT stop Play mode when you see HiTEM3D logging "still generating" — that is NORMAL and takes ~90s**
7. After first successful run, `Assets/aria_glb_cache.json` will exist with cached URLs
8. Subsequent runs with same category will be instant (cache hit)

---

## Git / Commit Rules
- Author: Kartik Gupta <guptak1@tcd.ie>
- NO Co-Authored-By Claude lines. NO mention of Claude anywhere in commits or code comments.
- Commit prefix convention: `feat:`, `fix:`, `chore:`, `refactor:`
- Never commit `config.json` or `aria_glb_cache.json` (both gitignored)
- Push to: https://github.com/XxAG17xX/ARIA (main branch)

---

## Next Steps (priority order)
1. **Test refinement pass** — run pipeline, check console for "✎ Refinement:" logs showing dimension adjustments
2. **Verify proportional scaling** — objects should have non-uniform scale when width/depth differ from height ratio
3. **Meta XR Simulator** — test with synthetic room data before building APK
4. **APK build** — push config.json to device via adb, test passthrough + real MRUK
5. Voice SDK wiring — `VoiceSDKConnector.cs` subscribes to `AppVoiceExperience.VoiceEvents.OnFullTranscription` and calls `orchestrator.ProcessVoiceCommand(transcript)`
