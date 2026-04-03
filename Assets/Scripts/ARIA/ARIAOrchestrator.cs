// ARIAOrchestrator.cs
// Master controller for the ARIA pipeline.
// Voice transcript → Claude (multimodal) → Gemini → HiTEM3D → GLTFast → place + scale + light.
// Objects appear progressively — each placed as soon as its own generation finishes.
//
// APK NOTE: WebCamTexture passthrough capture and real MRUK data require a Quest APK build.
//           Editor runs with mock MRUK data and a null passthrough image (text-only Claude call).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GLTFast;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

public class ARIAOrchestrator : MonoBehaviour
{
    [Header("ARIA Systems")]
    [SerializeField] private SemanticPlacementEngine placementEngine;
    [SerializeField] private ScaleInferenceSystem    scaleSystem;
    [SerializeField] private SphericalHarmonicsLightingEstimator shEstimator;
    [SerializeField] private ShadowReceiverSetup     shadowReceiver;

    [Header("Scene")]
    [Tooltip("Parent transform for all spawned objects. Defaults to this GameObject.")]
    [SerializeField] private Transform spawnRoot;
    [Tooltip("The scene's main directional light. SH estimator will control its direction.")]
    [SerializeField] private Light sceneDirectionalLight;

    // API keys — loaded from config.json at runtime, never hardcoded
    private string _claudeKey;
    private string _geminiKey;
    private string _hitemAccessKey;
    private string _hitemSecretKey;

    // Passthrough camera for frame capture (WebCamTexture, Quest 3/3S only)
    private WebCamTexture _webcam;

    // Tracks grey-mesh previews so they can be swapped when the textured mesh arrives
    private readonly Dictionary<string, GameObject> _previews = new();

    // GLB cache: category → textured GLB URL — persists between play sessions so we
    // skip Gemini + HiTEM3D for categories we've already generated (saves credits)
    private Dictionary<string, string> _glbCache = new();
    private static string GlbCachePath =>
#if UNITY_EDITOR
        Path.GetFullPath(Path.Combine(Application.dataPath, "aria_glb_cache.json"));
#else
        Path.Combine(Application.persistentDataPath, "aria_glb_cache.json");
#endif

    // Status callback for ARIADebugUI
    public event Action<string> OnStatusChanged;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        LoadConfig();
        LoadGlbCache();
    }

    private void Start()
    {
        // Initialise shadow receiver (configures EffectMesh materials) once scene is ready
        shadowReceiver?.Configure();

        // Subscribe to MRUK scene loaded event so we know when room data is available
#if UNITY_ANDROID && !UNITY_EDITOR
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.AddListener(OnMRUKSceneLoaded);
#endif
    }

    private void OnDestroy()
    {
        if (_webcam != null && _webcam.isPlaying)
            _webcam.Stop();

#if UNITY_ANDROID && !UNITY_EDITOR
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.RemoveListener(OnMRUKSceneLoaded);
#endif
    }

    private void OnMRUKSceneLoaded()
    {
        Debug.Log("[ARIA] MRUK scene loaded — real room data available.");
    }

    // -------------------------------------------------------------------------
    // Config
    // -------------------------------------------------------------------------

    private void LoadConfig()
    {
        string path = GetConfigPath();
        if (!File.Exists(path))
        {
            Debug.LogError($"[ARIA] config.json not found at: {path}");
            return;
        }

        var cfg = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
        _claudeKey      = cfg.GetValueOrDefault("claude_key",       "");
        _geminiKey      = cfg.GetValueOrDefault("gemini_key",       "");
        _hitemAccessKey = cfg.GetValueOrDefault("hitem_access_key", "");
        _hitemSecretKey = cfg.GetValueOrDefault("hitem_secret_key", "");
        Debug.Log("[ARIA] Config loaded.");
    }

    private void LoadGlbCache()
    {
        try
        {
            if (File.Exists(GlbCachePath))
            {
                _glbCache = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(GlbCachePath)) ?? new();
                Debug.Log($"[ARIA] GLB cache loaded — {_glbCache.Count} cached model(s): [{string.Join(", ", _glbCache.Keys)}]");
            }
            else
            {
                Debug.Log("[ARIA] No GLB cache found — will build one as models generate.");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ARIA] GLB cache load failed: {e.Message}");
            _glbCache = new();
        }
    }

    private void SaveGlbCache()
    {
        try { File.WriteAllText(GlbCachePath, JsonConvert.SerializeObject(_glbCache, Formatting.Indented)); }
        catch (Exception e) { Debug.LogWarning($"[ARIA] GLB cache save failed: {e.Message}"); }
    }

    private static string GetConfigPath()
    {
#if UNITY_EDITOR
        return Path.GetFullPath(Path.Combine(Application.dataPath, "../Assets/config.json"));
#else
        // Quest: push config.json with adb before first run:
        // adb push config.json /sdcard/Android/data/<package-name>/files/config.json
        return Path.Combine(Application.persistentDataPath, "config.json");
#endif
    }

    // -------------------------------------------------------------------------
    // Entry point — called by Voice SDK or ARIADebugUI
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pass the recognised voice transcript here to trigger the full ARIA pipeline.
    /// Called by VoiceSDKConnector (subscribes to AppVoiceExperience.VoiceEvents.OnFullTranscription)
    /// or directly from ARIADebugUI in editor.
    /// </summary>
    public async void ProcessVoiceCommand(string transcript)
    {
        Debug.Log($"[ARIA] Voice command: \"{transcript}\"");
        SetStatus("Capturing room...");

        // Capture passthrough frame ONCE — reused for Claude API and SH estimator
        byte[] jpeg = await CapturePassthroughFrameAsync();

        // Serialize MRUK room data
        string mrukJson = SerializeMRUKData();

        SetStatus("Asking Claude...");
        List<PlacementInstruction> instructions = await CallClaudeAsync(jpeg, mrukJson, transcript);

        if (instructions == null || instructions.Count == 0)
        {
            Debug.LogWarning("[ARIA] Claude returned no placement instructions.");
            SetStatus("No objects to place.");
            return;
        }

        Debug.Log($"[ARIA] Spawning {instructions.Count} object(s) progressively...");
        SetStatus($"Generating {instructions.Count} object(s)...");

        // Launch all object pipelines concurrently — progressive placement
        var tasks = new List<Task>();
        foreach (var instr in instructions)
            tasks.Add(ProcessObjectAsync(instr, jpeg));

        await Task.WhenAll(tasks);
        SetStatus("Done.");
        Debug.Log("[ARIA] All objects placed.");
    }

    // -------------------------------------------------------------------------
    // Passthrough frame capture (WebCamTexture, Quest 3 only)
    // -------------------------------------------------------------------------

    private async Task<byte[]> CapturePassthroughFrameAsync()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_webcam == null)
        {
            _webcam = new WebCamTexture();
            _webcam.Play();
        }

        // Wait 2 frames for first valid frame (first frame is often black)
        await Task.Yield();
        await Task.Yield();

        if (!_webcam.isPlaying || _webcam.width < 2)
        {
            Debug.LogWarning("[ARIA] WebCamTexture not ready — Claude will run text-only.");
            return null;
        }

        var snap = new Texture2D(_webcam.width, _webcam.height, TextureFormat.RGB24, false);
        snap.SetPixels(_webcam.GetPixels());
        snap.Apply();
        byte[] jpeg = snap.EncodeToJPG(75);
        Destroy(snap);
        return jpeg;
#else
        await Task.Yield();
        return null; // Editor: Claude operates on text + mock MRUK only
#endif
    }

    // -------------------------------------------------------------------------
    // MRUK serialisation
    // -------------------------------------------------------------------------

    private string SerializeMRUKData()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            var room = MRUK.Instance?.GetCurrentRoom();
            if (room == null)
            {
                Debug.LogWarning("[ARIA] MRUK room not ready — using mock data.");
                return MockMRUKJson();
            }

            var surfaces = new List<object>();
            foreach (var anchor in room.Anchors)
            {
                var t = anchor.transform;
                surfaces.Add(new
                {
                    label    = anchor.Label.ToString(),
                    position = new { x = t.position.x,   y = t.position.y,   z = t.position.z   },
                    normal   = new { x = t.forward.x,    y = t.forward.y,    z = t.forward.z    },
                    scale    = new { x = t.localScale.x, y = t.localScale.y }
                });
            }
            return JsonConvert.SerializeObject(new { surfaces });
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ARIA] MRUK serialisation error: {e.Message} — using mock data.");
            return MockMRUKJson();
        }
#else
        Debug.Log("[ARIA] Editor mode — using mock MRUK data.");
        return MockMRUKJson();
#endif
    }

    private static string MockMRUKJson() => JsonConvert.SerializeObject(new
    {
        room_width  = 5.0f,
        room_depth  = 4.0f,
        room_height = 2.8f,
        anchors = new[]
        {
            new { label = "FLOOR",     position = new[] { 0f,   -0.05f, 0f   }, normal = new[] { 0f,  1f,  0f }, size = new[] { 5f,   4f   }, height = 0f    },
            new { label = "CEILING",   position = new[] { 0f,    2.8f,  0f   }, normal = new[] { 0f, -1f,  0f }, size = new[] { 5f,   4f   }, height = 0f    },
            new { label = "WALL_FACE", position = new[] { 2.5f,  1.4f,  0f   }, normal = new[] {-1f,  0f,  0f }, size = new[] { 4f, 2.8f   }, height = 0f    },
            new { label = "WALL_FACE", position = new[] {-2.5f,  1.4f,  0f   }, normal = new[] { 1f,  0f,  0f }, size = new[] { 4f, 2.8f   }, height = 0f    },
            new { label = "WALL_FACE", position = new[] { 0f,    1.4f,  2f   }, normal = new[] { 0f,  0f, -1f }, size = new[] { 5f, 2.8f   }, height = 0f    },
            new { label = "WALL_FACE", position = new[] { 0f,    1.4f, -2f   }, normal = new[] { 0f,  0f,  1f }, size = new[] { 5f, 2.8f   }, height = 0f    },
            new { label = "TABLE",     position = new[] { 1f,    0.75f, -0.5f }, normal = new[] { 0f,  1f,  0f }, size = new[] { 1.2f, 0.8f }, height = 0.75f }
        }
    });

    // -------------------------------------------------------------------------
    // Claude API
    // -------------------------------------------------------------------------

    private async Task<List<PlacementInstruction>> CallClaudeAsync(
        byte[] jpeg, string mrukJson, string voiceCommand)
    {
        var content = new List<object>();

        if (jpeg != null)
        {
            content.Add(new
            {
                type   = "image",
                source = new { type = "base64", media_type = "image/jpeg", data = Convert.ToBase64String(jpeg) }
            });
        }

        content.Add(new
        {
            type = "text",
            text = $"Room layout:\n{mrukJson}\n\nUser command: {voiceCommand}"
        });

        string body = JsonConvert.SerializeObject(new
        {
            model      = "claude-sonnet-4-6",
            max_tokens = 2048,
            system     = "You are a spatial interior design AI. Respond with valid JSON only. No explanation. " +
                         "Return ONLY placeable 3D furniture/objects (NOT floors, walls, ceilings, rugs, curtains, or room surfaces). " +
                         "Maximum 4 objects. Each object must be a distinct piece of furniture or decor. " +
                         "Return a JSON array where each element has: " +
                         "prompt (string — description for image generation), " +
                         "surface_label (string: FLOOR/WALL_FACE/CEILING/TABLE), " +
                         "height_metres (float — real-world height), " +
                         "category (string — object type, lowercase), " +
                         "emits_light (bool — true if this object has a light source), " +
                         "light_type (string: 'point' or 'spot', only if emits_light), " +
                         "light_color (array of 3 floats 0-1 RGB, only if emits_light), " +
                         "light_intensity (float, only if emits_light), " +
                         "light_range (float metres, only if emits_light), " +
                         "light_offset (array of 3 floats — bulb position relative to object root, only if emits_light).",
            messages   = new[] { new { role = "user", content } }
        });

        using var req = new UnityWebRequest("https://api.anthropic.com/v1/messages", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout         = 30; // 30s — Claude rarely takes more than 10s
        req.SetRequestHeader("x-api-key",         _claudeKey);
        req.SetRequestHeader("anthropic-version", "2023-06-01");
        req.SetRequestHeader("content-type",      "application/json");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] Claude error: {req.error}\n{req.downloadHandler.text}");
            return null;
        }

        var    resp = JsonConvert.DeserializeObject<ClaudeResponse>(req.downloadHandler.text);
        string json = StripCodeFences(resp.content[0].text);
        return JsonConvert.DeserializeObject<List<PlacementInstruction>>(json);
    }

    // -------------------------------------------------------------------------
    // Per-object pipeline
    // -------------------------------------------------------------------------

    private async Task ProcessObjectAsync(PlacementInstruction instr, byte[] jpeg)
    {
        string name = instr.category ?? instr.prompt;
        var pipelineStart = DateTime.UtcNow;
        Debug.Log($"[ARIA] ═══ STARTING: {name} ═══");

        // ── Cache hit: skip Gemini + HiTEM3D entirely ──────────────────────────
        string cacheKey = name.ToLower().Trim();
        if (_glbCache.TryGetValue(cacheKey, out string cachedUrl))
        {
            Debug.Log($"[ARIA] ✔ CACHE HIT for \"{name}\" — skipping Gemini + HiTEM3D, using cached GLB.");
            SetStatus($"Spawning from cache: {name}");
            await SpawnObjectAsync(cachedUrl, instr, isPreview: false, jpeg: jpeg);
            Debug.Log($"[ARIA] ═══ COMPLETE (cached): {name} ═══");
            return;
        }

        // ── Full pipeline ───────────────────────────────────────────────────────
        SetStatus($"[1/3] Generating image: {name}...");

        // 1 — Gemini image generation
        byte[] png = await CallGeminiAsync(instr.prompt);
        if (png == null) { Debug.LogError($"[ARIA] ✖ Gemini failed for: {name}"); return; }
        Debug.Log($"[ARIA] ✔ [1/3] Image generated for: {name}");

        // 2 — HiTEM3D auth
        SetStatus($"[2/3] Authenticating 3D API: {name}...");
        string token = await GetHiTEMTokenAsync();
        if (token == null) { Debug.LogError($"[ARIA] ✖ HiTEM3D auth failed for: {name}"); return; }
        Debug.Log($"[ARIA] ✔ [2/3] 3D API authenticated");

        // 3 — All-in-one generation (requestType=3: geometry + texture in one call)
        //     Uses 512 resolution for cheapest/fastest testing. Upgrade later.
        SetStatus($"[3/3] Generating 3D model: {name} (~3-5 min, this is normal — do not stop!)");
        Debug.Log($"[ARIA] ⏳ [3/3] Submitting to HiTEM3D (all-in-one, 512) for {name}. DO NOT STOP.");
        string taskId = await SubmitHiTEMTaskAsync(token, png, requestType: 3);
        string glbUrl = await PollHiTEMTaskAsync(token, taskId, name, stage: "all-in-one");
        if (glbUrl == null)
        {
            Debug.LogError($"[ARIA] ✖ HiTEM3D generation failed/timed out for: {name}");
            return;
        }

        Debug.Log($"[ARIA] ✔ [3/3] 3D model ready for: {name} — spawning...");
        await SpawnObjectAsync(glbUrl, instr, isPreview: false, jpeg: jpeg);
        _glbCache[cacheKey] = glbUrl;
        SaveGlbCache();
        Debug.Log($"[ARIA] ✔ Cached GLB for \"{name}\" — future runs will be instant.");

        int totalSec = (int)(DateTime.UtcNow - pipelineStart).TotalSeconds;
        Debug.Log($"[ARIA] ═══ COMPLETE: {name} — total time {totalSec}s ═══");
    }

    // -------------------------------------------------------------------------
    // Gemini image generation
    // -------------------------------------------------------------------------

    private async Task<byte[]> CallGeminiAsync(string objectPrompt)
    {
        string prompt = $"{objectPrompt}, single object, white background, studio lighting, photorealistic";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            contents         = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseModalities = new[] { "IMAGE" }, imageConfig = new { aspectRatio = "1:1" } }
        }));

        // Stable model first, preview as fallback
        string[] models = { "gemini-2.5-flash-image", "gemini-3.1-flash-image-preview" };

        foreach (string model in models)
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_geminiKey}";
            Debug.Log($"[ARIA] Calling Gemini model: {model}");

            // Retry up to 2 times on 429
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = new UnityWebRequest(url, "POST");
                req.uploadHandler   = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout         = 60;
                req.SetRequestHeader("content-type", "application/json");

                await AwaitRequest(req.SendWebRequest());

                if (req.responseCode == 429)
                {
                    int wait = (attempt + 1) * 10;
                    Debug.LogWarning($"[ARIA] Gemini 429 on {model}. Waiting {wait}s...");
                    await Task.Delay(wait * 1000);
                    continue;
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[ARIA] Gemini {model} failed ({req.responseCode}): {req.downloadHandler.text}");
                    break; // try next model
                }

                var resp = JsonConvert.DeserializeObject<GeminiResponse>(req.downloadHandler.text);
                if (resp?.candidates == null || resp.candidates.Count == 0)
                {
                    Debug.LogWarning($"[ARIA] Gemini {model} returned no candidates — trying next model.");
                    break;
                }

                foreach (var part in resp.candidates[0].content.parts)
                {
                    if (part.inline_data != null && !string.IsNullOrEmpty(part.inline_data.data))
                    {
                        Debug.Log($"[ARIA] ✔ Gemini image received from {model}");
                        return Convert.FromBase64String(part.inline_data.data);
                    }
                }

                Debug.LogWarning($"[ARIA] Gemini {model} response had no image data — trying next model.");
                break;
            }
        }

        Debug.LogError("[ARIA] All Gemini models failed.");
        return null;
    }

    // -------------------------------------------------------------------------
    // HiTEM3D three-step pipeline: auth → submit → poll
    // -------------------------------------------------------------------------

    private async Task<string> GetHiTEMTokenAsync()
    {
        string creds = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_hitemAccessKey}:{_hitemSecretKey}"));

        using var req = new UnityWebRequest("https://api.hitem3d.ai/open-api/v1/auth/token", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Array.Empty<byte>());
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout         = 15;
        req.SetRequestHeader("Authorization", $"Basic {creds}");
        req.SetRequestHeader("content-type",  "application/json");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] HiTEM3D auth error: {req.error}");
            return null;
        }

        var resp = JsonConvert.DeserializeObject<HiTEMAuthResponse>(req.downloadHandler.text);
        return resp?.data?.accessToken;
    }

    private async Task<string> SubmitHiTEMTaskAsync(
        string token, byte[] png, int requestType, string meshUrl = null)
    {
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("images",       png, "image.png", "image/png"),
            new MultipartFormDataSection("request_type", requestType.ToString()),
            new MultipartFormDataSection("model",        "hitem3dv1.5"),
            new MultipartFormDataSection("format",       "2"),
            new MultipartFormDataSection("resolution",   "512")
        };

        if (meshUrl != null)
            form.Add(new MultipartFormDataSection("mesh_url", meshUrl));

        using var req = UnityWebRequest.Post("https://api.hitem3d.ai/open-api/v1/submit-task", form);
        req.SetRequestHeader("Authorization", $"Bearer {token}");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] HiTEM3D submit error: {req.error}\n{req.downloadHandler.text}");
            return null;
        }

        var resp = JsonConvert.DeserializeObject<HiTEMSubmitResponse>(req.downloadHandler.text);
        return resp?.data?.task_id;
    }

    private async Task<string> PollHiTEMTaskAsync(string token, string taskId, string objectName = "", string stage = "")
    {
        if (taskId == null) return null;

        var    startTime    = DateTime.UtcNow;
        string shortId      = taskId.Length > 8 ? taskId.Substring(0, 8) : taskId;
        int    pollInterval = 5000;  // ms between polls
        int    timeoutSec   = 600;   // 10 min max — HiTEM3D can take 5–8 min under load
        int    lastLoggedSec = -1;   // only log every 15s to avoid spam

        Debug.Log($"[ARIA] HiTEM3D {stage} submitted for \"{objectName}\" [{shortId}...] — pipeline is WORKING, waiting up to {timeoutSec/60} min...");
        SetStatus($"{stage}: {objectName} [{shortId}...]");

        while (true)
        {
            await Task.Delay(pollInterval);

            int elapsedSec = (int)(DateTime.UtcNow - startTime).TotalSeconds;

            if (elapsedSec >= timeoutSec)
            {
                Debug.LogError($"[ARIA] HiTEM3D timed out after {timeoutSec}s [{shortId}...]");
                return null;
            }

            using var req = new UnityWebRequest(
                $"https://api.hitem3d.ai/open-api/v1/query-task?task_id={taskId}", "GET");
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout         = 15;
            req.SetRequestHeader("Authorization", $"Bearer {token}");

            await AwaitRequest(req.SendWebRequest());

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[ARIA] HiTEM3D poll error: {req.error}");
                return null;
            }

            string rawJson = req.downloadHandler.text;
            var    resp     = JsonConvert.DeserializeObject<HiTEMQueryResponse>(rawJson);
            string state    = resp?.data?.state ?? "(no state)";

            // Log every 15 seconds — include raw JSON on first poll for debugging
            if (elapsedSec / 15 != lastLoggedSec / 15)
            {
                if (lastLoggedSec < 0)
                    Debug.Log($"[ARIA] HiTEM3D FIRST POLL raw:\n{rawJson}");
                Debug.Log($"[ARIA] ⏳ HiTEM3D {stage} for \"{objectName}\" — {elapsedSec}s elapsed. State: \"{state}\"");
                SetStatus($"{stage}: {objectName} — {elapsedSec}s elapsed (state: {state})");
                lastLoggedSec = elapsedSec;
            }

            switch (state.ToLower())
            {
                case "success":
                    if (string.IsNullOrEmpty(resp.data.url))
                    {
                        Debug.LogError($"[ARIA] ✖ HiTEM3D state=success but url is empty! Raw:\n{rawJson}");
                        return null;
                    }
                    Debug.Log($"[ARIA] ✔ HiTEM3D {stage} DONE for \"{objectName}\" in {elapsedSec}s! URL: {resp.data.url}");
                    SetStatus($"{stage} done: {objectName}");
                    return resp.data.url;
                case "failed":
                    Debug.LogError($"[ARIA] ✖ HiTEM3D {stage} FAILED for \"{objectName}\" after {elapsedSec}s. Raw:\n{rawJson}");
                    return null;
                // "created", "queueing", "processing" = still working, keep polling
            }
        }
    }

    // -------------------------------------------------------------------------
    // GLTFast spawn + systems integration
    // -------------------------------------------------------------------------

    private async Task SpawnObjectAsync(
        string glbUrl, PlacementInstruction instr, bool isPreview, byte[] jpeg)
    {
        using var req = UnityWebRequest.Get(glbUrl);
        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] GLB download error: {req.error}");
            return;
        }

        var root = new GameObject(instr.prompt);
        root.transform.SetParent(spawnRoot != null ? spawnRoot : transform);

        // GLTFast load — using generic Load() to avoid deprecated LoadGltfBinary()
        var  gltf = new GltfImport();
        bool ok   = await gltf.Load(req.downloadHandler.data);
        if (!ok)
        {
            Debug.LogError($"[ARIA] GLTFast failed: {instr.prompt}");
            Destroy(root);
            return;
        }
        await gltf.InstantiateMainSceneAsync(root.transform);

        // Scale to LLM-inferred real-world height
        scaleSystem?.ApplyScale(root, instr.height_metres, instr.category);

        // Place on the correct MRUK surface
        placementEngine?.Place(root, instr.surface_label);

        // Final object only (not preview): run lighting + interaction setup
        if (!isPreview)
        {
            // SH lighting — runs ONCE using the passthrough frame captured at command start
            // Updates RenderSettings.ambientProbe and sets sceneDirectionalLight direction
            shEstimator?.EstimateLighting(jpeg, sceneDirectionalLight, root);

            // Add reflection probe at object position (updates every frame automatically)
            AddReflectionProbe(root);

            // Virtual light source (e.g. lamp spawns with a Unity PointLight)
            if (instr.emits_light)
                AddVirtualLight(root, instr);

            // Rigidbody + BoxCollider for grabbable interaction
            AddPhysicsAndInteraction(root);
        }

        // Swap preview → final textured mesh
        if (isPreview)
        {
            _previews[instr.prompt] = root;
        }
        else
        {
            if (_previews.TryGetValue(instr.prompt, out var preview))
            {
                Destroy(preview);
                _previews.Remove(instr.prompt);
            }
        }

        Debug.Log($"[ARIA] Spawned \"{instr.prompt}\" (preview={isPreview})");
    }

    // -------------------------------------------------------------------------
    // Virtual light source (for lamps, lanterns, candles, etc.)
    // -------------------------------------------------------------------------

    private static void AddVirtualLight(GameObject root, PlacementInstruction instr)
    {
        var lightGO = new GameObject("VirtualLight");
        lightGO.transform.SetParent(root.transform);

        Vector3 offset = instr.light_offset != null && instr.light_offset.Length == 3
            ? new Vector3(instr.light_offset[0], instr.light_offset[1], instr.light_offset[2])
            : new Vector3(0f, instr.height_metres * 0.9f, 0f); // default: near top of object
        lightGO.transform.localPosition = offset;

        var light       = lightGO.AddComponent<Light>();
        light.type      = instr.light_type == "spot" ? LightType.Spot : LightType.Point;
        light.intensity = instr.light_intensity;
        light.range     = instr.light_range;
        light.shadows   = LightShadows.Soft;
        light.shadowStrength = 0.8f;

        if (instr.light_color != null && instr.light_color.Length == 3)
            light.color = new Color(instr.light_color[0], instr.light_color[1], instr.light_color[2]);
        else
            light.color = new Color(1f, 0.85f, 0.6f); // warm white default

        Debug.Log($"[ARIA] Added virtual {light.type} light to \"{root.name}\".");
    }

    // -------------------------------------------------------------------------
    // Reflection probe (per-object, realtime)
    // -------------------------------------------------------------------------

    private static void AddReflectionProbe(GameObject root)
    {
        var probeGO = new GameObject("ARIA_ReflectionProbe");
        probeGO.transform.position = root.transform.position + Vector3.up * 0.5f;

        var probe        = probeGO.AddComponent<ReflectionProbe>();
        probe.mode       = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
        probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame;
        probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
        probe.resolution = 64;
        probe.size       = new Vector3(4f, 3f, 4f);
        probeGO.transform.SetParent(root.transform);
    }

    // -------------------------------------------------------------------------
    // Physics + Interaction SDK setup (called once on final spawn)
    // -------------------------------------------------------------------------

    private static void AddPhysicsAndInteraction(GameObject root)
    {
        // Rigidbody — non-kinematic so physics apply, no gravity so it floats in place
        var rb           = root.AddComponent<Rigidbody>();
        rb.useGravity    = false;
        rb.isKinematic   = false;
        rb.constraints   = RigidbodyConstraints.FreezeRotation;

        // BoxCollider auto-sized to mesh bounds
        var bounds = CalculateMeshBounds(root);
        var col    = root.AddComponent<BoxCollider>();
        col.center = root.transform.InverseTransformPoint(bounds.center);
        col.size   = bounds.size;

        // HandGrabInteractable is added via the Building Blocks Hand Grab Interaction setup.
        // Once the scene has OVRInteractionComprehensive, add:
        // root.AddComponent<Oculus.Interaction.Grabbable>();
        // root.AddComponent<Oculus.Interaction.HandGrab.HandGrabInteractable>();
        // (Uncomment when Interaction SDK Building Block is confirmed in scene)
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    /// <summary>Bridges UnityWebRequestAsyncOperation to Task so we can await it.</summary>
    private static Task AwaitRequest(UnityWebRequestAsyncOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }

    /// <summary>Strips ```json ... ``` fences that LLMs sometimes add around JSON.
    /// Falls back to finding the first '[' or '{' so any wrapping format is handled.</summary>
    private static string StripCodeFences(string s)
    {
        s = s.Trim();

        // Find the first JSON-start character, skipping any fence/preamble
        int jsonStart = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '[' || s[i] == '{') { jsonStart = i; break; }
        }
        if (jsonStart > 0) s = s.Substring(jsonStart);

        // Trim trailing fence or whitespace after the last JSON-close character
        int jsonEnd = s.LastIndexOfAny(new[] { ']', '}' });
        if (jsonEnd >= 0 && jsonEnd < s.Length - 1)
            s = s.Substring(0, jsonEnd + 1);

        return s.Trim();
    }

    /// <summary>Computes world-space bounding box across all Renderer children.</summary>
    public static Bounds CalculateMeshBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(root.transform.position, Vector3.one * 0.1f);
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    private void SetStatus(string msg)
    {
        OnStatusChanged?.Invoke(msg);
    }

    // -------------------------------------------------------------------------
    // Test helper — spawns directly from a GLB URL, bypassing all APIs
    // -------------------------------------------------------------------------

    /// <summary>
    /// Spawns a GLB directly from a URL with sensible defaults.
    /// Use from ARIADebugUI to test the spawn pipeline without spending any API credits.
    /// </summary>
    public async void TestSpawnFromUrl(string glbUrl, string category = "test", float heightMetres = 0.75f, string surfaceLabel = "FLOOR")
    {
        if (string.IsNullOrWhiteSpace(glbUrl))
        {
            Debug.LogWarning("[ARIA] TestSpawnFromUrl: no URL provided.");
            return;
        }

        var instr = new PlacementInstruction
        {
            prompt        = $"test_{category}",
            surface_label = surfaceLabel,
            height_metres = heightMetres,
            category      = category,
            emits_light   = false
        };

        Debug.Log($"[ARIA] TEST SPAWN — bypassing all APIs. URL: {glbUrl}");
        SetStatus("Test spawn: downloading GLB...");
        await SpawnObjectAsync(glbUrl, instr, isPreview: false, jpeg: null);
        SetStatus("Test spawn complete.");
        Debug.Log("[ARIA] TEST SPAWN complete — check hierarchy for spawned object.");
    }
}

// =============================================================================
// Data models
// =============================================================================

[Serializable]
public class PlacementInstruction
{
    public string   prompt;
    public string   surface_label;
    public float    height_metres;
    public string   category;
    // Virtual light source fields (optional — only set when emits_light = true)
    public bool     emits_light;
    public string   light_type;
    public float[]  light_color;
    public float    light_intensity;
    public float    light_range;
    public float[]  light_offset;
}

// Claude
[Serializable] public class ClaudeResponse   { public List<ClaudeContent>    content;     }
[Serializable] public class ClaudeContent    { public string type; public string text;    }

// Gemini (generateContent response — fields are camelCase in the API)
[Serializable] public class GeminiResponse    { public List<GeminiCandidate> candidates;         }
[Serializable] public class GeminiCandidate   { public GeminiContent content;                    }
[Serializable] public class GeminiContent     { public List<GeminiPart> parts;                   }
[Serializable] public class GeminiPart
{
    public string text;
    [JsonProperty("inlineData")] public GeminiInlineData inline_data;
}
[Serializable] public class GeminiInlineData
{
    [JsonProperty("mimeType")] public string mime_type;
    public string data;
}

// HiTEM3D
[Serializable] public class HiTEMAuthResponse  { public HiTEMAuthData   data; }
[Serializable] public class HiTEMAuthData       { public string accessToken;   }
[Serializable] public class HiTEMSubmitResponse { public HiTEMSubmitData data; }
[Serializable] public class HiTEMSubmitData     { public string task_id;       }
[Serializable] public class HiTEMQueryResponse  { public HiTEMQueryData  data; public string msg; }
[Serializable] public class HiTEMQueryData
{
    public string task_id;
    public string state;      // "created", "queueing", "processing", "success", "failed"
    public string id;
    public string cover_url;
    public string url;        // GLB download URL (valid for 1 hour)
}
