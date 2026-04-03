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
                                  hitem_access_key, hitem_secret_key
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
  - requestType=1 → grey mesh (~90s)
  - requestType=2 → textured mesh (~2-3min), pass meshUrl from step 1
- Poll: `GET https://api.hitem3d.ai/open-api/v1/query-task?task_id={id}` every 5s
  - Status field: check `.ToLower()` for "success"/"failed" (API casing inconsistent)
  - **"unknown" in logs = still generating, PIPELINE IS WORKING, do not stop**
  - Timeout: 5 minutes max
  - Grey mesh typically takes ~90s. Textured ~2-3min. Total ~4-5min per object.

---

## Pipeline Flow (ARIAOrchestrator.cs)

```
ProcessVoiceCommand(transcript)
  → CapturePassthroughFrameAsync()   [Quest: WebCamTexture, Editor: null]
  → SerializeMRUKData()              [Quest: real MRUK, Editor: MockMRUKJson()]
  → CallClaudeAsync(jpeg, mruk, cmd) → List<PlacementInstruction>
  → foreach instruction (concurrent):
      ProcessObjectAsync(instr, jpeg)
        → [cache check] if category in aria_glb_cache.json → SpawnObjectAsync directly
        → CallGeminiAsync(prompt) → byte[] png
        → GetHiTEMTokenAsync() → token
        → SubmitHiTEMTaskAsync(token, png, requestType:1) → greyId
        → PollHiTEMTaskAsync(token, greyId) → greyUrl
        → SpawnObjectAsync(greyUrl, isPreview:true)
        → SubmitHiTEMTaskAsync(token, png, requestType:2, greyUrl) → texId
        → PollHiTEMTaskAsync(token, texId) → texUrl
        → SpawnObjectAsync(texUrl, isPreview:false)
        → save texUrl to GLB cache

SpawnObjectAsync(glbUrl, instr, isPreview, jpeg)
  → UnityWebRequest.Get(glbUrl)
  → GltfImport().Load(bytes) + InstantiateMainSceneAsync()
  → scaleSystem.ApplyScale(root, instr.height_metres, instr.category)
  → placementEngine.Place(root, instr.surface_label)
  → if !isPreview: shEstimator.EstimateLighting(), AddReflectionProbe(), AddVirtualLight(), AddPhysicsAndInteraction()
  → if isPreview: store in _previews dict
  → if !isPreview: destroy preview, remove from dict
```

---

## PlacementInstruction Model (Claude outputs this JSON)
```json
{
  "prompt": "A tall modern floor lamp...",
  "surface_label": "FLOOR",
  "height_metres": 1.5,
  "category": "lamp",
  "emits_light": true,
  "light_type": "point",
  "light_color": [1.0, 0.85, 0.6],
  "light_intensity": 2.0,
  "light_range": 3.0,
  "light_offset": [0, 1.4, 0]
}
```

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

### ✅ WORKING
- Config loading (config.json)
- Claude API → JSON parsing (StripCodeFences handles any response format)
- Gemini image generation (2.5-flash-image stable + 3.1 preview fallback)
- HiTEM3D auth + submit + poll (status logging every 15s with elapsed time)
- GLB cache (newly added, not yet battle-tested)
- Shadow receiver (editor: creates ARIA_ShadowFloor plane)
- ARIADebugUI (Send Command + Mock Room Test buttons, status label)
- Placement editor fallback (1.5m in front of camera)

### ⚠️ NOT YET TESTED END-TO-END
- GLTFast spawn (pipeline was stopped before this stage in all test runs)
- ScaleInferenceSystem (code complete, not yet reached in testing)
- SphericalHarmonicsLightingEstimator (code complete, not yet reached)
- Virtual light spawning
- On-device MRUK (requires Quest APK)
- Voice SDK integration (ARIADebugUI bypasses it for now)

### ❌ KNOWN ISSUES
- `gemini-3.1-flash-image-preview` occasionally hangs with no response (60s timeout now fires a clear error instead of silent hang)
- HiTEM3D status field casing inconsistent — using `.ToLower()` comparison to handle it

---

## Recent Fixes (this session, 2026-04-03)
1. `StripCodeFences` — finds first `[` or `{` to handle any LLM response wrapping
2. `max_tokens` 1024 → 2048 — Claude was truncating JSON mid-array
3. Gemini endpoint — Imagen 3 shutdown → switched to `generateContent` with `gemini-2.5-flash-image`
4. Gemini response parsing — `inlineData` (camelCase) not `inline_data` — added `[JsonProperty]`
5. Claude system prompt — capped at 4 objects, furniture only (was returning 14 items incl. floors/walls)
6. HiTEM3D polling — logs elapsed time every 15s, `.ToLower()` status check, 5min timeout
7. `UnityWebRequest.timeout` — added to all API calls (Claude:30s, Gemini:60s, HiTEM3D:15s)
8. GLB cache — skips Gemini+HiTEM3D for already-generated categories
9. Gemini fallback — tries stable model first, preview second
10. Floor placement — camera-forward 1.5m instead of random on MRUK floor surface
11. Concurrent → sequential object processing (to respect rate limits)
12. Concurrent restored after billing enabled

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
1. **Test full pipeline end-to-end** — let it run past HiTEM3D into GLTFast spawn
2. **Verify object appears in scene** at correct position/scale
3. **Meta XR Simulator** — test with synthetic room data before building APK
4. **APK build** — push config.json to device via adb, test passthrough + real MRUK
5. Voice SDK wiring — `VoiceSDKConnector.cs` subscribes to `AppVoiceExperience.VoiceEvents.OnFullTranscription` and calls `orchestrator.ProcessVoiceCommand(transcript)`
