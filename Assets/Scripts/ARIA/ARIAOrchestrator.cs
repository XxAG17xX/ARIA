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
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GLTFast;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

public enum MeshProvider { HiTEM3D, Tripo3D }

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

    [Header("Pipeline Options")]
    [Tooltip("When enabled, Claude sees the Gemini reference image and refines dimensions/style before HiTEM3D. Adds ~10s per object but improves contextual accuracy.")]
    [SerializeField] private bool enableClaudeRefinement = true;

    [Tooltip("Enable lighting effects (SH estimation, virtual lights, reflection probes). " +
             "Disable for demo: show placement/scale first, then toggle on for lighting demo.")]
    [SerializeField] private bool enableLighting = true;

    [Tooltip("Enable physics (Rigidbody + BoxCollider) and interaction setup on spawned objects.")]
    [SerializeField] private bool enablePhysics = true;

    [Tooltip("Enable PBR materials (metallic/roughness maps) on Tripo3D models. " +
             "Costs more credits but shiny/metallic objects look correct.")]
    [SerializeField] private bool enablePBR = false;

    [Header("3D Generation Provider")]
    [Tooltip("Which API to use for 3D mesh generation. Switch to save credits.")]
    [SerializeField] private MeshProvider meshProvider = MeshProvider.Tripo3D;

    // API keys — loaded from StreamingAssets/config.json at runtime
    private string _claudeKey;
    private string _geminiKey;
    private string _hitemAccessKey;
    private string _hitemSecretKey;
    private string _tripoKey;

    // Passthrough camera for frame capture (WebCamTexture, Quest 3/3S only)
    private WebCamTexture _webcam;

    // Last user context — voice command or button label, passed to Claude for adjustment
    private string _lastUserCommand = "";

    /// <summary>Set by VoiceSDKConnector or ARIADebugUI before adjustment calls.</summary>
    public void SetUserContext(string command) { _lastUserCommand = command ?? ""; }

    // Tracks grey-mesh previews so they can be swapped when the textured mesh arrives
    private readonly Dictionary<string, GameObject> _previews = new();

    // GLB cache: category → local file path — saves actual GLB bytes so we never
    // re-download or re-generate. Tripo/HiTEM URLs expire, local files don't.
    private Dictionary<string, string> _glbCache = new();
    private static string GlbCachePath =>
#if UNITY_EDITOR
        Path.GetFullPath(Path.Combine(Application.dataPath, "aria_glb_cache.json"));
#else
        Path.Combine(Application.persistentDataPath, "aria_glb_cache.json");
#endif
    private static string GlbCacheDir
    {
        get
        {
#if UNITY_EDITOR
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "ARIA_GLBCache"));
#else
            string dir = Path.Combine(Application.persistentDataPath, "glb_cache");
#endif
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // Status callback for ARIADebugUI
    public event Action<string> OnStatusChanged;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        StartCoroutine(CopyConfigThenLoad());
#else
        LoadConfig();
#endif
        LoadGlbCache();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private System.Collections.IEnumerator CopyConfigThenLoad()
    {
        string dest = Path.Combine(Application.persistentDataPath, "config.json");
        if (!File.Exists(dest))
        {
            string src = Path.Combine(Application.streamingAssetsPath, "config.json");
            using var req = UnityWebRequest.Get(src);
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
                File.WriteAllText(dest, req.downloadHandler.text);
            else
                Debug.LogError($"[ARIA] Failed to copy config from StreamingAssets: {req.error}");
        }
        LoadConfig();
    }
#endif

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
        _tripoKey       = cfg.GetValueOrDefault("tripo_key",        "");
        Debug.Log($"[ARIA] Config loaded. Mesh provider: {meshProvider}");
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

    private const int MaxCacheEntries = 10;

    private void SaveGlbCache()
    {
        // Evict oldest entries when cache exceeds limit
        while (_glbCache.Count > MaxCacheEntries)
        {
            string oldest = _glbCache.Keys.First();
            _glbCache.Remove(oldest);
            Debug.Log($"[ARIA] Cache full — evicted oldest entry: \"{oldest}\"");
        }
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
        _lastUserCommand = transcript; // save for Claude adjustment context
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
            tasks.Add(ProcessObjectAsync(instr, jpeg, mrukJson));

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
            system     = "You are a spatial interior design AI for mixed reality. Respond with valid JSON only. No explanation.\n\n" +
                         "OBJECT SELECTION RULES:\n" +
                         "- Generate ONLY the objects the user explicitly asked for. Do NOT add extra items.\n" +
                         "- If the user says 'a bed', return exactly 1 object. 'a lamp and a table' = 2 objects.\n" +
                         "- ONLY placeable 3D furniture/objects. NOT floors, walls, ceilings, rugs, curtains.\n" +
                         "- Maximum 4 objects.\n\n" +
                         "CONTEXTUAL REASONING (CRITICAL):\n" +
                         "You receive the room layout (dimensions, surfaces, existing furniture) and optionally a passthrough camera image.\n" +
                         "Use this context to decide:\n" +
                         "- STYLE: Match the room's aesthetic. Modern room → sleek furniture. Traditional → classic. If the image shows wooden floors and warm tones, suggest warm-toned furniture.\n" +
                         "- COLOR/MATERIAL: Complement existing room colors visible in the image. Don't clash.\n" +
                         "- DIMENSIONS: Size objects proportionally to the room. A bed in a 3m-wide room should be narrower than in a 5m room. " +
                         "A figurine on a table should be proportional to that table's dimensions. A bookshelf in a small room should be compact.\n" +
                         "- PLACEMENT: Consider what's already in the room to avoid overlap.\n\n" +
                         "PROMPT FIELD: Write a detailed image-generation prompt describing the object's style, color, material, and proportions. " +
                         "Include context like 'matching the warm wood tones of the room' or 'compact modern design suitable for a small apartment'.\n\n" +
                         "Return a JSON array. Each element:\n" +
                         "- prompt (string — detailed description for image generation, include style/color/material)\n" +
                         "- surface_label (string: FLOOR/WALL_FACE/CEILING/TABLE)\n" +
                         "- height_metres (float — real-world height, contextual to room)\n" +
                         "- width_metres (float — real-world width, proportional to room/surface)\n" +
                         "- depth_metres (float — real-world depth)\n" +
                         "- category (string — object type, lowercase)\n" +
                         "- emits_light (bool)\n" +
                         "- light_type (string: 'point'/'spot', only if emits_light)\n" +
                         "- light_color ([R,G,B] 0-1, only if emits_light)\n" +
                         "- light_intensity (float, only if emits_light)\n" +
                         "- light_range (float metres, only if emits_light)\n" +
                         "- light_offset ([x,y,z] relative to root, only if emits_light)",
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
    // Claude refinement — sees Gemini reference image + original instruction
    // -------------------------------------------------------------------------

    private async Task<PlacementInstruction> CallClaudeRefinementAsync(
        PlacementInstruction original, byte[] referenceImage, string mrukJson)
    {
        var content = new List<object>();

        // Send the Gemini reference image
        content.Add(new
        {
            type   = "image",
            source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(referenceImage) }
        });

        // Detect whether this is a passthrough image (JPEG from camera) or a Gemini reference (PNG)
        bool isPassthrough = referenceImage != null && referenceImage.Length > 2 &&
                             referenceImage[0] == 0xFF && referenceImage[1] == 0xD8; // JPEG magic bytes

        string contextText = isPassthrough
            ? $"Room layout:\n{mrukJson}\n\n" +
              $"Current placement:\n{JsonConvert.SerializeObject(original)}\n\n" +
              "Above is the LIVE passthrough camera image of the real room where this object was just placed. " +
              "The object is a pre-loaded demo model. Using the room context (visible furniture, surfaces, scale of real objects, " +
              "lighting conditions, style), adjust the dimensions so the object fits naturally. " +
              "If the surface_label is TABLE, the object should be miniature/figurine-sized to fit on the table. " +
              "If FLOOR, scale to real-world furniture size. Consider what's visible in the room to avoid overlap. " +
              "Return the FULL updated PlacementInstruction as a single JSON object (not an array)."
            : $"Room layout:\n{mrukJson}\n\n" +
              $"Original placement instruction:\n{JsonConvert.SerializeObject(original)}\n\n" +
              "Above is the AI-generated reference image for this object. " +
              "Review the image and the room layout. Adjust the dimensions (height_metres, width_metres, depth_metres) " +
              "if the reference image suggests different proportions than originally planned. " +
              "Also refine the prompt if the image style doesn't match the room context. " +
              "Return the FULL updated PlacementInstruction as a single JSON object (not an array).";

        content.Add(new { type = "text", text = contextText });

        string body = JsonConvert.SerializeObject(new
        {
            model      = "claude-sonnet-4-6",
            max_tokens = 1024,
            system     = "You are refining a 3D object placement instruction after seeing its AI-generated reference image. " +
                         "Return valid JSON only. A single object (not an array). " +
                         "Keep all original fields. Only adjust dimensions/prompt if the reference image reveals a mismatch " +
                         "with room proportions or style. If everything looks correct, return the original values unchanged.",
            messages   = new[] { new { role = "user", content } }
        });

        using var req = new UnityWebRequest("https://api.anthropic.com/v1/messages", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout         = 20;
        req.SetRequestHeader("x-api-key",         _claudeKey);
        req.SetRequestHeader("anthropic-version", "2023-06-01");
        req.SetRequestHeader("content-type",      "application/json");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            string errBody = req.downloadHandler?.text ?? "(no body)";
            Debug.LogError($"[ARIA] Claude API FAILED: {req.error} | {req.responseCode} | {errBody}");
            SetStatus($"Claude FAILED: {req.error}");
            return original;
        }

        try
        {
            Debug.Log($"[ARIA] Claude response received ({req.downloadHandler.text.Length} chars)");
            var resp = JsonConvert.DeserializeObject<ClaudeResponse>(req.downloadHandler.text);
            string json = StripCodeFences(resp.content[0].text);
            var refined = JsonConvert.DeserializeObject<PlacementInstruction>(json);

            // Log what changed
            bool changed = false;
            if (Mathf.Abs(refined.height_metres - original.height_metres) > 0.01f)
            { Debug.Log($"[ARIA] ✎ Refinement: height {original.height_metres:F2}m → {refined.height_metres:F2}m"); changed = true; }
            if (Mathf.Abs(refined.width_metres - original.width_metres) > 0.01f)
            { Debug.Log($"[ARIA] ✎ Refinement: width {original.width_metres:F2}m → {refined.width_metres:F2}m"); changed = true; }
            if (Mathf.Abs(refined.depth_metres - original.depth_metres) > 0.01f)
            { Debug.Log($"[ARIA] ✎ Refinement: depth {original.depth_metres:F2}m → {refined.depth_metres:F2}m"); changed = true; }
            if (refined.prompt != original.prompt)
            { Debug.Log($"[ARIA] ✎ Refinement: prompt updated"); changed = true; }

            if (!changed) Debug.Log("[ARIA] ✔ Claude refinement: no changes needed.");
            return refined;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ARIA] Claude refinement parse error: {e.Message} — using original.");
            return original;
        }
    }

    // -------------------------------------------------------------------------
    // Claude adjustment — dedicated method for post-spawn visual refinement
    // -------------------------------------------------------------------------

    private async Task<PlacementInstruction> CallClaudeAdjustmentAsync(
        PlacementInstruction original, byte[] passthroughJpeg, string mrukJson,
        Vector3 userPosition, Vector3 gazeDirection)
    {
        var content = new List<object>();

        content.Add(new
        {
            type = "image",
            source = new { type = "base64", media_type = "image/jpeg",
                           data = Convert.ToBase64String(passthroughJpeg) }
        });

        content.Add(new
        {
            type = "text",
            text = $"=== ROOM SCAN DATA (Meta Quest 3 MRUK — real room boundaries) ===\n{mrukJson}\n\n" +
                   $"=== USER STATE ===\n" +
                   $"Position in room: ({userPosition.x:F2}, {userPosition.y:F2}, {userPosition.z:F2}) metres\n" +
                   $"Gaze direction: ({gazeDirection.x:F2}, {gazeDirection.y:F2}, {gazeDirection.z:F2})\n" +
                   $"The IMAGE CENTER is exactly where the user is looking.\n\n" +
                   $"=== VIRTUAL OBJECT TO PLACE ===\n" +
                   $"Object: \"{original.category}\"\n" +
                   $"Current world size: {original.height_metres:F2}m H x {original.width_metres:F2}m W x {original.depth_metres:F2}m D\n" +
                   $"Current position: ({original.light_range:F2}, {original.light_intensity:F2}, 0) (approximate)\n\n" +
                   (string.IsNullOrEmpty(_lastUserCommand) ? "" :
                   $"=== USER VOICE COMMAND / CONTEXT ===\n\"{_lastUserCommand}\"\n" +
                   "Use this to understand the user's INTENT — what they want, where they want it, " +
                   "and any specific instructions about placement or style.\n\n") +
                   "=== OTHER SPAWNED OBJECTS IN SCENE ===\n" + GetSpawnedObjectsSummary() + "\n\n" +
                   "=== YOUR TASK ===\n" +
                   "You are the spatial reasoning AI for ARIA, a mixed reality interior design system on Meta Quest 3.\n" +
                   "The user is wearing a VR headset with passthrough (they see the real room). They spawned a virtual object " +
                   "and now want you to decide the BEST placement and scale based on what you see.\n\n" +
                   "HOW TO USE THE IMAGE:\n" +
                   "- This is a RAW passthrough camera image — you see the REAL room exactly as the user sees it\n" +
                   "- The CENTER of the image is where the user is looking (their gaze target)\n" +
                   "- Look at what SURFACE is at the center: is it a table/desk surface? A wall? The floor?\n" +
                   "- Look at OBJECTS on that surface: keyboard, mouse, phone, bottles, cables — the virtual object must NOT overlap these\n" +
                   "- Look at CLEAR AREAS near the gaze center where the virtual object could fit\n" +
                   "- If the user is looking at a small area (e.g., corner of a desk), the object must be scaled DOWN to fit\n\n" +
                   "HOW TO USE MRUK DATA:\n" +
                   "- The room scan gives you exact dimensions of walls, floor, ceiling, furniture in metres\n" +
                   "- Use these to determine maximum possible size (object can't be bigger than the room)\n" +
                   "- surface_label tells the placement engine WHICH real surface to attach the object to\n" +
                   "- Cross-reference the image (what you SEE) with the MRUK data (exact measurements)\n\n" +
                   "RULES:\n" +
                   "1. surface_label: FLOOR / WALL_FACE / TABLE / BED / COUCH / CEILING\n" +
                   "   - Judge from the IMAGE what surface the user is looking at\n" +
                   "   - TABLE = desk, table, any horizontal elevated surface with objects on it\n" +
                   "   - FLOOR = ground/floor visible in the image\n" +
                   "   - WALL_FACE = vertical wall surface\n\n" +
                   "2. scale_factor: A SINGLE float (0.02 to 2.0) for UNIFORM proportional resize\n" +
                   "   - 1.0 = current size (real-world furniture scale)\n" +
                   "   - 0.1 = miniature/figurine (10% of real size, good for placing on desks)\n" +
                   "   - 0.05 = tiny decorative item\n" +
                   "   - NEVER change height/width/depth independently — ONLY scale_factor\n" +
                   "   - If surface is TABLE: scale_factor should be 0.05–0.2 (miniature to fit on desk)\n" +
                   "   - If surface is FLOOR: scale_factor 0.5–1.0 (real furniture size, fit the room)\n" +
                   "   - If surface is WALL_FACE: scale_factor 0.3–0.8 (painting/art size)\n\n" +
                   "3. AVOID OVERLAP with real objects visible in the image\n" +
                   "   - Don't place on top of keyboards, mice, phones, shoes, bags, bottles\n" +
                   "   - Find the nearest CLEAR area to the gaze center\n\n" +
                   "Return JSON ONLY:\n" +
                   "{\"surface_label\": \"TABLE\", \"scale_factor\": 0.1, \"category\": \"lamp\", " +
                   "\"reasoning\": \"User looking at desk corner near keyboard, scaling to miniature to fit clear area\"}"
        });

        string body = JsonConvert.SerializeObject(new
        {
            model = "claude-sonnet-4-6",
            max_tokens = 256,
            system = "You are the spatial reasoning AI for ARIA, a mixed reality furniture placement system on Meta Quest 3. " +
                     "You receive: (1) a passthrough camera image of the real room, (2) MRUK room scan data with exact dimensions, " +
                     "(3) user position and gaze direction. " +
                     "Your job: decide surface_label and scale_factor for a virtual object. " +
                     "scale_factor is a SINGLE float for UNIFORM proportional scaling — NEVER change individual dimensions. " +
                     "Analyze the image to identify the surface the user is looking at and find a clear area to place the object. " +
                     "Return valid JSON only, no explanation outside the JSON.",
            messages = new[] { new { role = "user", content } }
        });

        using var req = new UnityWebRequest("https://api.anthropic.com/v1/messages", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout = 30;
        req.SetRequestHeader("x-api-key", _claudeKey);
        req.SetRequestHeader("anthropic-version", "2023-06-01");
        req.SetRequestHeader("content-type", "application/json");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            string errBody = req.downloadHandler?.text ?? "(no body)";
            Debug.LogError($"[ARIA] Claude adjustment FAILED: {req.error} | {req.responseCode} | {errBody}");
            SetStatus($"Claude FAILED: {req.responseCode} {req.error}");
            return original;
        }

        try
        {
            Debug.Log($"[ARIA] Claude adjustment response: {req.downloadHandler.text.Substring(0, Mathf.Min(200, req.downloadHandler.text.Length))}...");
            var resp = JsonConvert.DeserializeObject<ClaudeResponse>(req.downloadHandler.text);
            string json = StripCodeFences(resp.content[0].text);
            return JsonConvert.DeserializeObject<PlacementInstruction>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ARIA] Claude adjustment parse error: {e.Message}");
            SetStatus($"Claude parse error: {e.Message}");
            return original;
        }
    }

    // -------------------------------------------------------------------------
    // Per-object pipeline
    // -------------------------------------------------------------------------

    private async Task ProcessObjectAsync(PlacementInstruction instr, byte[] jpeg, string mrukJson)
    {
        string name = instr.category ?? instr.prompt;
        var pipelineStart = DateTime.UtcNow;
        Debug.Log($"[ARIA] ═══ STARTING: {name} ═══");

        // ── Cache hit: load from local file, zero API calls ─────────────────────
        string cacheKey = name.ToLower().Trim();
        if (_glbCache.TryGetValue(cacheKey, out string cachedPath) && File.Exists(cachedPath))
        {
            Debug.Log($"[ARIA] ✔ CACHE HIT for \"{name}\" — loading local GLB: {cachedPath}");
            SetStatus($"Spawning from cache: {name}");
            await SpawnFromLocalGlb(cachedPath, instr, jpeg);
            Debug.Log($"[ARIA] ═══ COMPLETE (cached): {name} ═══");
            return;
        }

        // ── Full pipeline ───────────────────────────────────────────────────────
        SetStatus($"[1/4] Generating image: {name}...");

        // 1 — Gemini image generation
        byte[] png = await CallGeminiAsync(instr.prompt);
        if (png == null) { Debug.LogError($"[ARIA] ✖ Gemini failed for: {name}"); return; }
        Debug.Log($"[ARIA] ✔ [1/4] Image generated for: {name}");

        // 2 — Claude refinement (optional) — sees reference image + room layout
        if (enableClaudeRefinement)
        {
            SetStatus($"[2/4] Claude refining dimensions: {name}...");
            Debug.Log($"[ARIA] ⏳ [2/4] Claude reviewing reference image for \"{name}\"...");
            instr = await CallClaudeRefinementAsync(instr, png, mrukJson);
            Debug.Log($"[ARIA] ✔ [2/4] Refinement complete for: {name} " +
                      $"(h={instr.height_metres:F2} w={instr.width_metres:F2} d={instr.depth_metres:F2})");
        }

        // 3+4 — 3D mesh generation (provider-dependent)
        string glbUrl;
        if (meshProvider == MeshProvider.Tripo3D)
        {
            SetStatus($"[3/4] Uploading to Tripo3D: {name}...");
            string fileToken = await TripoUploadImageAsync(png);
            if (fileToken == null) { Debug.LogError($"[ARIA] ✖ Tripo upload failed for: {name}"); return; }
            Debug.Log($"[ARIA] ✔ [3/4] Image uploaded to Tripo3D");

            SetStatus($"[4/4] Generating 3D model (Tripo3D): {name} (~1-2 min)...");
            Debug.Log($"[ARIA] ⏳ [4/4] Submitting to Tripo3D for {name}.");
            string taskId = await TripoCreateTaskAsync(fileToken);
            glbUrl = await TripoPollTaskAsync(taskId, name);
        }
        else // HiTEM3D
        {
            SetStatus($"[3/4] Authenticating HiTEM3D: {name}...");
            string token = await GetHiTEMTokenAsync();
            if (token == null) { Debug.LogError($"[ARIA] ✖ HiTEM3D auth failed for: {name}"); return; }
            Debug.Log($"[ARIA] ✔ [3/4] HiTEM3D authenticated");

            SetStatus($"[4/4] Generating 3D model (HiTEM3D): {name} (~3-5 min, do not stop!)");
            Debug.Log($"[ARIA] ⏳ [4/4] Submitting to HiTEM3D (all-in-one, 512) for {name}. DO NOT STOP.");
            string taskId = await SubmitHiTEMTaskAsync(token, png, requestType: 3);
            glbUrl = await PollHiTEMTaskAsync(token, taskId, name, stage: "all-in-one");
        }

        if (glbUrl == null)
        {
            Debug.LogError($"[ARIA] ✖ 3D generation failed/timed out for: {name}");
            return;
        }

        Debug.Log($"[ARIA] ✔ [4/4] 3D model ready for: {name} — downloading & caching...");

        // Download GLB bytes and save locally so we never need the URL again
        using var dlReq = UnityWebRequest.Get(glbUrl);
        await AwaitRequest(dlReq.SendWebRequest());
        if (dlReq.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] GLB download error: {dlReq.error}");
            return;
        }
        byte[] glbBytes = dlReq.downloadHandler.data;

        // Save to local cache file
        string localPath = Path.Combine(GlbCacheDir, $"{cacheKey.Replace(' ', '_')}.glb");
        File.WriteAllBytes(localPath, glbBytes);
        _glbCache[cacheKey] = localPath;
        SaveGlbCache();
        Debug.Log($"[ARIA] ✔ Cached GLB locally: {localPath} ({glbBytes.Length / 1024}KB)");

        await SpawnFromGlbBytes(glbBytes, instr, jpeg);

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
    // Tripo3D pipeline: upload image → create task → poll → GLB URL
    // Cheapest settings: no PBR, lowest face count, draft texture
    // Docs: https://platform.tripo3d.ai/docs/generation
    // -------------------------------------------------------------------------

    private async Task<string> TripoUploadImageAsync(byte[] png)
    {
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", png, "image.png", "image/png")
        };

        using var req = UnityWebRequest.Post("https://api.tripo3d.ai/v2/openapi/upload", form);
        req.timeout = 30;
        req.SetRequestHeader("Authorization", $"Bearer {_tripoKey}");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] Tripo upload error: {req.error}\n{req.downloadHandler.text}");
            return null;
        }

        var resp = JsonConvert.DeserializeObject<TripoUploadResponse>(req.downloadHandler.text);
        string token = resp?.data?.image_token;
        Debug.Log($"[ARIA] Tripo upload OK — token: {(token != null ? token.Substring(0, Mathf.Min(20, token.Length)) + "..." : "null")}");
        return token;
    }

    private async Task<string> TripoCreateTaskAsync(string fileToken)
    {
        // Cheapest config: draft quality, low face count, no PBR
        string body = JsonConvert.SerializeObject(new
        {
            type = "image_to_model",
            file = new { type = "png", file_token = fileToken },
            model_version   = "default",
            face_limit      = 10000,       // bare minimum for testing
            texture          = true,        // need texture for visual demo
            pbr              = enablePBR,    // PBR maps for metallic/shiny objects (costs more credits)
            auto_size        = true
        });

        using var req = new UnityWebRequest("https://api.tripo3d.ai/v2/openapi/task", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout         = 30;
        req.SetRequestHeader("Authorization", $"Bearer {_tripoKey}");
        req.SetRequestHeader("Content-Type", "application/json");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] Tripo task submit error: {req.error}\n{req.downloadHandler.text}");
            return null;
        }

        var resp = JsonConvert.DeserializeObject<TripoTaskResponse>(req.downloadHandler.text);
        string taskId = resp?.data?.task_id;
        Debug.Log($"[ARIA] Tripo task created: {taskId}");
        return taskId;
    }

    private async Task<string> TripoPollTaskAsync(string taskId, string objectName = "")
    {
        if (taskId == null) return null;

        var  startTime  = DateTime.UtcNow;
        int  timeoutSec = 300; // 5 min max
        int  lastLog    = -1;

        Debug.Log($"[ARIA] Tripo3D polling for \"{objectName}\" [{taskId}]...");

        while (true)
        {
            await Task.Delay(5000);

            int elapsed = (int)(DateTime.UtcNow - startTime).TotalSeconds;
            if (elapsed >= timeoutSec)
            {
                Debug.LogError($"[ARIA] Tripo3D timed out after {timeoutSec}s for \"{objectName}\"");
                return null;
            }

            using var req = new UnityWebRequest(
                $"https://api.tripo3d.ai/v2/openapi/task/{taskId}", "GET");
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout         = 15;
            req.SetRequestHeader("Authorization", $"Bearer {_tripoKey}");

            await AwaitRequest(req.SendWebRequest());

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[ARIA] Tripo poll error: {req.error}");
                return null;
            }

            string rawJson = req.downloadHandler.text;
            var    resp     = JsonConvert.DeserializeObject<TripoQueryResponse>(rawJson);
            string status   = resp?.data?.status ?? "(no status)";

            // Log every 15s
            if (elapsed / 15 != lastLog / 15)
            {
                if (lastLog < 0)
                    Debug.Log($"[ARIA] Tripo FIRST POLL raw:\n{rawJson}");
                Debug.Log($"[ARIA] ⏳ Tripo3D \"{objectName}\" — {elapsed}s elapsed. Status: \"{status}\"");
                SetStatus($"Tripo3D: {objectName} — {elapsed}s (status: {status})");
                lastLog = elapsed;
            }

            switch (status.ToLower())
            {
                case "success":
                    string glbUrl = resp.data?.output?.model;
                    if (string.IsNullOrEmpty(glbUrl))
                    {
                        Debug.LogError($"[ARIA] ✖ Tripo success but no model URL! Raw:\n{rawJson}");
                        return null;
                    }
                    Debug.Log($"[ARIA] ✔ Tripo3D DONE for \"{objectName}\" in {elapsed}s!");
                    return glbUrl;
                case "failed":
                    Debug.LogError($"[ARIA] ✖ Tripo3D FAILED for \"{objectName}\". Raw:\n{rawJson}");
                    return null;
                // "queued", "running" = still working
            }
        }
    }

    // -------------------------------------------------------------------------
    // GLTFast spawn + systems integration
    // -------------------------------------------------------------------------

    private async Task SpawnFromLocalGlb(string localPath, PlacementInstruction instr, byte[] jpeg)
    {
        byte[] glbBytes = File.ReadAllBytes(localPath);
        await SpawnFromGlbBytes(glbBytes, instr, jpeg);
    }

    private async Task SpawnFromGlbBytes(byte[] glbBytes, PlacementInstruction instr, byte[] jpeg)
    {
        var root = new GameObject(instr.prompt);
        root.transform.SetParent(spawnRoot != null ? spawnRoot : transform);

        var  gltf = new GltfImport();
        bool ok   = await gltf.Load(glbBytes);
        if (!ok)
        {
            Debug.LogError($"[ARIA] GLTFast failed: {instr.prompt}");
            Destroy(root);
            return;
        }
        await gltf.InstantiateMainSceneAsync(root.transform);

        // Fix pink materials — replace any missing/magenta shaders with URP/Lit
        FixPinkMaterials(root);

        // Scale to LLM-inferred real-world dimensions
        scaleSystem?.ApplyScale(root, instr.height_metres, instr.category,
                                instr.width_metres, instr.depth_metres);

        // Place on the correct MRUK surface
        placementEngine?.Place(root, instr.surface_label);

        // Auto-fit: shrink if object clips walls/ceiling/furniture
#if UNITY_ANDROID && !UNITY_EDITOR
        var fitRoom = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();
        if (fitRoom != null)
            placementEngine?.FitToAvailableSpace(root, fitRoom);
#endif

        // Lighting + interaction setup
        if (enableLighting)
        {
            shEstimator?.EstimateLighting(jpeg, sceneDirectionalLight, root);
            shEstimator?.AddPerObjectLight(root); // per-object directional from nearest ceiling light
            AddReflectionProbe(root);
            if (instr.emits_light) AddVirtualLight(root, instr);
            Debug.Log($"[ARIA] Lighting applied to \"{instr.prompt}\"");
        }

        if (enablePhysics)
            AddPhysicsAndInteraction(root);

        if (_previews.TryGetValue(instr.prompt, out var preview))
        {
            Destroy(preview);
            _previews.Remove(instr.prompt);
        }

        Debug.Log($"[ARIA] Spawned \"{instr.prompt}\"");
    }

    /// <summary>
    /// Replaces pink/magenta materials (missing shader) with URP/Lit fallback.
    /// GLTFast shader graphs get stripped from Android builds — this catches them.
    /// </summary>
    private static void FixPinkMaterials(GameObject root)
    {
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        Shader urpSimpleLit = Shader.Find("Universal Render Pipeline/Simple Lit");
        Shader fallback = urpLit != null ? urpLit : urpSimpleLit;

        if (fallback == null)
        {
            Debug.LogWarning("[ARIA] No URP/Lit shader found for material fix.");
            return;
        }

        int fixed_count = 0;
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            var mats = r.materials; // clone array
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || mats[i].shader == null ||
                    mats[i].shader.name == "Hidden/InternalErrorShader" ||
                    mats[i].shader.name.Contains("Error"))
                {
                    // Pink material — replace with URP/Lit keeping texture if possible
                    var newMat = new Material(fallback);
                    if (mats[i] != null)
                    {
                        // Try to preserve the main texture
                        if (mats[i].HasProperty("_MainTex") && mats[i].mainTexture != null)
                            newMat.mainTexture = mats[i].mainTexture;
                        if (mats[i].HasProperty("_BaseMap") && mats[i].GetTexture("_BaseMap") != null)
                            newMat.SetTexture("_BaseMap", mats[i].GetTexture("_BaseMap"));
                        if (mats[i].HasProperty("_Color"))
                            newMat.color = mats[i].color;
                        if (mats[i].HasProperty("_BaseColor"))
                            newMat.SetColor("_BaseColor", mats[i].GetColor("_BaseColor"));
                    }
                    mats[i] = newMat;
                    fixed_count++;
                }
            }
            r.materials = mats;
        }

        if (fixed_count > 0)
            Debug.Log($"[ARIA] Fixed {fixed_count} pink material(s) → URP/Lit");
    }

    // -------------------------------------------------------------------------
    // Demo spawn — load pre-bundled GLBs from StreamingAssets (zero API calls)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Spawns a pre-bundled GLB from StreamingAssets/GLBCache/{filename}.
    /// Zero API calls — for demo/presentation use.
    /// </summary>
    public async void SpawnBundledGlb(string filename, string surfaceLabel, float height,
        string category = null, float width = 0f, float depth = 0f)
    {
        SetStatus($"Loading bundled: {filename}...");
        string path = Path.Combine(Application.streamingAssetsPath, "GLBCache", filename);

        byte[] glbBytes;

#if UNITY_ANDROID && !UNITY_EDITOR
        // On Android, StreamingAssets is inside the APK — need UnityWebRequest
        using var req = UnityWebRequest.Get(path);
        await AwaitRequest(req.SendWebRequest());
        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] Bundled GLB load failed: {req.error}");
            SetStatus("Failed to load bundled model.");
            return;
        }
        glbBytes = req.downloadHandler.data;
#else
        if (!File.Exists(path))
        {
            Debug.LogError($"[ARIA] Bundled GLB not found: {path}");
            SetStatus("Bundled model not found.");
            return;
        }
        glbBytes = File.ReadAllBytes(path);
#endif

        string name = category ?? Path.GetFileNameWithoutExtension(filename);

        // Spawn the GLB first (unscaled, at origin temporarily)
        var root = new GameObject(name);
        root.transform.SetParent(spawnRoot != null ? spawnRoot : transform);
        root.layer = LayerMask.NameToLayer("Ignore Raycast"); // don't interfere with gaze raycast

        var gltf = new GltfImport();
        bool ok = await gltf.Load(glbBytes);
        if (!ok) { Debug.LogError($"[ARIA] GLTFast failed: {name}"); Destroy(root); return; }
        await gltf.InstantiateMainSceneAsync(root.transform);
        FixPinkMaterials(root);

        // Scale to initial real-world dimensions
        scaleSystem?.ApplyScale(root, height, category, width, depth);

        // Place at crosshair hit point — wherever the user was looking
        Camera cam = Camera.main;
        if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 10f))
        {
            // Place at the hit point, offset so object doesn't clip into the surface
            Bounds b = CalculateMeshBounds(root);
            Vector3 pos = hit.point;

            // If hit a horizontal surface (floor/table), lift object so bottom sits on surface
            if (Vector3.Dot(hit.normal, Vector3.up) > 0.5f)
                pos.y += b.extents.y;
            // If hit a vertical surface (wall), offset from wall
            else
                pos += hit.normal * (Mathf.Min(b.extents.x, b.extents.z) + 0.05f);

            root.transform.position = pos;

            // Face the user
            Vector3 toUser = cam.transform.position - pos;
            toUser.y = 0;
            if (toUser.sqrMagnitude > 0.01f)
                root.transform.rotation = Quaternion.LookRotation(toUser.normalized);

            Debug.Log($"[ARIA] Spawned {name} at crosshair hit: {pos} (surface normal: {hit.normal})");
        }
        else
        {
            // No hit — place 1.5m in front of user on floor
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
            fwd.y = 0; fwd.Normalize();
            Bounds b = CalculateMeshBounds(root);
            root.transform.position = cam.transform.position + fwd * 1.5f + Vector3.up * b.extents.y;
            Debug.Log($"[ARIA] Spawned {name} at fallback (no raycast hit)");
        }

        // Auto-fit to available MRUK space (won't hit other spawned objects due to layer)
#if UNITY_ANDROID && !UNITY_EDITOR
        var fitRoom = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();
        if (fitRoom != null)
            placementEngine?.FitToAvailableSpace(root, fitRoom);
#endif

        // Reset layer so future raycasts CAN hit this object
        root.layer = 0;

        // Physics
        if (enablePhysics) AddPhysicsAndInteraction(root);

        // Reflection probe
        AddReflectionProbe(root);

        SetStatus($"Spawned: {name}. Look at target, tap 'Adjust with Claude'.");
    }

    // -------------------------------------------------------------------------
    // User-triggered Claude adjustment — look at target, then press button
    // -------------------------------------------------------------------------

    /// <summary>
    /// Captures passthrough from current gaze, sends to Claude with MRUK data,
    /// and adjusts the most recently spawned object's scale/position.
    /// Called by "Adjust with Claude" button AFTER user positions their gaze.
    /// </summary>
    public async void AdjustLastSpawnWithClaude()
    {
        Transform root = spawnRoot != null ? spawnRoot : transform;
        if (root.childCount == 0)
        {
            SetStatus("Nothing to adjust — spawn something first.");
            return;
        }

        if (string.IsNullOrEmpty(_claudeKey))
        {
            SetStatus($"Claude API key missing! Key length: {(_claudeKey ?? "null").Length}");
            Debug.LogError($"[ARIA] Claude key is empty/null. Key: '{_claudeKey}'");
            return;
        }

        // Get the most recently spawned object
        Transform lastSpawn = root.GetChild(root.childCount - 1);
        string objName = lastSpawn.name;

        SetStatus($"Capturing view for Claude...");
        byte[] jpeg = await CapturePassthroughFrameAsync();
        if (jpeg == null)
        {
            SetStatus("Passthrough capture failed — camera not ready.");
            Debug.LogError("[ARIA] CapturePassthroughFrameAsync returned null");
            return;
        }

        SetStatus($"Claude analyzing {objName} ({jpeg.Length/1024}KB image)...");
        Debug.Log($"[ARIA] Sending to Claude: {objName}, image={jpeg.Length/1024}KB, key={_claudeKey.Substring(0,10)}...");

        string mrukJson = SerializeMRUKData();

        // Get actual world-space bounds of the spawned object
        Bounds objBounds = CalculateMeshBounds(lastSpawn.gameObject);
        Camera cam = Camera.main;
        Vector3 gazePos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 gazeFwd = cam != null ? cam.transform.forward : Vector3.forward;

        Vector3 objPos = lastSpawn.position;
        var instr = new PlacementInstruction
        {
            prompt = objName,
            category = objName,
            surface_label = "AUTO",
            height_metres = objBounds.size.y,
            width_metres = objBounds.size.x,
            depth_metres = objBounds.size.z,
            // Repurpose light fields to pass object position to prompt
            light_range = objPos.x,
            light_intensity = objPos.y
        };

        // Send passthrough image with rich context
        var refined = await CallClaudeAdjustmentAsync(instr, jpeg, mrukJson, gazePos, gazeFwd);

        // Claude returns scale_factor (uniform) + surface_label + reasoning
        float scaleFactor = refined.scale_factor > 0.01f ? refined.scale_factor : 1f;
        string surface = refined.surface_label ?? "FLOOR";
        string reasoning = refined.reasoning ?? "";

        // Apply uniform scale (proportional, no deformation)
        if (scaleFactor > 0.01f && Mathf.Abs(scaleFactor - 1f) > 0.05f)
        {
            lastSpawn.localScale *= scaleFactor;
            Debug.Log($"[ARIA] Claude scale: {scaleFactor:F2}x (uniform)");
        }

        // Re-place on the surface Claude decided
        placementEngine?.Place(lastSpawn.gameObject, surface);

#if UNITY_ANDROID && !UNITY_EDITOR
        var fitRoom = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();
        if (fitRoom != null)
            placementEngine?.FitToAvailableSpace(lastSpawn.gameObject, fitRoom);
#endif

        SetStatus($"Claude: {objName} → {surface}, scale {scaleFactor:F2}x. {reasoning}");
        Debug.Log($"[ARIA] Claude adjustment: surface={surface}, scale={scaleFactor:F2}x, reason={reasoning}");
    }

    // -------------------------------------------------------------------------
    // Room lighting scan — 4-photo 360° capture for accurate SH estimation
    // -------------------------------------------------------------------------

    private byte[][] _roomScanPhotos;
    private float[]  _roomScanHeadings;
    private int      _roomScanCount;
    public bool HasRoomScan => _roomScanCount >= 4;

    /// <summary>Initialise storage for a new 4-photo room scan.</summary>
    public void BeginRoomLightingScan()
    {
        _roomScanPhotos = new byte[4][];
        _roomScanHeadings = new float[4];
        _roomScanCount = 0;
        Debug.Log("[ARIA] Room lighting scan started.");
    }

    /// <summary>Capture and store one photo for the room scan.</summary>
    public async Task<bool> CaptureRoomScanPhoto(int phase)
    {
        byte[] jpeg = await CapturePassthroughFrameAsync();
        if (jpeg == null || jpeg.Length == 0)
        {
            Debug.LogWarning($"[ARIA] Room scan photo {phase} capture failed.");
            return false;
        }

        Camera cam = Camera.main;
        float heading = cam != null ? cam.transform.eulerAngles.y : phase * 90f;

        _roomScanPhotos[phase] = jpeg;
        _roomScanHeadings[phase] = heading;
        _roomScanCount = phase + 1;

        Debug.Log($"[ARIA] Room scan photo {phase} captured (heading: {heading:F1}°, {jpeg.Length / 1024}KB)");
        return true;
    }

    // -------------------------------------------------------------------------
    // Retroactive lighting — call from debug UI to apply lighting to all spawned objects
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies lighting (SH, reflection probes) to all children of spawnRoot.
    /// Called from ARIADebugUI when "Enable Lighting" is toggled on mid-demo.
    /// </summary>
    public async void ApplyLightingToAllSpawned()
    {
        Transform root = spawnRoot != null ? spawnRoot : transform;
        int count = 0;

        // Use room scan data if available (4-photo 360°), otherwise single frame
        if (HasRoomScan && shEstimator != null)
        {
            shEstimator.EstimateFromRoomScan(_roomScanPhotos, _roomScanHeadings,
                                              sceneDirectionalLight);
            Debug.Log("[ARIA] Applied 4-photo room scan lighting.");
        }
        else
        {
            byte[] jpeg = await CapturePassthroughFrameAsync();
            bool firstChild = true;
            foreach (Transform child in root)
            {
                if (firstChild)
                {
                    shEstimator?.EstimateLighting(jpeg, sceneDirectionalLight, child.gameObject);
                    firstChild = false;
                }
            }
            Debug.Log("[ARIA] Applied single-frame lighting (no room scan).");
        }

        foreach (Transform child in root)
        {
            if (child.Find("ARIA_ObjectLight") == null)
                shEstimator?.AddPerObjectLight(child.gameObject);
            if (child.Find("ARIA_ReflectionProbe") == null)
                AddReflectionProbe(child.gameObject);
            count++;
        }
        string summary = shEstimator != null ? shEstimator.GetLightingSummary() : "";
        Debug.Log($"[ARIA] Applied lighting to {count} spawned object(s). {summary}");
        SetStatus($"Lighting: {count} obj(s). {summary}");
    }

    /// <summary>
    /// Toggles between ARIA lighting and default Unity lighting for demo comparison.
    /// </summary>
    public bool ToggleLightingComparison()
    {
        if (shEstimator == null) return true;
        bool ariaActive = shEstimator.ToggleComparison(sceneDirectionalLight);
        SetStatus(ariaActive ? "ARIA lighting ON" : "Default lighting (no ARIA)");
        return ariaActive;
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
        // Rigidbody — kinematic so objects stay exactly where placed (no drift/wobble)
        var rb           = root.AddComponent<Rigidbody>();
        rb.useGravity    = false;
        rb.isKinematic   = true;

        // BoxCollider auto-sized to mesh bounds (convert world→local space)
        var bounds = CalculateMeshBounds(root);
        var col    = root.AddComponent<BoxCollider>();
        col.center = root.transform.InverseTransformPoint(bounds.center);
        var ls = root.transform.localScale;
        col.size = new Vector3(
            ls.x > 0.0001f ? bounds.size.x / ls.x : bounds.size.x,
            ls.y > 0.0001f ? bounds.size.y / ls.y : bounds.size.y,
            ls.z > 0.0001f ? bounds.size.z / ls.z : bounds.size.z);

        // Hand grab — requires Interaction SDK Building Block in scene (OVRInteractionComprehensive)
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            root.AddComponent(System.Type.GetType("Oculus.Interaction.Grabbable, Oculus.Interaction.Runtime"));
            root.AddComponent(System.Type.GetType("Oculus.Interaction.HandGrab.HandGrabInteractable, Oculus.Interaction.Runtime"));
            Debug.Log($"[ARIA] Hand grab enabled on {root.name}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[ARIA] Hand grab setup skipped: {e.Message}");
        }
#endif
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

    private string GetSpawnedObjectsSummary()
    {
        Transform root = spawnRoot != null ? spawnRoot : transform;
        if (root.childCount == 0) return "None — this is the first object.";

        var sb = new StringBuilder();
        foreach (Transform child in root)
        {
            Bounds b = CalculateMeshBounds(child.gameObject);
            sb.AppendLine($"- \"{child.name}\" at ({child.position.x:F2}, {child.position.y:F2}, {child.position.z:F2}), " +
                          $"size: {b.size.x:F2}x{b.size.y:F2}x{b.size.z:F2}m");
        }
        return sb.ToString();
    }

    private void SetStatus(string msg)
    {
        OnStatusChanged?.Invoke(msg);
    }

    // -------------------------------------------------------------------------
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
    public float    width_metres;
    public float    depth_metres;
    public string   category;
    public float    scale_factor;    // uniform scale (0.05–2.0), used by Claude adjustment
    public string   reasoning;      // Claude's explanation of its decision
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

// Tripo3D
[Serializable] public class TripoUploadResponse { public TripoUploadData data; }
[Serializable] public class TripoUploadData     { public string image_token;   }
[Serializable] public class TripoTaskResponse   { public TripoTaskData   data; public int code; }
[Serializable] public class TripoTaskData       { public string task_id;       }
[Serializable] public class TripoQueryResponse  { public TripoQueryData  data; public int code; }
[Serializable] public class TripoQueryData
{
    public string task_id;
    public string status;     // "queued", "running", "success", "failed"
    public TripoOutput output;
}
[Serializable] public class TripoOutput
{
    public string model;      // GLB download URL
    public string pbr_model;  // PBR variant (we don't use this)
}
