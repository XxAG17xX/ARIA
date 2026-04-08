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
public enum ShadowMode  { Directional, PointLight }

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

    // API keys — set locally, stripped for git push
    private string _claudeKey  = "";
    private string _geminiKey  = "";
    private string _hitemAccessKey = "";
    private string _hitemSecretKey = "";
    private string _tripoKey   = "";

    // Passthrough camera for frame capture (WebCamTexture, Quest 3/3S only)
    private WebCamTexture _webcam;

    // Last user context — voice command or button label, passed to Claude for adjustment
    private string _lastUserCommand = "";

    /// <summary>Set by VoiceSDKConnector or ARIADebugUI before adjustment calls.</summary>
    public void SetUserContext(string command) { _lastUserCommand = command ?? ""; }

    // Anchor registry: maps anchor IDs (e.g. "WALL_0") to MRUK anchors for specific placement
    private Dictionary<string, Meta.XR.MRUtilityKit.MRUKAnchor> _anchorRegistry = new();

    /// <summary>Look up a specific MRUK anchor by ID (e.g. "WALL_2"). Returns null if not found.</summary>
    public Meta.XR.MRUtilityKit.MRUKAnchor GetAnchorById(string anchorId)
        => !string.IsNullOrEmpty(anchorId) && _anchorRegistry.TryGetValue(anchorId, out var a) ? a : null;

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
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.AddListener(OnMRUKSceneLoaded);

        // Enable environment depth occlusion so virtual objects hide behind real geometry
        EnableOcclusion();
    }

    private void EnableOcclusion()
    {
        // Try the template OcclusionManager first
        var occMgr = FindFirstObjectByType<UnityEngine.XR.Templates.MR.OcclusionManager>();
        if (occMgr != null)
        {
            occMgr.enableManagerOnStart = true;
            occMgr.SetupManager();
            Debug.Log("[ARIA] Occlusion enabled via OcclusionManager.");
            return;
        }

        // Fallback: enable AROcclusionManager directly
        var arocc = FindFirstObjectByType<UnityEngine.XR.ARFoundation.AROcclusionManager>();
        if (arocc != null)
        {
            arocc.enabled = true;
            Debug.Log("[ARIA] Occlusion enabled via AROcclusionManager.");
            return;
        }

        Debug.LogWarning("[ARIA] No occlusion manager found — virtual objects won't be occluded by real geometry.");
    }

    private void OnDestroy()
    {
        if (_webcam != null && _webcam.isPlaying)
            _webcam.Stop();


        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.RemoveListener(OnMRUKSceneLoaded);

    }

    private void OnMRUKSceneLoaded()
    {
        Debug.Log("[ARIA] MRUK scene loaded — real room data available.");
        // Re-apply PTRL shadow material now that EffectMesh has spawned surfaces
        StartCoroutine(DelayedShadowSetup());
    }

    private System.Collections.IEnumerator DelayedShadowSetup()
    {
        // Wait 1 second for EffectMesh to finish generating surfaces
        yield return new WaitForSeconds(1f);
        shadowReceiver?.Reconfigure();
        Debug.Log("[ARIA] Shadow receiver reconfigured after MRUK scene load.");
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

        // Serialize MRUK room data first (populates _anchorRegistry for labels)
        string mrukJson = SerializeMRUKData();
        ARIADebugUI.AppendClaudeLog($"VOICE: \"{transcript}\"\nAnchors: {_anchorRegistry.Count} ({string.Join(", ", _anchorRegistry.Keys)})");

        // Capture annotated view — passthrough + wireframe + anchor labels + gaze crosshair
        SetStatus("Capturing annotated view...");
        byte[] jpeg = await CaptureAnnotatedViewAsync();
        ARIADebugUI.AppendClaudeLog($"IMAGE: {(jpeg != null ? jpeg.Length / 1024 + "KB" : "null")}");

        SetStatus("Asking Claude...");
        List<PlacementInstruction> instructions = await CallClaudeAsync(jpeg, mrukJson, transcript);

        if (instructions == null || instructions.Count == 0)
        {
            Debug.LogWarning("[ARIA] Claude returned no placement instructions.");
            SetStatus("No objects to place.");
            ARIADebugUI.AppendClaudeLog("Claude returned NO objects.");
            return;
        }

        // Log Claude's decisions
        foreach (var instr in instructions)
        {
            string placement = instr.anchor_id ?? instr.surface_label;
            string near = !string.IsNullOrEmpty(instr.near_anchor_id) ? $" near {instr.near_anchor_id}" : "";
            ARIADebugUI.AppendClaudeLog($"CLAUDE → {instr.category}\n  On: {placement}{near}\n  Size: {instr.height_metres:F2}x{instr.width_metres:F2}x{instr.depth_metres:F2}m");
        }

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

        // Use Meta's PassthroughCameraAccess (not WebCamTexture which returns black on Quest 3)
        var pca = FindFirstObjectByType<Meta.XR.PassthroughCameraAccess>();
        if (pca != null && pca.IsPlaying)
        {
            Texture camTex = pca.GetTexture();
            if (camTex != null && camTex.width > 2)
            {
                // Copy GPU texture to CPU-readable Texture2D
                var rt = RenderTexture.GetTemporary(camTex.width, camTex.height, 0);
                Graphics.Blit(camTex, rt);
                RenderTexture.active = rt;
                var snap = new Texture2D(camTex.width, camTex.height, TextureFormat.RGB24, false);
                snap.ReadPixels(new Rect(0, 0, camTex.width, camTex.height), 0, 0);
                snap.Apply();
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);

                byte[] jpeg = snap.EncodeToJPG(75);
                Debug.Log($"[ARIA] PCA capture: {camTex.width}x{camTex.height}, JPEG={jpeg.Length/1024}KB");
                Destroy(snap);
                return jpeg;
            }
            Debug.LogWarning("[ARIA] PCA texture not ready.");
        }

        // Fallback: WebCamTexture (likely returns black but try anyway)
        if (_webcam == null)
        {
            _webcam = new WebCamTexture();
            _webcam.Play();
        }
        await Task.Yield();
        await Task.Yield();
        if (_webcam.isPlaying && _webcam.width > 2)
        {
            var snap = new Texture2D(_webcam.width, _webcam.height, TextureFormat.RGB24, false);
            snap.SetPixels(_webcam.GetPixels());
            snap.Apply();
            byte[] jpeg = snap.EncodeToJPG(75);
            Debug.Log($"[ARIA] WebCam fallback: {_webcam.width}x{_webcam.height}, JPEG={jpeg.Length/1024}KB");
            Destroy(snap);
            return jpeg;
        }

        Debug.LogWarning("[ARIA] No camera available — text-only mode.");
        return null;
    }

    /// <summary>
    /// Captures what the user SEES — passthrough + wireframe + virtual objects.
    /// Better for Claude adjustment than raw passthrough since Claude can see
    /// the spawned objects and MRUK boundaries visually.
    /// </summary>
    private async Task<byte[]> CaptureRenderedViewAsync()
    {

        // Wait for end of frame so rendering is complete
        await Task.Yield();

        Camera cam = Camera.main;
        if (cam == null) return await CapturePassthroughFrameAsync();

        // Render the camera to a RenderTexture
        int w = 1536, h = 1536;
        var rt = new RenderTexture(w, h, 24);
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = null;

        // Read pixels from RenderTexture
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        Destroy(rt);

        byte[] jpeg = tex.EncodeToJPG(80);
        Debug.Log($"[ARIA] Rendered view capture: {w}x{h}, JPEG={jpeg.Length/1024}KB");
        Destroy(tex);
        return jpeg;
    }

    /// <summary>
    /// Captures a rendered view with anchor labels and gaze crosshair overlaid.
    /// Creates temporary TextMesh labels at each MRUK anchor position, renders
    /// the scene, then destroys them. Claude sees labeled surfaces in the image.
    /// </summary>
    private async Task<byte[]> CaptureAnnotatedViewAsync()
    {
        Camera cam = Camera.main;
        if (cam == null) return await CapturePassthroughFrameAsync();

        var tempObjects = new List<GameObject>();

        // Hide EffectMesh wireframe during capture — blue wireframe confuses Claude
        // Anchor labels provide spatial info instead
        var effectMesh = FindFirstObjectByType<Meta.XR.MRUtilityKit.EffectMesh>();
        bool wasWireframeVisible = false;
        if (effectMesh != null)
        {
            wasWireframeVisible = !effectMesh.HideMesh;
            if (wasWireframeVisible)
            {
                foreach (Transform child in effectMesh.transform)
                    child.gameObject.SetActive(false);
                effectMesh.HideMesh = true;
            }
        }

        try
        {
            // Create small labels at anchors that are visible to the camera
            foreach (var kvp in _anchorRegistry)
            {
                string id = kvp.Key;
                var anchor = kvp.Value;
                if (anchor == null) continue;

                Vector3 labelPos = anchor.transform.position;
                // Skip anchors behind the camera
                Vector3 toAnchor = labelPos - cam.transform.position;
                if (Vector3.Dot(toAnchor, cam.transform.forward) < 0) continue;

                // Scale label size by distance so they're readable but not huge
                float dist = toAnchor.magnitude;
                float charSize = Mathf.Clamp(dist * 0.012f, 0.015f, 0.04f);

                Vector3 toCamera = -toAnchor.normalized;
                labelPos += toCamera * 0.05f;

                // Background shadow text (black, slightly behind)
                var shadowGO = CreateTextMesh($"Label_Shadow_{id}", id,
                    labelPos - toCamera * 0.002f, cam, Color.black, charSize);
                tempObjects.Add(shadowGO);

                // Foreground label text (yellow)
                var labelGO = CreateTextMesh($"Label_{id}", id,
                    labelPos, cam, Color.yellow, charSize);
                tempObjects.Add(labelGO);
            }

            // Crosshair at gaze hit point
            Ray gazeRay = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.Raycast(gazeRay, out RaycastHit hit, 10f))
            {
                var crosshair = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                crosshair.name = "GazeCrosshair";
                crosshair.transform.position = hit.point + hit.normal * 0.01f;
                crosshair.transform.localScale = Vector3.one * 0.04f;
                Destroy(crosshair.GetComponent<Collider>());
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    mat.color = new Color(1f, 0f, 0f, 1f); // red
                    crosshair.GetComponent<Renderer>().material = mat;
                }
                tempObjects.Add(crosshair);
            }

            // Render the annotated scene
            await Task.Yield();
            return await CaptureRenderedViewAsync();
        }
        finally
        {
            // Always clean up temp objects
            foreach (var go in tempObjects)
                if (go != null) Destroy(go);

            // Restore wireframe if it was visible before capture
            if (wasWireframeVisible && effectMesh != null)
            {
                foreach (Transform child in effectMesh.transform)
                    child.gameObject.SetActive(true);
                effectMesh.HideMesh = false;
            }
        }
    }

    private static GameObject CreateTextMesh(string name, string text,
        Vector3 position, Camera cam, Color color, float charSize)
    {
        var go = new GameObject(name);
        go.transform.position = position;
        // Billboard: face the camera
        Vector3 lookDir = go.transform.position - cam.transform.position;
        if (lookDir.sqrMagnitude > 0.001f)
            go.transform.rotation = Quaternion.LookRotation(lookDir.normalized);

        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.characterSize = charSize;
        tm.fontSize = 36;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = color;
        // Use built-in font
        tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null && tm.font != null)
            mr.material = tm.font.material;

        return go;
    }

    // -------------------------------------------------------------------------
    // MRUK serialisation
    // -------------------------------------------------------------------------

    private string SerializeMRUKData()
    {
        try
        {
            var room = MRUK.Instance?.GetCurrentRoom();
            if (room == null)
            {
                Debug.LogWarning("[ARIA] MRUK room not ready — using mock data.");
                return MockMRUKJson();
            }

            Camera cam = Camera.main;
            _anchorRegistry.Clear();
            var counters = new Dictionary<string, int>();
            var surfaces = new List<object>();

            foreach (var anchor in room.Anchors)
            {
                var t = anchor.transform;
                string rawLabel = anchor.Label.ToString();

                // Assign short prefix for ID
                string prefix = rawLabel switch
                {
                    "WALL_FACE"    => "WALL",
                    "FLOOR"        => "FLOOR",
                    "CEILING"      => "CEILING",
                    "TABLE"        => "TABLE",
                    "COUCH"        => "COUCH",
                    "DOOR_FRAME"   => "DOOR",
                    "WINDOW_FRAME" => "WINDOW",
                    _              => "OTHER"
                };
                counters.TryGetValue(prefix, out int idx);
                string anchorId = $"{prefix}_{idx}";
                counters[prefix] = idx + 1;

                _anchorRegistry[anchorId] = anchor;

                // Size from PlaneRect (walls) or VolumeBounds (furniture)
                float sizeW = 0f, sizeH = 0f;
                if (anchor.PlaneRect.HasValue)
                {
                    sizeW = anchor.PlaneRect.Value.size.x;
                    sizeH = anchor.PlaneRect.Value.size.y;
                }
                else if (anchor.VolumeBounds.HasValue)
                {
                    sizeW = anchor.VolumeBounds.Value.size.x;
                    sizeH = anchor.VolumeBounds.Value.size.y;
                }

                // Screen position (viewport 0-1) so Claude can correlate anchor with image region
                object screenPos = null;
                if (cam != null)
                {
                    Vector3 vp = cam.WorldToViewportPoint(t.position);
                    if (vp.z > 0) // in front of camera
                        screenPos = new { x = Mathf.Round(vp.x * 100f) / 100f, y = Mathf.Round(vp.y * 100f) / 100f };
                }

                surfaces.Add(new
                {
                    id       = anchorId,
                    label    = rawLabel,
                    position = new { x = t.position.x, y = t.position.y, z = t.position.z },
                    normal   = new { x = t.forward.x, y = t.forward.y, z = t.forward.z },
                    size_metres = new { width = sizeW, height = sizeH },
                    screen_position = screenPos
                });
            }

            Debug.Log($"[ARIA] Serialized {surfaces.Count} anchors ({_anchorRegistry.Count} registered)");
            return JsonConvert.SerializeObject(new { surfaces });
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ARIA] MRUK serialisation error: {e.Message} — using mock data.");
            return MockMRUKJson();
        }
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
            text = $"Room layout (each surface has a unique ID matching labels in the image):\n{mrukJson}\n\n" +
                   $"The RED DOT in the image marks where the user is currently looking (gaze crosshair).\n\n" +
                   $"User command: {voiceCommand}"
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
                         "ANCHOR-AWARE PLACEMENT:\n" +
                         "The image has YELLOW LABELS (e.g. WALL_0, WALL_1, TABLE_0, FLOOR_0) at each room surface.\n" +
                         "A RED DOT shows where the user is looking (gaze crosshair).\n" +
                         "The room JSON includes each anchor's 'id' matching these labels, plus 'screen_position' (0-1 viewport coords).\n" +
                         "Use the crosshair position and labels to determine WHICH specific surface the user means.\n\n" +
                         "DEICTIC REFERENCE RESOLUTION:\n" +
                         "- 'that wall' / 'this wall' → the wall anchor nearest the crosshair/gaze\n" +
                         "- 'the door' → the DOOR anchor visible near crosshair\n" +
                         "- 'this corner' → two walls meeting near the crosshair; place objects on those walls and/or floor\n" +
                         "- 'decorate it' / 'fill this space' → multiple objects on surfaces near the crosshair\n" +
                         "- If no location specified, use the anchor nearest the crosshair\n\n" +
                         "CONTEXTUAL REASONING:\n" +
                         "- STYLE: Match the room's aesthetic from the image.\n" +
                         "- COLOR/MATERIAL: Complement existing room colors. Don't clash.\n" +
                         "- DIMENSIONS: Size proportionally to the room and target surface.\n" +
                         "- PLACEMENT: Consider existing objects to avoid overlap.\n\n" +
                         "PROMPT FIELD: Detailed image-generation description (style, color, material, proportions).\n\n" +
                         "Return a JSON array. Each element:\n" +
                         "- prompt (string — detailed description for image generation)\n" +
                         "- surface_label (string: FLOOR/WALL_FACE/CEILING/TABLE — the surface TYPE)\n" +
                         "- anchor_id (string — specific anchor ID like 'WALL_2' or 'TABLE_0', from room JSON)\n" +
                         "- near_anchor_id (string, optional — place near this anchor, e.g. 'on the floor near the door' → anchor_id='FLOOR_0', near_anchor_id='DOOR_0')\n" +
                         "- height_metres (float — real-world height)\n" +
                         "- width_metres (float — real-world width)\n" +
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
                   "and now want you to ADJUST it based on their voice command.\n\n" +
                   "*** PRIORITY RULE — WORDS OVERRIDE GAZE ***\n" +
                   "The user's VOICE COMMAND is the PRIMARY instruction. Follow it literally.\n" +
                   "- 'move up 20cm' → position_offset [0, 0.2, 0]. Do NOT move to where the red dot is.\n" +
                   "- 'make it bigger' → increase scale_factor. Do NOT relocate the object.\n" +
                   "- 'put it on that wall' → use the red dot to resolve WHICH wall, then anchor_id.\n" +
                   "- 'move it left' → position_offset [-0.3, 0, 0]. Do NOT re-place on a different surface.\n" +
                   "The red gaze dot is ONLY used to resolve deictic words: 'that', 'this', 'there', 'here'.\n" +
                   "If the user gives a specific direction/distance, USE EXACTLY THAT — ignore gaze position.\n\n" +
                   "HOW TO USE THE IMAGE:\n" +
                   "- ANNOTATED view: real room + virtual objects + yellow anchor labels (WALL_0, TABLE_0 etc.) + red gaze dot\n" +
                   "- You CAN see virtual objects already spawned — use this to avoid overlap\n" +
                   "- RED DOT = user's gaze. Only relevant for 'that/this/there' resolution, NOT for movement commands\n" +
                   "- YELLOW LABELS = anchor IDs matching room JSON data\n\n" +
                   "HOW TO USE MRUK DATA:\n" +
                   "- Room scan gives exact dimensions of walls, floor, ceiling, furniture in metres\n" +
                   "- surface_label tells the placement engine WHICH real surface to attach the object to\n" +
                   "- Only change surface_label if the user explicitly asks to move to a different surface\n\n" +
                   "RULES:\n" +
                   "1. surface_label: FLOOR / WALL_FACE / TABLE / BED / COUCH / CEILING\n" +
                   "   - Keep the CURRENT surface unless user says to change it\n" +
                   "   - TABLE = desk, table, any horizontal elevated surface\n\n" +
                   "2. scale_factor: SINGLE float (0.02 to 2.0) for UNIFORM resize\n" +
                   "   - 1.0 = keep current size. Only change if user asks for size change.\n\n" +
                   "3. position_offset: [x, y, z] metres to MOVE the object from CURRENT position\n" +
                   "   - x = right(+)/left(-), y = up(+)/down(-), z = forward(+)/backward(-)\n" +
                   "   - 'move up 20cm' → [0, 0.2, 0]\n" +
                   "   - 'slide left a bit' → [-0.2, 0, 0]\n" +
                   "   - 'raise it higher' → [0, 0.3, 0]\n" +
                   "   - If NOT moving, set [0, 0, 0]\n\n" +
                   "4. anchor_id: Only set if user wants to RELOCATE to a specific surface\n" +
                   "   - 'put it on that wall' → anchor_id = nearest wall anchor to red dot\n" +
                   "   - 'move up 20cm' → keep current anchor_id or omit (NO relocation)\n\n" +
                   "Return JSON ONLY:\n" +
                   "{\"surface_label\": \"FLOOR\", \"anchor_id\": \"FLOOR_0\", \"scale_factor\": 1.0, " +
                   "\"position_offset\": [0, 0.2, 0], \"category\": \"lamp\", " +
                   "\"reasoning\": \"User asked to move up 20cm, keeping on floor, raising by offset\"}"
        });

        string body = JsonConvert.SerializeObject(new
        {
            model = "claude-sonnet-4-6",
            max_tokens = 512,
            system = "You are the spatial reasoning AI for ARIA, a mixed reality furniture placement system on Meta Quest 3. " +
                     "CRITICAL: The user's VOICE COMMAND takes absolute priority over gaze direction. " +
                     "If they say 'move up 20cm', apply position_offset [0,0.2,0] — do NOT relocate to the gaze dot. " +
                     "Only use gaze to resolve 'that/this/there' references. " +
                     "You receive: annotated image + MRUK data + user voice command. " +
                     "Return JSON with surface_label, anchor_id, scale_factor, position_offset, category, reasoning. " +
                     "position_offset [x,y,z] moves the object (x=right, y=up, z=forward). " +
                     "Only change surface_label/anchor_id if user explicitly wants to RELOCATE. " +
                     "Return valid JSON only.",
            messages = new[] { new { role = "user", content } }
        });

        // Log what we're sending
        string requestLog = $"REQUEST:\n" +
            $"Object: {original.category}\n" +
            $"Size: {original.height_metres:F2}x{original.width_metres:F2}x{original.depth_metres:F2}m\n" +
            $"User pos: ({userPosition.x:F1},{userPosition.y:F1},{userPosition.z:F1})\n" +
            $"Gaze: ({gazeDirection.x:F2},{gazeDirection.y:F2},{gazeDirection.z:F2})\n" +
            $"Voice: \"{_lastUserCommand}\"\n" +
            $"Image: {passthroughJpeg.Length/1024}KB\n" +
            $"MRUK: {mrukJson.Length} chars";
        Debug.Log($"[ARIA] {requestLog}");
        ARIADebugUI.AppendClaudeLog(requestLog);

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
            string fullResponse = req.downloadHandler.text;
            var resp = JsonConvert.DeserializeObject<ClaudeResponse>(fullResponse);
            string claudeText = resp.content[0].text;
            string json = StripCodeFences(claudeText);

            string responseLog = $"RESPONSE:\n{claudeText}";
            Debug.Log($"[ARIA] {responseLog}");
            ARIADebugUI.AppendClaudeLog(responseLog);

            var result = JsonConvert.DeserializeObject<PlacementInstruction>(json);
            SetStatus($"Claude: {result.category} → {result.surface_label}, scale {result.scale_factor:F2}x. {result.reasoning ?? ""}");
            return result;
        }
        catch (Exception e)
        {
            Debug.LogError($"[ARIA] Claude adjustment parse error: {e.Message}\nRaw: {req.downloadHandler.text}");
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

        // Place on the correct MRUK surface — use specific anchor if Claude provided one
        MRUKAnchor targetAnchor = GetAnchorById(instr.anchor_id);
        MRUKAnchor nearAnchor = GetAnchorById(instr.near_anchor_id);
        placementEngine?.Place(root, instr.surface_label, targetAnchor, nearAnchor);

        // Auto-fit: shrink if object clips walls/ceiling/furniture

        var fitRoom = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();
        if (fitRoom != null)
            placementEngine?.FitToAvailableSpace(root, fitRoom);


        // Lighting + interaction setup
        // Lighting is handled by PTRL toggle, not per-spawn
        // Only add virtual light if object emits light (e.g. lamp)
        if (instr.emits_light) AddVirtualLight(root, instr);

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

        // Wait one frame for mesh renderers to initialize bounds properly
        await Task.Yield();

        // Place on the correct surface using the placement engine
        placementEngine?.Place(root, surfaceLabel);

        // Auto-fit to available MRUK space
        var fitRoom = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();
        if (fitRoom != null)
            placementEngine?.FitToAvailableSpace(root, fitRoom);


        // Reset layer so future raycasts CAN hit this object
        root.layer = 0;

        // Physics
        if (enablePhysics) AddPhysicsAndInteraction(root);

        // Reflection probe
        AddReflectionProbe(root);

        SetStatus($"Spawned: {name}. Look at target, tap 'Adjust with Claude'.");
        Bounds spawnBounds = CalculateMeshBounds(root);
        ARIADebugUI.AppendClaudeLog($"SPAWNED: {name}\nPos: {root.transform.position:F2}\nSize: {spawnBounds.size:F2}");
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

        SetStatus($"Capturing scene for Claude...");
        // Annotated view: passthrough + virtual objects + anchor labels + gaze crosshair
        // So Claude can see what's already spawned and where anchors are
        string mrukJson = SerializeMRUKData();
        byte[] jpeg = await CaptureAnnotatedViewAsync();
        if (jpeg == null)
        {
            SetStatus("Image capture failed.");
            Debug.LogError("[ARIA] Both rendered and passthrough capture failed");
            return;
        }

        SetStatus($"Claude analyzing {objName} ({jpeg.Length/1024}KB image)...");
        Debug.Log($"[ARIA] Sending to Claude: {objName}, image={jpeg.Length/1024}KB");

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

        // Claude returns scale_factor + surface_label + anchor_id + position_offset + reasoning
        float scaleFactor = refined.scale_factor > 0.01f ? refined.scale_factor : 1f;
        string surface = refined.surface_label ?? "FLOOR";
        string reasoning = refined.reasoning ?? "";

        // Apply uniform scale (proportional, no deformation)
        if (scaleFactor > 0.01f && Mathf.Abs(scaleFactor - 1f) > 0.05f)
        {
            lastSpawn.localScale *= scaleFactor;
            Debug.Log($"[ARIA] Claude scale: {scaleFactor:F2}x (uniform)");
        }

        // Only re-place if Claude explicitly wants to RELOCATE (gave an anchor_id)
        // If Claude just wants to move/scale, skip Place() to preserve current position
        MRUKAnchor targetAnchor = GetAnchorById(refined.anchor_id);
        bool hasOffset = refined.position_offset != null && refined.position_offset.Length >= 3
            && (Mathf.Abs(refined.position_offset[0]) > 0.001f ||
                Mathf.Abs(refined.position_offset[1]) > 0.001f ||
                Mathf.Abs(refined.position_offset[2]) > 0.001f);

        if (targetAnchor != null)
        {
            // Claude wants to relocate to a specific anchor
            placementEngine?.Place(lastSpawn.gameObject, surface, targetAnchor);

            var fitRoom = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();
            if (fitRoom != null)
                placementEngine?.FitToAvailableSpace(lastSpawn.gameObject, fitRoom);
        }
        // else: no relocation — just apply offset/scale from current position

        // Apply position offset AFTER fit — so Claude's "move up 20cm" actually sticks
        if (hasOffset)
        {
            Vector3 offset = new Vector3(refined.position_offset[0], refined.position_offset[1], refined.position_offset[2]);
            lastSpawn.position += offset;
            Debug.Log($"[ARIA] Claude position offset: ({offset.x:F2}, {offset.y:F2}, {offset.z:F2})");
        }

        string offsetStr = refined.position_offset != null && refined.position_offset.Length >= 3
            ? $", offset ({refined.position_offset[0]:F2},{refined.position_offset[1]:F2},{refined.position_offset[2]:F2})" : "";
        ARIADebugUI.AppendClaudeLog($"ADJUST: {objName}\n  → {refined.anchor_id ?? surface}, scale {scaleFactor:F2}x{offsetStr}\n  {reasoning}");


        SetStatus($"Claude: {objName} → {surface}, scale {scaleFactor:F2}x. {reasoning}");
        Debug.Log($"[ARIA] Claude adjustment: surface={surface}, scale={scaleFactor:F2}x, reason={reasoning}");
    }

    // -------------------------------------------------------------------------
    // Room lighting scan removed — now uses single-frame analysis + PTRL shader

    // -------------------------------------------------------------------------
    // Retroactive lighting — call from debug UI to apply lighting to all spawned objects
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // PTRL toggle — single button controls everything
    // -------------------------------------------------------------------------

    private bool _ptrlActive;
    private ShadowMode _shadowMode = ShadowMode.Directional;

    /// <summary>Whether PTRL is currently active. Used by DebugUI to gate selection.</summary>
    public bool IsPTRLActive => _ptrlActive;
    public ShadowMode CurrentShadowMode => _shadowMode;

    /// <summary>Cycles shadow mode. Only takes effect on next TogglePTRL call.</summary>
    public ShadowMode CycleShadowMode()
    {
        _shadowMode = _shadowMode == ShadowMode.Directional
            ? ShadowMode.PointLight : ShadowMode.Directional;
        // If PTRL is already on, re-apply with the new mode
        if (_ptrlActive)
        {
            ApplyShadowMode();
            UpdatePTRLSurfaceProperties();
        }
        string modeLabel = _shadowMode == ShadowMode.Directional ? "Directional" : "Point Light";
        SetStatus($"Shadow mode: {modeLabel}");
        Debug.Log($"[ARIA] Shadow mode: {_shadowMode}");
        ARIADebugUI.AppendClaudeLog($"Shadow mode → {modeLabel}");
        return _shadowMode;
    }

    /// <summary>
    /// Toggles PTRL on/off. Single button replaces Apply Lighting + ARIA vs Default.
    /// ON: EffectMesh gets PTRL material, shadows enabled, light spheres hidden, passthrough dims.
    /// OFF: EffectMesh back to wireframe, no shadows, plain default lighting.
    /// </summary>
    public bool TogglePTRL()
    {
        _ptrlActive = !_ptrlActive;

        // EffectMesh is NEVER touched here — wireframe is independent (Toggle Wireframe button)

        if (_ptrlActive)
        {
            // ── PTRL ON ──────────────────────────────────────────────────

            // Create invisible shadow surfaces (floor + walls + ceiling) with PTRL material
            CreatePTRLShadowFloor();

            // Apply shadow mode (directional or point light)
            ApplyShadowMode();

            // Hide sphere visuals so they don't cast shadows themselves
            foreach (var lightGO in _manualLights)
            {
                if (lightGO == null) continue;
                foreach (var r in lightGO.GetComponentsInChildren<Renderer>())
                    r.enabled = false;
            }

            // Configure PTRL surface properties based on shadow mode
            UpdatePTRLSurfaceProperties();

            // Make sure EffectMesh surfaces don't cast shadows
            var effectMesh = FindFirstObjectByType<Meta.XR.MRUtilityKit.EffectMesh>();
            if (effectMesh != null)
            {
                foreach (var r in effectMesh.GetComponentsInChildren<Renderer>())
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                }
            }

            SetStatus($"PTRL ON ({_shadowMode}) — {_manualLights.Count} light(s)");
            Debug.Log($"[ARIA] PTRL enabled. Shadow mode: {_shadowMode}");
            ARIADebugUI.AppendClaudeLog($"PTRL ON ({_shadowMode})\nLights: {_manualLights.Count}\nAmbient: {RenderSettings.ambientLight}");
        }
        else
        {
            // ── PTRL OFF ─────────────────────────────────────────────────

            // Destroy the shadow floor
            DestroyPTRLShadowFloor();

            // Directional light: plain white from above, NO shadows, full intensity
            if (sceneDirectionalLight != null)
            {
                sceneDirectionalLight.shadows = LightShadows.None;
                sceneDirectionalLight.color = Color.white;
                sceneDirectionalLight.transform.rotation = Quaternion.Euler(90, 0, 0);
                sceneDirectionalLight.intensity = 1f;
            }

            // Reset ambient to default bright so objects don't go dark
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.5f); // neutral grey ambient

            // Disable point lights, show sphere visuals
            foreach (var lightGO in _manualLights)
            {
                if (lightGO == null) continue;
                var light = lightGO.GetComponent<Light>();
                if (light != null) light.enabled = false;
                foreach (var r in lightGO.GetComponentsInChildren<Renderer>())
                    r.enabled = true;
            }

            SetStatus("PTRL OFF — default lighting");
            Debug.Log("[ARIA] PTRL disabled.");
            ARIADebugUI.AppendClaudeLog("PTRL OFF");
        }

        return _ptrlActive;
    }

    /// <summary>
    /// Configures lights based on current shadow mode.
    /// Directional: single directional light aimed from first sphere toward objects, point lights for color only.
    /// PointLight: each point light casts its own shadows (cubemap), no directional shadow.
    /// </summary>
    private void ApplyShadowMode()
    {
        if (_shadowMode == ShadowMode.Directional)
        {
            // Directional light aimed from first sphere toward objects — sharp shadows
            if (sceneDirectionalLight != null && _manualLights.Count > 0)
            {
                Vector3 lightPos = _manualLights[0].transform.position;
                Transform objRoot = spawnRoot != null ? spawnRoot : transform;
                Vector3 targetPos = objRoot.childCount > 0
                    ? objRoot.GetChild(0).position : Vector3.zero;
                Vector3 dir = (targetPos - lightPos).normalized;
                if (dir.sqrMagnitude > 0.01f)
                    sceneDirectionalLight.transform.rotation = Quaternion.LookRotation(dir);

                // Directional intensity: scale up from sampled light for strong shadows
                float sampledIntensity = _manualLights[0].GetComponent<Light>()?.intensity ?? 2f;
                sceneDirectionalLight.shadows = LightShadows.Soft;
                sceneDirectionalLight.shadowStrength = 0.95f;
                sceneDirectionalLight.intensity = Mathf.Clamp(sampledIntensity * 0.7f, 0.8f, 2.0f);
                sceneDirectionalLight.color = _manualLights[0].GetComponent<Light>()?.color ?? Color.white;
            }
            else if (sceneDirectionalLight != null)
            {
                sceneDirectionalLight.shadows = LightShadows.None;
                sceneDirectionalLight.intensity = 0.5f;
            }

            // Point lights: color/illumination only, NO shadows
            foreach (var lightGO in _manualLights)
            {
                if (lightGO == null) continue;
                var light = lightGO.GetComponent<Light>();
                if (light != null)
                {
                    light.enabled = true;
                    light.shadows = LightShadows.None;
                }
            }
        }
        else // PointLight mode
        {
            // Directional light: dim ambient fill, NO shadows
            if (sceneDirectionalLight != null)
            {
                sceneDirectionalLight.shadows = LightShadows.None;
                sceneDirectionalLight.intensity = 0.1f;
                sceneDirectionalLight.color = Color.white;
                sceneDirectionalLight.transform.rotation = Quaternion.Euler(90, 0, 0);
            }

            // Each point light casts its own shadows — aggressive intensity for visible shadows
            foreach (var lightGO in _manualLights)
            {
                if (lightGO == null) continue;
                var light = lightGO.GetComponent<Light>();
                if (light != null)
                {
                    // Aggressive intensity boost — point lights need a LOT more power for visible shadows
                    float height = lightGO.transform.position.y;
                    float heightBoost = Mathf.Clamp(height * 4f, 3f, 15f);
                    light.intensity = Mathf.Max(light.intensity, heightBoost);
                    light.range = Mathf.Max(8f, height * 3f); // wide range for shadow coverage
                    light.enabled = true;
                    light.shadows = LightShadows.Soft;
                    light.shadowStrength = 1f;
                    light.shadowBias = 0.02f;
                    light.shadowNormalBias = 0.3f;
                }
            }
        }
    }

    /// <summary>
    /// Updates PTRL surface shader properties for the current shadow mode.
    /// Walls always receiveShadows=true, never cast shadows (avoids self-shadowing and rectangular artifacts).
    /// </summary>
    private void UpdatePTRLSurfaceProperties()
    {
        // Point light: strong shadow, slight highlight. Directional: moderate shadow, slight highlight.
        float hlOpacity = _shadowMode == ShadowMode.PointLight ? 0.15f : 0.08f;
        float shadowInt = _shadowMode == ShadowMode.PointLight ? 6f : 0.85f;

        foreach (var surface in _ptrlShadowSurfaces)
        {
            if (surface == null) continue;
            var rend = surface.GetComponent<Renderer>();
            if (rend == null) continue;

            // Walls: NEVER cast shadows (avoids rectangular shadow artifacts in directional
            // AND self-shadowing darkness in point light). Always receive shadows so objects
            // cast visible shadows onto walls.
            // Floor + ceiling: never cast shadows either, just receive.
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = true;

            var mat = rend.material;
            if (mat != null)
            {
                mat.SetFloat("_HighlightOpacity", hlOpacity);
                mat.SetFloat("_ShadowIntensity", shadowInt);
            }
        }
    }

    // Invisible PTRL shadow surfaces — floor + wall planes, independent of EffectMesh
    private readonly List<GameObject> _ptrlShadowSurfaces = new();

    private void CreatePTRLShadowFloor()
    {
        if (_ptrlShadowSurfaces.Count > 0) return; // already exists

        // Get PTRL material from ShadowReceiverSetup, or find/create it
        Material ptrlMat = shadowReceiver?.PTRLMaterial;
        if (ptrlMat == null)
        {
            ptrlMat = UnityEngine.Resources.FindObjectsOfTypeAll<Material>()
                .FirstOrDefault(m => m.name == "TransparentSceneAnchor");
        }
        if (ptrlMat == null)
        {
            var shader = Shader.Find("Meta/MRUK/Scene/HighlightsAndShadows");
            if (shader != null) ptrlMat = new Material(shader);
        }
        if (ptrlMat == null)
        {
            Debug.LogError("[ARIA] PTRL material not found.");
            return;
        }

        var room = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();

        // ── Floor plane ─────────────────────────────────────────────────
        float floorY = 0f;
        if (room != null)
        {
            var floorAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(Meta.XR.MRUtilityKit.MRUKAnchor.SceneLabels.FLOOR));
            if (floorAnchor != null) floorY = floorAnchor.transform.position.y;
        }

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "ARIA_PTRL_ShadowFloor";
        floor.transform.position = new Vector3(0f, floorY + 0.001f, 0f);
        floor.transform.localScale = new Vector3(2f, 1f, 2f); // 20x20m
        Destroy(floor.GetComponent<Collider>());
        ConfigurePTRLRenderer(floor, ptrlMat);
        _ptrlShadowSurfaces.Add(floor);

        Debug.Log($"[ARIA] PTRL shadow floor at Y={floorY}");

        // ── Wall planes — one per MRUK wall anchor ──────────────────────
        if (room != null)
        {
            int wallCount = 0;
            foreach (var anchor in room.Anchors)
            {
                if (!anchor.HasAnyLabel(Meta.XR.MRUtilityKit.MRUKAnchor.SceneLabels.WALL_FACE))
                    continue;

                // Wall anchor: position = center, forward = surface normal, PlaneRect = size
                Vector3 wallPos = anchor.transform.position;
                Quaternion wallRot = anchor.transform.rotation;
                Vector2 wallSize = anchor.PlaneRect.HasValue
                    ? anchor.PlaneRect.Value.size
                    : new Vector2(4f, 2.8f); // fallback

                var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
                wall.name = $"ARIA_PTRL_ShadowWall_{wallCount}";
                // Offset slightly inward (toward room center) so it doesn't z-fight with EffectMesh
                wall.transform.position = wallPos - anchor.transform.forward * 0.002f;
                wall.transform.rotation = wallRot;
                wall.transform.localScale = new Vector3(wallSize.x, wallSize.y, 1f);
                Destroy(wall.GetComponent<Collider>());
                ConfigurePTRLRenderer(wall, ptrlMat);
                _ptrlShadowSurfaces.Add(wall);
                wallCount++;
            }
            Debug.Log($"[ARIA] PTRL shadow walls created: {wallCount}");
        }

        // ── Ceiling plane ───────────────────────────────────────────────
        if (room != null)
        {
            var ceilAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(Meta.XR.MRUtilityKit.MRUKAnchor.SceneLabels.CEILING));
            if (ceilAnchor != null)
            {
                var ceil = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ceil.name = "ARIA_PTRL_ShadowCeiling";
                ceil.transform.position = new Vector3(0f, ceilAnchor.transform.position.y - 0.001f, 0f);
                ceil.transform.rotation = Quaternion.Euler(180f, 0f, 0f); // face downward
                ceil.transform.localScale = new Vector3(2f, 1f, 2f);
                Destroy(ceil.GetComponent<Collider>());
                ConfigurePTRLRenderer(ceil, ptrlMat);
                _ptrlShadowSurfaces.Add(ceil);
                Debug.Log($"[ARIA] PTRL shadow ceiling at Y={ceilAnchor.transform.position.y}");
            }
        }
    }

    private static void ConfigurePTRLRenderer(GameObject go, Material ptrlMat)
    {
        var r = go.GetComponent<Renderer>();
        r.material = ptrlMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = true;
    }

    private void DestroyPTRLShadowFloor()
    {
        foreach (var go in _ptrlShadowSurfaces)
        {
            if (go != null) Destroy(go);
        }
        _ptrlShadowSurfaces.Clear();
        Debug.Log("[ARIA] PTRL shadow surfaces destroyed.");
    }

    // -------------------------------------------------------------------------
    // Manual light placement — user places virtual lights at real light positions
    // -------------------------------------------------------------------------

    private readonly List<GameObject> _manualLights = new();

    /// <summary>Returns the manual light list for DebugUI selection/deletion.</summary>
    public List<GameObject> GetManualLights() => _manualLights;

    /// <summary>Removes a light from the tracked list (called when DebugUI deletes it).</summary>
    public void RemoveManualLight(GameObject lightGO)
    {
        _manualLights.Remove(lightGO);
    }

    /// <summary>
    /// Places a virtual point light at the crosshair position.
    /// User looks at their real ceiling light and presses this button.
    /// The PTRL shader renders shadows from this light on room surfaces.
    /// </summary>
    public void PlaceLightAtCrosshair()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // Spawn 1m in front of user with default white light
        Vector3 lightPos = cam.transform.position + cam.transform.forward * 1f;
        Color lightColor = new Color(1f, 0.95f, 0.85f); // default warm white
        float lightIntensity = 5f; // strong enough for visible point light shadows

        var lightGO = new GameObject($"ARIA_ManualLight_{_manualLights.Count}");
        lightGO.transform.position = lightPos;

        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = lightColor;
        light.intensity = lightIntensity;
        light.range = 6f; // 6m — enough reach for point light shadows
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 1f;
        light.shadowBias = 0.02f;
        light.shadowNormalBias = 0.3f;

        // Visual indicator — small glowing sphere
        var indicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        indicator.transform.SetParent(lightGO.transform);
        indicator.transform.localPosition = Vector3.zero;
        indicator.transform.localScale = Vector3.one * 0.05f;
        Destroy(indicator.GetComponent<Collider>());
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (mat != null)
        {
            mat.SetColor("_BaseColor", lightColor);
            mat.SetColor("_EmissionColor", lightColor * 3f);
            mat.EnableKeyword("_EMISSION");
            indicator.GetComponent<Renderer>().material = mat;
        }

        // Add collider so controller can detect it + Interaction SDK grab components
        var col = indicator.AddComponent<SphereCollider>();
        col.radius = 2f; // generous grab radius in local space (sphere is 0.05 scale = 0.1m grab zone)

        // Add Grabbable + HandGrabInteractable via reflection (avoids compile-time dependency)

        try
        {
            var grabbableType = System.Type.GetType("Oculus.Interaction.Grabbable, Oculus.Interaction.Runtime");
            var grabInterType = System.Type.GetType("Oculus.Interaction.HandGrab.HandGrabInteractable, Oculus.Interaction.Runtime");
            if (grabbableType != null) lightGO.AddComponent(grabbableType);
            if (grabInterType != null) lightGO.AddComponent(grabInterType);
            Debug.Log($"[ARIA] Grab components added to light #{_manualLights.Count}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[ARIA] Grab setup skipped: {e.Message}");
        }


        // Add Rigidbody (kinematic — no gravity, stays where you drop it)
        var rb = lightGO.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;

        _manualLights.Add(lightGO);

        // Add small "Confirm" world-space UI near the sphere
        var confirmCanvas = new GameObject("ConfirmUI");
        var canvas = confirmCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        confirmCanvas.AddComponent<UnityEngine.UI.CanvasScaler>();
        confirmCanvas.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        confirmCanvas.transform.SetParent(lightGO.transform);
        confirmCanvas.transform.localPosition = new Vector3(0f, 0.08f, 0f); // above the sphere
        confirmCanvas.transform.localScale = Vector3.one * 0.002f;
        var canvasRT = confirmCanvas.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(200, 90); // wider for instruction text

        // Billboard: always face the camera
        confirmCanvas.AddComponent<BillboardFaceCamera>();

        // Confirm button
        var btnGO = new GameObject("ConfirmBtn", typeof(RectTransform),
            typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(BoxCollider));
        btnGO.transform.SetParent(confirmCanvas.transform, false);
        var btnRT = btnGO.GetComponent<RectTransform>();
        btnRT.sizeDelta = new Vector2(120, 40);
        btnGO.GetComponent<UnityEngine.UI.Image>().color = new Color(0.1f, 0.6f, 0.1f, 0.9f);
        btnGO.GetComponent<BoxCollider>().size = new Vector3(120, 40, 10);

        var txtGO = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
        txtGO.transform.SetParent(btnGO.transform, false);
        var txtRT = txtGO.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = Vector2.zero; txtRT.offsetMax = Vector2.zero;
        var txt = txtGO.GetComponent<UnityEngine.UI.Text>();
        txt.text = "Confirm";
        txt.font = UnityEngine.Resources.GetBuiltinResource<Font>("Arial.ttf");
        txt.fontSize = 20;
        txt.fontStyle = FontStyle.Bold;
        txt.color = Color.white;

        // Instruction label below the button
        var instrGO = new GameObject("InstrText", typeof(RectTransform), typeof(UnityEngine.UI.Text));
        instrGO.transform.SetParent(confirmCanvas.transform, false);
        var instrRT = instrGO.GetComponent<RectTransform>();
        instrRT.anchoredPosition = new Vector2(0, -35f);
        instrRT.sizeDelta = new Vector2(200, 50);
        var instrTxt = instrGO.GetComponent<UnityEngine.UI.Text>();
        instrTxt.text = "Look at the area\nthis light illuminates";
        instrTxt.font = UnityEngine.Resources.GetBuiltinResource<Font>("Arial.ttf");
        instrTxt.fontSize = 11;
        instrTxt.fontStyle = FontStyle.Italic;
        instrTxt.color = new Color(0.9f, 0.9f, 0.6f, 0.9f);
        instrTxt.alignment = TextAnchor.UpperCenter;
        txt.alignment = TextAnchor.MiddleCenter;

        // On confirm: hide sphere, capture passthrough, sample color, update light
        int lightIndex = _manualLights.Count - 1;
        btnGO.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() =>
        {
            ConfirmLightPlacement(lightIndex);
            Destroy(confirmCanvas); // remove confirm UI after clicking
        });

        // Light starts disabled until confirmed — just the visual sphere shows
        light.enabled = false;

        SetStatus($"Light #{_manualLights.Count} spawned — grab, position, then click Confirm");
        Debug.Log($"[ARIA] Manual light spawned at {lightPos} — awaiting confirmation");
    }

    /// <summary>
    /// Called when user confirms a light sphere placement.
    /// Hides the sphere momentarily, captures passthrough, samples color, updates light.
    /// </summary>
    public async void ConfirmLightPlacement(int lightIndex)
    {
        if (lightIndex < 0 || lightIndex >= _manualLights.Count) return;
        var lightGO = _manualLights[lightIndex];
        if (lightGO == null) return;

        // Hide the sphere so it doesn't block the passthrough capture
        foreach (var r in lightGO.GetComponentsInChildren<Renderer>())
            r.enabled = false;

        SetStatus("3...");
        await Task.Delay(1000);
        SetStatus("2...");
        await Task.Delay(1000);
        SetStatus("1...");
        await Task.Delay(1000);

        // Capture raw passthrough
        byte[] jpeg = await CapturePassthroughFrameAsync();

        // Sample 4x4 grid for color (our SH-inspired contribution)
        Color lightColor = new Color(1f, 0.95f, 0.85f);
        float lightIntensity = 5f; // strong baseline for visible shadows

        if (jpeg != null && jpeg.Length > 1000)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (tex.LoadImage(jpeg))
            {
                // ── Center 4x4 grid: light source color & intensity ──
                Color avgColor = Color.black;
                float maxBright = 0f;
                int samples = 0;
                for (int sRow = 0; sRow < 4; sRow++)
                {
                    for (int sCol = 0; sCol < 4; sCol++)
                    {
                        Color p = tex.GetPixelBilinear((sCol + 0.5f) / 4f, (sRow + 0.5f) / 4f);
                        avgColor += p;
                        if (p.grayscale > maxBright) maxBright = p.grayscale;
                        samples++;
                    }
                }
                avgColor /= Mathf.Max(samples, 1);
                if (avgColor.grayscale > 0.05f)
                {
                    lightColor = Color.Lerp(Color.white, avgColor, 0.7f);
                    lightIntensity = Mathf.Clamp(maxBright * 5f, 3f, 10f);
                }

                // ── Edge sampling: ambient room color (fakes bounce light) ──
                // Sample the borders of the image — walls, floor, ceiling visible at edges
                // This captures the overall room tone that indirect light would carry
                Color ambientColor = Color.black;
                int ambientSamples = 0;
                for (int i = 0; i < 8; i++)
                {
                    float u = (i % 4) / 3f;  // 0, 0.33, 0.66, 1.0
                    // Top edge
                    ambientColor += tex.GetPixelBilinear(u, 0.95f);
                    // Bottom edge
                    ambientColor += tex.GetPixelBilinear(u, 0.05f);
                    // Left edge
                    ambientColor += tex.GetPixelBilinear(0.05f, u);
                    // Right edge
                    ambientColor += tex.GetPixelBilinear(0.95f, u);
                    ambientSamples += 4;
                }
                ambientColor /= Mathf.Max(ambientSamples, 1);

                // Set Unity's global ambient light to match room tone
                // This fills the dark side of objects with soft room-colored light
                // Intensity kept low (0.15-0.4) so it's subtle fill, not overpowering
                float ambientStrength = Mathf.Clamp(ambientColor.grayscale * 0.5f, 0.15f, 0.4f);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Color.Lerp(Color.black, ambientColor, ambientStrength);
                Debug.Log($"[ARIA] Ambient set: color={ambientColor}, strength={ambientStrength:F2}");
            }
            Destroy(tex);
        }

        // Apply sampled color to the light
        var light = lightGO.GetComponent<Light>();
        if (light != null)
        {
            light.color = lightColor;
            light.intensity = lightIntensity;
            light.enabled = true;
        }

        // Update sphere visual color
        foreach (var r in lightGO.GetComponentsInChildren<Renderer>())
        {
            r.enabled = true;
            if (r.material != null)
            {
                r.material.SetColor("_BaseColor", lightColor);
                r.material.SetColor("_EmissionColor", lightColor * 3f);
            }
        }

        string colorName = GetColorName(lightColor);
        string kelvin = GetColorTemperature(lightColor);
        SetStatus($"Light #{lightIndex + 1}: {colorName} ({kelvin}), intensity={lightIntensity:F1}");
        Debug.Log($"[ARIA] Light #{lightIndex + 1} confirmed at {lightGO.transform.position}, color={lightColor} ({colorName}), ambient={RenderSettings.ambientLight}");
        ARIADebugUI.AppendClaudeLog($"LIGHT #{lightIndex + 1} CONFIRMED:\nColor: {colorName} ({kelvin})\nRGB: ({lightColor.r:F2}, {lightColor.g:F2}, {lightColor.b:F2})\nIntensity: {lightIntensity:F1}\nAmbient fill: {RenderSettings.ambientLight}\nPos: {lightGO.transform.position:F2}");
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
        light.shadowStrength = 1f;

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
        // Ensure all renderers on virtual objects CAST shadows (for PTRL shadow floor)
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = true;
        }

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

    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    /// <summary>Converts a color to a human-readable name based on hue/saturation/brightness.</summary>
    private static string GetColorName(Color c)
    {
        float h, s, v;
        Color.RGBToHSV(c, out h, out s, out v);

        if (s < 0.1f)
        {
            if (v > 0.9f) return "White";
            if (v > 0.6f) return "Light Grey";
            if (v > 0.3f) return "Grey";
            return "Dark Grey";
        }

        // Hue-based naming (0-1 range)
        string hue;
        if (h < 0.04f || h > 0.96f) hue = "Red";
        else if (h < 0.11f) hue = "Orange";
        else if (h < 0.18f) hue = "Yellow";
        else if (h < 0.22f) hue = "Yellow-Green";
        else if (h < 0.42f) hue = "Green";
        else if (h < 0.52f) hue = "Cyan";
        else if (h < 0.72f) hue = "Blue";
        else if (h < 0.82f) hue = "Purple";
        else hue = "Pink";

        if (v < 0.3f) return $"Dark {hue}";
        if (s < 0.4f) return $"Pale {hue}";
        if (v > 0.85f && s > 0.5f) return $"Bright {hue}";
        return $"Warm {hue}";
    }

    /// <summary>Estimates color temperature in Kelvin from RGB color (approximate).</summary>
    private static string GetColorTemperature(Color c)
    {
        // Simple estimate: warm (reddish) = low K, cool (bluish) = high K
        float warmth = (c.r * 0.6f + c.g * 0.3f) / Mathf.Max(c.b + 0.01f, 0.01f);
        int kelvin;
        if (warmth > 2.5f) kelvin = 2700;       // warm incandescent
        else if (warmth > 1.8f) kelvin = 3200;   // warm white LED
        else if (warmth > 1.3f) kelvin = 4000;   // neutral white
        else if (warmth > 0.9f) kelvin = 5000;   // daylight
        else kelvin = 6500;                       // cool daylight
        return $"~{kelvin}K";
    }

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
    public string   anchor_id;       // specific anchor (e.g. "WALL_2"), null = generic placement
    public string   near_anchor_id;  // hint: place near this anchor (e.g. "DOOR_0" for "near the door")
    public float[]  position_offset; // [x,y,z] metres offset for fine adjustment (move up/left/forward)
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
