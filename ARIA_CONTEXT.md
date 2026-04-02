# ARIA — Project Context
## Read this at the start of every Claude Code session

---

## What ARIA Is

ARIA (Adaptive Reality and Intelligent Authoring) is a Meta Quest 3 mixed reality app. The user speaks a natural language command — e.g. "make this corner feel like a reading nook" — and multiple 3D objects are generated, placed on real room surfaces at correct scale, lit by the actual room lighting, and immediately interactive with bare hands.

**Module:** Extended Reality CSU44054/CS7GV4, Trinity College Dublin
**Student:** Kartik Gupta (24371670)
**Supervisors:** Gareth Young, Binh-Son Hua
**Deadline:** ~April 10, 2026
**Maps to:** Project 6 "Make it Home" from module brief

---

## Unity Project

- **Engine:** Unity 6000.3.8f1 LTS
- **Template:** Mixed Reality (MR)
- **Render Pipeline:** URP
- **Target:** Android, Meta Quest 3
- **Location:** C:\Users\gupta\UNITYPROJ\ARIA

### Packages Already Installed
- Meta XR All-in-One SDK
- GLTFast (com.unity.cloud.gltfast)
- Newtonsoft Json (com.unity.nuget.newtonsoft-json)
- MRUK (comes with Meta XR SDK)
- Unity MCP

---

## API Credentials

Keys are stored in config.json in the project root. Never commit config.json — it is in .gitignore.

- claude_key: [see config.json]
- gemini_key: [see config.json]
- hitem_access_key: [see config.json]
- hitem_secret_key: [see config.json]

---

## Full Pipeline

Voice transcript
  → Claude API (image + MRUK JSON + voice) → JSON array of placement instructions
  → For each object:
      Gemini Nano Banana 2 (text → PNG, white background, studio lighting)
      → HiTEM3D API (PNG → GLB, three-step auth, poll until success)
      → GLTFast (load bytes → instantiate)
      → ScaleInferenceSystem (bounding box → real-world height)
      → SemanticPlacementEngine (validate surface → MRUK FindSpawnPositions)
      → SphericalHarmonicsLightingEstimator (passthrough → SH coefficients → light probe)
      → ShadowReceiverSetup (MRUK mesh receives shadows)
      → HandGrabInteractable (grab, move, rotate, resize)

Objects appear progressively — each placed as soon as its own generation finishes.

---

## API Reference

### Claude
- URL: https://api.anthropic.com/v1/messages
- Method: POST
- Headers: x-api-key, anthropic-version: 2023-06-01, content-type: application/json
- Model: claude-sonnet-4-6
- Body: multimodal — image block (passthrough JPEG base64) + text block (MRUK JSON + voice command)
- System prompt: "You are a spatial interior design AI. Respond with valid JSON only. No explanation."
- Response: JSON array in content[0].text
- Format: [{prompt, surface_label, height_metres, category}]

### Gemini
- URL: https://generativelanguage.googleapis.com/v1beta/models/imagen-3.0-generate-002:predict?key=KEY
- Method: POST
- Body: {"instances": [{"prompt": "...single object, white background, studio lighting, photorealistic"}], "parameters": {"sampleCount": 1}}
- Response: base64 PNG in predictions[0].bytesBase64Encoded

### HiTEM3D — THREE STEPS

Step 1 — Get Token:
- POST https://api.hitem3d.ai/open-api/v1/auth/token
- Header: Authorization: Basic base64(access_key:secret_key)
- Response: data.accessToken

Step 2 — Submit Task:
- POST https://api.hitem3d.ai/open-api/v1/submit-task
- Header: Authorization: Bearer {accessToken}
- Body multipart/form-data: images (PNG bytes), request_type=3, model=hitem3dv1.5, format=2, resolution=1024
- Response: data.task_id

Step 3 — Poll:
- GET https://api.hitem3d.ai/open-api/v1/query-task?task_id=ID
- Header: Authorization: Bearer {accessToken}
- Poll every 5 seconds
- States: created → queueing → processing → success / failed
- On success: data.url = GLB download link (valid 1 hour)

Staged generation trick: request_type=1 first (grey mesh ~20s shown immediately), then request_type=2 with mesh URL for texture. Better perceived latency for demo.

### GLTFast
var gltf = new GltfImport();
bool success = await gltf.LoadGltfBinary(glbBytes, new Uri(glbUrl));
if (success) await gltf.InstantiateMainSceneAsync(targetTransform);

CRITICAL FOR APK: Add glTFast shaders to Graphics Settings → Always Included Shaders or materials break in build.

---

## The Five C# Systems

### ARIAOrchestrator.cs
Master controller. Receives voice transcript, captures passthrough frame, serialises MRUK data, calls Claude, runs object queue progressively.

### SemanticPlacementEngine.cs
Constraint table mapping object categories to valid MRUK surface types:
- Painting, shelf, clock → WALL_FACE
- Rug, plant, chair, table, desk → FLOOR
- Overhead lamp → ceiling anchor or elevated position
Invalid LLM assignments corrected to nearest valid surface.
Uses MRUK.Instance.GetCurrentRoom().FindSpawnPositions() for exact placement.

### ScaleInferenceSystem.cs
On GLB load: compute mesh bounding box, scale factor = LLM height (metres) / bounding box height.
Result: chair=90cm, mug=10cm, bookshelf=180cm. No manual calibration.

### SphericalHarmonicsLightingEstimator.cs — PRIMARY CONTRIBUTION
- Samples Quest passthrough camera texture at 5Hz
- Divides frame into directional bins relative to headset orientation
- Projects pixel luminance + chrominance onto 2-band SH basis (9 coefficients, Ramamoorthi & Hanrahan 2001)
- Updates Unity directional light rotation + colour
- Applies SphericalHarmonicsL2 probe to all generated objects via LightProbeGroup
- Known limitation: view-dependent — reflects lighting at user position not object position. Documented as known approximation.
- Requires APK — passthrough camera unavailable in editor

### ShadowReceiverSetup.cs
Configures MRUK Scene Mesh to receive shadows. Occlusion shader at zero opacity — invisible but active as shadow surface. Shadows fall in SH-estimated light direction.

---

## What Works Where

Feature                          | Editor  | APK
Claude / Gemini / HiTEM3D calls | YES     | YES
GLTFast loading                  | YES     | YES
MRUK real room data              | NO mock | YES
Passthrough camera               | NO      | YES
SH lighting estimation           | NO      | YES
Voice input                      | PARTIAL | YES

Dev strategy: build all API + placement logic in editor with hardcoded mock MRUK data first. APK only for passthrough, SH lighting, real room testing.

---

## HTTP Rules for Unity

- Always UnityWebRequest — never HttpClient (not supported on Quest)
- Always async/await — never block main thread
- Always read keys from config.json at runtime
- For HiTEM3D multipart: use List<IMultipartFormSection>

---

## Known Limitations (For Report)

1. SH lighting is view-dependent — object gets user's lighting zone not its own
2. 60-90s generation latency per object — mitigated by progressive placement and staged generation
3. LLM reasons from text description of room — unusual geometries may need constraint engine correction

---

## Prior Work Being Improved

Say It, See It (IEEE VR 2025): voice → single object at gaze point. No room awareness, no surface anchoring, no lighting, no scale, no post-spawn interaction. ARIA improves every limitation.

---

## Claude Code Rules

1. Read this file fully before starting any session
2. Read the specific .cs file being worked on
3. Use UnityWebRequest for all HTTP — never HttpClient
4. Use async/await throughout — never block main thread
5. Load all keys from config.json — never hardcode
6. Remind about APK requirement when touching passthrough/MRUK/SH code
7. Commit messages: feat: / fix: / refactor: prefix
8. Never commit config.json
