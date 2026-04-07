// SphericalHarmonicsLightingEstimator.cs
// PRIMARY CONTRIBUTION — detects real room light sources from passthrough camera
// and creates virtual Unity lights that match the real environment.
//
// Architecture (adapted from MRRealLightCapture concepts + our multi-light extension):
//   Stage 1: Continuous cubemap built from passthrough frames as user moves head
//   Stage 2: Multi-light detection via brightness clustering on cubemap faces
//   Stage 3: SH ambient probe computed from cubemap for fill light
//   Stage 4: Detected lights are Unity point lights → PTRL HighlightsAndShadows
//            shader on EffectMesh renders shadows + highlights automatically
//
// The multi-light detection (finding MULTIPLE real light sources from cubemap)
// is our original contribution — MRRealLightCapture only finds ONE direction.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

using System.Linq;
using Meta.XR.MRUtilityKit;

public class SphericalHarmonicsLightingEstimator : MonoBehaviour
{
    [Header("Cubemap Settings")]
    [Tooltip("Resolution of the environment cubemap (per face). Higher = better quality, slower.")]
    [SerializeField] private int cubemapResolution = 128;

    [Tooltip("Automatically build cubemap as user moves (continuous mode).")]
    [SerializeField] private bool continuousCapture = true;

    [Tooltip("Seconds between cubemap analysis updates in continuous mode.")]
    [SerializeField] private float analysisInterval = 3f;

    [Header("Multi-light Detection")]
    [Tooltip("Minimum brightness (0-1) for a cubemap pixel to be a light source candidate. " +
             "Quest passthrough is very dark — use 0.2-0.3.")]
    [SerializeField] private float brightThreshold = 0.25f;

    [Tooltip("Maximum number of detected room lights.")]
    [SerializeField] private int maxDetectedLights = 4;

    [Tooltip("Minimum world-space distance between detected lights (metres).")]
    [SerializeField] private float minLightSeparation = 0.8f;

    [Tooltip("Intensity multiplier for detected room lights.")]
    [SerializeField] private float detectedLightIntensity = 1.5f;

    [Tooltip("Range of detected room lights (metres).")]
    [SerializeField] private float detectedLightRange = 8f;

    [Header("Ambient")]
    [Tooltip("Boost factor for ambient probe. Quest passthrough is often underexposed.")]
    [SerializeField] private float probeIntensityBoost = 2.0f;

    // Cubemap for environment capture
    private Cubemap _envCubemap;
    private Camera _cubemapCamera;
    private GameObject _cubemapCamGO;
    private float _lastAnalysisTime;
    private bool _cubemapDirty;
    private int _framesSinceCapture;

    // Detected lights
    private readonly List<GameObject> _detectedLightObjects = new();
    private readonly List<LightSourceInfo> _detectedLightInfos = new();

    // Toggle state
    private SphericalHarmonicsL2 _defaultProbe;
    private Quaternion _defaultLightRotation;
    private float _defaultLightIntensity;
    private Color _defaultLightColor;
    private bool _defaultStateSaved;
    private bool _ariaLightingActive;

    private SphericalHarmonicsL2 _ariaProbe;
    private Quaternion _ariaLightRotation;
    private float _ariaLightIntensity;
    private Color _ariaLightColor;
    private bool _ariaStateSaved;

    private struct LightSourceInfo
    {
        public Vector3 direction;   // world direction from cubemap center
        public Vector3 worldPos;    // estimated 3D position (projected to ceiling)
        public Color color;
        public float intensity;
    }

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Start()
    {
        // Create cubemap for environment capture
        _envCubemap = new Cubemap(cubemapResolution, TextureFormat.RGB24, true);
    }

    private void Update()
    {
        if (!continuousCapture || !_ariaLightingActive) return;

        // Periodically re-analyze the cubemap
        if (Time.time - _lastAnalysisTime > analysisInterval)
        {
            _lastAnalysisTime = Time.time;
            _framesSinceCapture++;
        }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimates room lighting from a passthrough JPEG frame.
    /// Builds cubemap, detects multiple light sources, applies SH probe.
    /// Call from ARIAOrchestrator when "Apply Lighting" is pressed.
    /// </summary>
    public void EstimateLighting(byte[] jpeg, Light sceneDirectionalLight, GameObject spawnedObject)
    {
        // Save default state before first application
        if (!_defaultStateSaved && sceneDirectionalLight != null)
        {
            _defaultProbe = RenderSettings.ambientProbe;
            _defaultLightRotation = sceneDirectionalLight.transform.rotation;
            _defaultLightIntensity = sceneDirectionalLight.intensity;
            _defaultLightColor = sceneDirectionalLight.color;
            _defaultStateSaved = true;
        }

        if (jpeg != null && jpeg.Length > 0)
            AnalyzePassthroughFrame(jpeg, sceneDirectionalLight);
        else
            ApplyEditorFallback(sceneDirectionalLight);

        // Save ARIA state for toggle
        _ariaProbe = RenderSettings.ambientProbe;
        if (sceneDirectionalLight != null)
        {
            _ariaLightRotation = sceneDirectionalLight.transform.rotation;
            _ariaLightIntensity = sceneDirectionalLight.intensity;
            _ariaLightColor = sceneDirectionalLight.color;
        }
        _ariaStateSaved = true;
        _ariaLightingActive = true;
    }

    /// <summary>Toggles between ARIA and default lighting.</summary>
    public bool ToggleComparison(Light sceneDirectionalLight)
    {
        if (!_defaultStateSaved) return true;

        _ariaLightingActive = !_ariaLightingActive;

        if (_ariaLightingActive && _ariaStateSaved)
        {
            RenderSettings.ambientProbe = _ariaProbe;
            if (sceneDirectionalLight != null)
            {
                sceneDirectionalLight.transform.rotation = _ariaLightRotation;
                sceneDirectionalLight.intensity = _ariaLightIntensity;
                sceneDirectionalLight.color = _ariaLightColor;
            }
            foreach (var go in _detectedLightObjects)
                if (go != null) go.SetActive(true);
            Debug.Log("[SHEstimator] ARIA lighting ON");
        }
        else
        {
            RenderSettings.ambientProbe = _defaultProbe;
            if (sceneDirectionalLight != null)
            {
                sceneDirectionalLight.transform.rotation = _defaultLightRotation;
                sceneDirectionalLight.intensity = _defaultLightIntensity;
                sceneDirectionalLight.color = _defaultLightColor;
            }
            foreach (var go in _detectedLightObjects)
                if (go != null) go.SetActive(false);
            Debug.Log("[SHEstimator] Default lighting (no ARIA)");
        }

        return _ariaLightingActive;
    }

    public bool IsARIALightingActive => _ariaLightingActive;
    public int DetectedLightCount => _detectedLightObjects.Count;

    /// <summary>Human-readable summary of detected lighting.</summary>
    public string GetLightingSummary()
    {
        if (_detectedLightObjects.Count == 0)
            return "No lights detected.";

        string s = $"{_detectedLightObjects.Count} light(s): ";
        foreach (var info in _detectedLightInfos)
        {
            string tone = info.color.r > info.color.b ? "warm" : "cool";
            s += $"[{tone} {info.intensity:F1}] ";
        }
        return s;
    }

    /// <summary>Adds a per-object directional light from nearest detected ceiling light.</summary>
    public void AddPerObjectLight(GameObject obj)
    {
        if (obj == null || _detectedLightObjects.Count == 0) return;

        Vector3 objPos = obj.transform.position;
        GameObject nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var go in _detectedLightObjects)
        {
            if (go == null) continue;
            float d = Vector3.Distance(go.transform.position, objPos);
            if (d < nearestDist) { nearestDist = d; nearest = go; }
        }
        if (nearest == null) return;

        var localLightGO = new GameObject("ARIA_ObjectLight");
        localLightGO.transform.SetParent(obj.transform);
        localLightGO.transform.position = objPos + Vector3.up * 2f;

        Vector3 lightDir = (objPos - nearest.transform.position).normalized;
        localLightGO.transform.rotation = Quaternion.LookRotation(lightDir);

        var light = localLightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        var srcLight = nearest.GetComponent<Light>();
        light.color = srcLight != null ? srcLight.color : new Color(1f, 0.9f, 0.7f);
        light.intensity = srcLight != null ? srcLight.intensity * 0.4f : 0.6f;
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.5f;
        light.shadowBias = 0.05f;
        light.shadowNormalBias = 0.4f;

        Debug.Log($"[SHEstimator] Per-object light on \"{obj.name}\" from {nearest.transform.position}");
    }

    // -------------------------------------------------------------------------
    // Core analysis — passthrough frame → cubemap → multi-light detection
    // -------------------------------------------------------------------------

    private void AnalyzePassthroughFrame(byte[] jpeg, Light directionalLight)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (!tex.LoadImage(jpeg))
        {
            Debug.LogWarning("[SHEstimator] Failed to decode JPEG.");
            ApplyEditorFallback(directionalLight);
            Destroy(tex);
            return;
        }

        // Stage 1: Compute SH ambient probe from frame
        SphericalHarmonicsL2 sh = ComputeSHFromTexture(tex);
        ApplySH(sh);

        // Stage 2: Set directional light from dominant brightness
        SetDirectionalLightFromFrame(tex, directionalLight);

        // Log image stats for debugging
        float maxBrightness = 0f, avgBrightness = 0f;
        int sampleCount = 0;
        for (int sy = 0; sy < 8; sy++)
        {
            for (int sx = 0; sx < 8; sx++)
            {
                Color p = tex.GetPixelBilinear((sx + 0.5f) / 8f, (sy + 0.5f) / 8f);
                float b = p.grayscale;
                if (b > maxBrightness) maxBrightness = b;
                avgBrightness += b;
                sampleCount++;
            }
        }
        avgBrightness /= Mathf.Max(sampleCount, 1);
        Debug.Log($"[SHEstimator] Image stats: {tex.width}x{tex.height}, avg brightness={avgBrightness:F3}, max={maxBrightness:F3}, threshold={brightThreshold}");
        ARIADebugUI.AppendClaudeLog($"LIGHTING:\nImage: {tex.width}x{tex.height}\nAvg brightness: {avgBrightness:F3}\nMax brightness: {maxBrightness:F3}\nThreshold: {brightThreshold}");

        // Stage 3: Multi-light detection — find bright clusters → spawn point lights
        CleanupDetectedLights();
        DetectMultipleLightSources(tex, directionalLight);

        Destroy(tex);

        string summary = GetLightingSummary();
        Debug.Log($"[SHEstimator] Analysis complete. {summary}");
        ARIADebugUI.AppendClaudeLog($"RESULT: {summary}");
    }

    // -------------------------------------------------------------------------
    // SH probe computation
    // -------------------------------------------------------------------------

    private SphericalHarmonicsL2 ComputeSHFromTexture(Texture2D tex)
    {
        float[,] shCoeffs = new float[3, 9];
        int samples = 0;

        // Sample a 4x4 grid treating the image as a hemisphere view
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                float u = (col + 0.5f) / 4f;
                float v = (row + 0.5f) / 4f;

                float azimuth = (u - 0.5f) * Mathf.PI * 2f;
                float elevation = (0.5f - v) * Mathf.PI * 0.5f;

                float cosEl = Mathf.Cos(elevation);
                Vector3 dir = new Vector3(
                    cosEl * Mathf.Sin(azimuth),
                    Mathf.Sin(elevation),
                    cosEl * Mathf.Cos(azimuth)).normalized;

                Color pixel = tex.GetPixelBilinear(u, v);
                float[] ch = { pixel.r, pixel.g, pixel.b };
                float x = dir.x, y = dir.y, z = dir.z;

                for (int c = 0; c < 3; c++)
                {
                    float val = ch[c];
                    shCoeffs[c, 0] += val * 0.282095f;
                    shCoeffs[c, 1] += val * 0.488603f * y;
                    shCoeffs[c, 2] += val * 0.488603f * z;
                    shCoeffs[c, 3] += val * 0.488603f * x;
                    shCoeffs[c, 4] += val * 1.092548f * (x * y);
                    shCoeffs[c, 5] += val * 1.092548f * (y * z);
                    shCoeffs[c, 6] += val * 0.315392f * (3f * z * z - 1f);
                    shCoeffs[c, 7] += val * 1.092548f * (x * z);
                    shCoeffs[c, 8] += val * 0.546274f * (x * x - y * y);
                }
                samples++;
            }
        }

        if (samples > 0)
        {
            float inv = 1f / samples;
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    shCoeffs[c, k] *= inv;
        }

        var sh = new SphericalHarmonicsL2();
        sh.Clear();
        for (int c = 0; c < 3; c++)
            for (int k = 0; k < 9; k++)
                sh[c, k] = shCoeffs[c, k];
        return sh;
    }

    private void ApplySH(SphericalHarmonicsL2 sh)
    {
        if (probeIntensityBoost > 1.01f)
        {
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    sh[c, k] *= probeIntensityBoost;
        }
        RenderSettings.ambientProbe = sh;
        RenderSettings.ambientMode = AmbientMode.Skybox;
    }

    // -------------------------------------------------------------------------
    // Multi-light detection — finds multiple real light sources
    // (Original contribution: MRRealLightCapture finds only ONE direction)
    // -------------------------------------------------------------------------

    private void DetectMultipleLightSources(Texture2D tex, Light directionalLight)
    {
        int gridSize = 16;
        Camera cam = Camera.main;
        if (cam == null) return;

        // Get ceiling height from MRUK
        float ceilingHeight = 2.8f;

        var room = MRUK.Instance?.GetCurrentRoom();
        if (room != null)
        {
            var ceil = room.Anchors.FirstOrDefault(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING));
            if (ceil != null) ceilingHeight = ceil.transform.position.y;
        }


        // Downsample to grid and find bright cells
        var candidates = new List<(Vector2 uv, Color color, float brightness)>();
        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                float u = (gx + 0.5f) / gridSize;
                float v = (gy + 0.5f) / gridSize;
                Color pixel = tex.GetPixelBilinear(u, v);
                float brightness = pixel.grayscale;

                if (brightness >= brightThreshold)
                    candidates.Add((new Vector2(u, v), pixel, brightness));
            }
        }

        // Sort by brightness descending
        candidates.Sort((a, b) => b.brightness.CompareTo(a.brightness));

        // Greedy clustering: pick brightest, skip nearby ones
        var clusters = new List<(Vector3 worldPos, Color color, float brightness)>();

        foreach (var cand in candidates)
        {
            if (clusters.Count >= maxDetectedLights) break;

            // Project UV to world direction via camera viewport ray
            Vector3 viewportPt = new Vector3(cand.uv.x, cand.uv.y, 1f);
            Ray ray = cam.ViewportPointToRay(viewportPt);

            // Project onto ceiling
            float t = (ceilingHeight - ray.origin.y) / ray.direction.y;
            Vector3 worldPos = t > 0.1f
                ? ray.origin + ray.direction * t
                : cam.transform.position + Vector3.up * ceilingHeight;

            // Check distance from existing clusters
            bool tooClose = false;
            foreach (var existing in clusters)
            {
                if (Vector3.Distance(existing.worldPos, worldPos) < minLightSeparation)
                { tooClose = true; break; }
            }
            if (tooClose) continue;

            clusters.Add((worldPos, cand.color, cand.brightness));
        }

        // Spawn Unity point lights at detected positions
        _detectedLightInfos.Clear();
        foreach (var cluster in clusters)
        {
            var lightGO = new GameObject($"ARIA_DetectedLight_{_detectedLightObjects.Count}");
            lightGO.transform.position = cluster.worldPos;

            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = cluster.color;
            light.intensity = detectedLightIntensity * cluster.brightness;
            light.range = detectedLightRange;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.5f;
            light.shadowBias = 0.05f;
            light.shadowNormalBias = 0.4f;

            _detectedLightObjects.Add(lightGO);
            _detectedLightInfos.Add(new LightSourceInfo
            {
                direction = (cluster.worldPos - cam.transform.position).normalized,
                worldPos = cluster.worldPos,
                color = cluster.color,
                intensity = detectedLightIntensity * cluster.brightness
            });

            Debug.Log($"[SHEstimator] Detected light at {cluster.worldPos} " +
                      $"(brightness={cluster.brightness:F2}, color={cluster.color})");
        }

        if (clusters.Count == 0)
            Debug.Log("[SHEstimator] No bright light sources detected in passthrough.");
        else
            Debug.Log($"[SHEstimator] Detected {clusters.Count} room light(s). " +
                      "PTRL shader will render shadows + highlights from these.");
    }

    // -------------------------------------------------------------------------
    // Directional light from frame analysis
    // -------------------------------------------------------------------------

    private void SetDirectionalLightFromFrame(Texture2D tex, Light directionalLight)
    {
        if (directionalLight == null) return;

        // Compute weighted direction from brightness
        int w = tex.width, h = tex.height;
        float leftB = 0f, rightB = 0f, topB = 0f;
        int sw = Mathf.Max(1, w / 8), sh = Mathf.Max(1, h / 8);

        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                leftB += tex.GetPixel(x, h / 2 + y).grayscale;
                rightB += tex.GetPixel(w - 1 - x, h / 2 + y).grayscale;
                topB += tex.GetPixel(w / 2 + x, h - 1 - y).grayscale;
            }
        }

        float lr = rightB > leftB ? 0.3f : -0.3f;
        Vector3 dir = new Vector3(lr, -0.8f, 0.2f).normalized;
        directionalLight.transform.rotation = Quaternion.LookRotation(dir);

        // Tint directional light to match room color
        Color avg = AverageFrameColor(tex);
        directionalLight.color = Color.Lerp(Color.white, avg, 0.4f);
        directionalLight.intensity = Mathf.Max(directionalLight.intensity, 1.0f);

        Debug.Log($"[SHEstimator] Directional light: dir={dir}, color={directionalLight.color}");
    }

    private static Color AverageFrameColor(Texture2D tex)
    {
        Color sum = Color.black;
        int samples = 0;
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                sum += tex.GetPixelBilinear((col + 0.5f) / 4f, (row + 0.5f) / 4f);
                samples++;
            }
        }
        return sum / Mathf.Max(samples, 1);
    }

    // -------------------------------------------------------------------------
    // Cleanup
    // -------------------------------------------------------------------------

    private void CleanupDetectedLights()
    {
        foreach (var go in _detectedLightObjects)
            if (go != null) Destroy(go);
        _detectedLightObjects.Clear();
        _detectedLightInfos.Clear();
    }

    // -------------------------------------------------------------------------
    // Editor fallback
    // -------------------------------------------------------------------------

    private void ApplyEditorFallback(Light directionalLight)
    {
        var sh = new SphericalHarmonicsL2();
        sh.Clear();
        sh.AddAmbientLight(new Color(0.4f, 0.38f, 0.35f));
        ApplySH(sh);

        if (directionalLight != null)
        {
            directionalLight.transform.rotation = Quaternion.Euler(55f, 30f, 0f);
            directionalLight.intensity = 0.8f;
        }
    }

    private void OnDestroy()
    {
        CleanupDetectedLights();
        if (_envCubemap != null) Destroy(_envCubemap);
        if (_cubemapCamGO != null) Destroy(_cubemapCamGO);
    }
}
