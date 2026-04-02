// ARIAOrchestrator.cs
// Master controller for the ARIA pipeline.
// Voice transcript → Claude → per-object: Gemini → HiTEM3D → GLTFast → place + scale + light.
// Objects appear progressively — each placed as soon as its own generation finishes.
//
// APK NOTE: Passthrough frame capture and real MRUK data require a Quest APK build.
//           Editor runs with a null passthrough image and mock MRUK room data.

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

    // API keys — loaded from config.json at runtime, never hardcoded
    private string _claudeKey;
    private string _geminiKey;
    private string _hitemAccessKey;
    private string _hitemSecretKey;

    // Tracks grey-mesh previews so they can be swapped when the textured mesh arrives
    private readonly Dictionary<string, GameObject> _previews = new();

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        LoadConfig();
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

    private static string GetConfigPath()
    {
#if UNITY_EDITOR
        // Project root — two levels up from Assets/
        return Path.GetFullPath(Path.Combine(Application.dataPath, "../config.json"));
#else
        // Quest: push config.json before first run with:
        // adb push config.json /sdcard/Android/data/<package-name>/files/config.json
        return Path.Combine(Application.persistentDataPath, "config.json");
#endif
    }

    // -------------------------------------------------------------------------
    // Entry point — called by the voice input system
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pass the recognised voice transcript here to trigger the full ARIA pipeline.
    /// </summary>
    public async void ProcessVoiceCommand(string transcript)
    {
        Debug.Log($"[ARIA] Voice command: \"{transcript}\"");

        byte[] passthroughJpeg = await CapturePassthroughFrameAsync();
        string mrukJson        = SerializeMRUKData();

        List<PlacementInstruction> instructions =
            await CallClaudeAsync(passthroughJpeg, mrukJson, transcript);

        if (instructions == null || instructions.Count == 0)
        {
            Debug.LogWarning("[ARIA] Claude returned no placement instructions.");
            return;
        }

        Debug.Log($"[ARIA] Spawning {instructions.Count} object(s) progressively...");

        // Launch all object pipelines concurrently — progressive placement
        var tasks = new List<Task>();
        foreach (var instr in instructions)
            tasks.Add(ProcessObjectAsync(instr));

        await Task.WhenAll(tasks);
        Debug.Log("[ARIA] All objects placed.");
    }

    // -------------------------------------------------------------------------
    // Passthrough frame capture
    // -------------------------------------------------------------------------

    private async Task<byte[]> CapturePassthroughFrameAsync()
    {
        // APK only — read Quest passthrough camera texture and encode to JPEG.
        // Passthrough camera is unavailable in the editor.
        // TODO: implement via OVRCameraRig passthrough texture + Texture2D.EncodeToJPG()
        await Task.Yield();
        return null;
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
        surfaces = new[]
        {
            new { label = "FLOOR",     position = new { x = 0f, y = 0f,   z = 0f  }, normal = new { x = 0f, y =  1f, z = 0f }, scale = new { x = 4f, y = 3f } },
            new { label = "WALL_FACE", position = new { x = 0f, y = 1.5f, z = -3f }, normal = new { x = 0f, y =  0f, z = 1f }, scale = new { x = 4f, y = 3f } },
            new { label = "CEILING",   position = new { x = 0f, y = 3f,   z = 0f  }, normal = new { x = 0f, y = -1f, z = 0f }, scale = new { x = 4f, y = 3f } }
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
                source = new
                {
                    type       = "base64",
                    media_type = "image/jpeg",
                    data       = Convert.ToBase64String(jpeg)
                }
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
            max_tokens = 1024,
            system     = "You are a spatial interior design AI. Respond with valid JSON only. " +
                         "No explanation. Return a JSON array where each element has: " +
                         "prompt (string), surface_label (string: FLOOR/WALL_FACE/CEILING), " +
                         "height_metres (float), category (string).",
            messages   = new[] { new { role = "user", content } }
        });

        using var req = new UnityWebRequest("https://api.anthropic.com/v1/messages", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
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

    private async Task ProcessObjectAsync(PlacementInstruction instr)
    {
        Debug.Log($"[ARIA] Pipeline start: {instr.prompt}");

        // 1 — Gemini image generation
        byte[] png = await CallGeminiAsync(instr.prompt);
        if (png == null) { Debug.LogError($"[ARIA] Gemini failed: {instr.prompt}"); return; }

        // 2 — HiTEM3D auth
        string token = await GetHiTEMTokenAsync();
        if (token == null) { Debug.LogError("[ARIA] HiTEM3D auth failed."); return; }

        // 3a — Staged: grey mesh first (~20 s) for perceived speed
        string greyId  = await SubmitHiTEMTaskAsync(token, png, requestType: 1);
        string greyUrl = await PollHiTEMTaskAsync(token, greyId);
        if (greyUrl != null)
            await SpawnObjectAsync(greyUrl, instr, isPreview: true);

        // 3b — Full textured mesh replaces the preview
        string texId  = await SubmitHiTEMTaskAsync(token, png, requestType: 2, meshUrl: greyUrl);
        string texUrl = await PollHiTEMTaskAsync(token, texId);
        if (texUrl != null)
            await SpawnObjectAsync(texUrl, instr, isPreview: false);
    }

    // -------------------------------------------------------------------------
    // Gemini image generation
    // -------------------------------------------------------------------------

    private async Task<byte[]> CallGeminiAsync(string objectPrompt)
    {
        string prompt = $"{objectPrompt}, single object, white background, studio lighting, photorealistic";
        string body   = JsonConvert.SerializeObject(new
        {
            instances  = new[] { new { prompt } },
            parameters = new { sampleCount = 1 }
        });

        string url = "https://generativelanguage.googleapis.com/v1beta/models/" +
                     $"imagen-3.0-generate-002:predict?key={_geminiKey}";

        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("content-type", "application/json");

        await AwaitRequest(req.SendWebRequest());

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ARIA] Gemini error: {req.error}\n{req.downloadHandler.text}");
            return null;
        }

        var resp = JsonConvert.DeserializeObject<GeminiResponse>(req.downloadHandler.text);
        return Convert.FromBase64String(resp.predictions[0].bytesBase64Encoded);
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
            new MultipartFormDataSection("resolution",   "1024")
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

    private async Task<string> PollHiTEMTaskAsync(string token, string taskId)
    {
        if (taskId == null) return null;

        while (true)
        {
            await Task.Delay(5000);

            using var req = new UnityWebRequest(
                $"https://api.hitem3d.ai/open-api/v1/query-task?task_id={taskId}", "GET");
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {token}");

            await AwaitRequest(req.SendWebRequest());

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[ARIA] HiTEM3D poll error: {req.error}");
                return null;
            }

            var    resp   = JsonConvert.DeserializeObject<HiTEMQueryResponse>(req.downloadHandler.text);
            string status = resp?.data?.status ?? "unknown";
            Debug.Log($"[ARIA] HiTEM3D [{taskId}] → {status}");

            switch (status)
            {
                case "success": return resp.data.url;
                case "failed":
                    Debug.LogError($"[ARIA] HiTEM3D task failed: {taskId}");
                    return null;
            }
            // created / queueing / processing — keep polling
        }
    }

    // -------------------------------------------------------------------------
    // GLTFast spawn + systems integration
    // -------------------------------------------------------------------------

    private async Task SpawnObjectAsync(string glbUrl, PlacementInstruction instr, bool isPreview)
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

        var  gltf = new GltfImport();
        bool ok   = await gltf.LoadGltfBinary(req.downloadHandler.data, new Uri(glbUrl));
        if (!ok)
        {
            Debug.LogError($"[ARIA] GLTFast failed: {instr.prompt}");
            Destroy(root);
            return;
        }
        await gltf.InstantiateMainSceneAsync(root.transform);

        // Scale to LLM-specified real-world height
        scaleSystem?.ApplyScale(root, instr.height_metres);

        // Semantic surface placement
        placementEngine?.Place(root, instr.surface_label);

        // Shadow receiver on MRUK scene mesh
        shadowReceiver?.Configure();

        // SH lighting estimator runs at 5 Hz continuously — no per-object call needed

        // Swap preview with final textured mesh
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
    // Utilities
    // -------------------------------------------------------------------------

    /// <summary>Bridges UnityWebRequestAsyncOperation to Task so we can await it.</summary>
    private static Task AwaitRequest(UnityWebRequestAsyncOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }

    /// <summary>Strips ```json ... ``` fences that LLMs sometimes add around JSON output.</summary>
    private static string StripCodeFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        int start = s.IndexOf('\n') + 1;
        int end   = s.LastIndexOf("```");
        return end > start ? s.Substring(start, end - start).Trim() : s;
    }
}

// =============================================================================
// Data models
// =============================================================================

[Serializable]
public class PlacementInstruction
{
    public string prompt;
    public string surface_label;
    public float  height_metres;
    public string category;
}

// Claude
[Serializable] public class ClaudeResponse   { public List<ClaudeContent>    content;     }
[Serializable] public class ClaudeContent    { public string type; public string text;    }

// Gemini
[Serializable] public class GeminiResponse   { public List<GeminiPrediction> predictions; }
[Serializable] public class GeminiPrediction { public string bytesBase64Encoded;          }

// HiTEM3D
[Serializable] public class HiTEMAuthResponse  { public HiTEMAuthData   data; }
[Serializable] public class HiTEMAuthData       { public string accessToken;   }
[Serializable] public class HiTEMSubmitResponse { public HiTEMSubmitData data; }
[Serializable] public class HiTEMSubmitData     { public string task_id;       }
[Serializable] public class HiTEMQueryResponse  { public HiTEMQueryData  data; }
[Serializable] public class HiTEMQueryData      { public string status; public string url; }
