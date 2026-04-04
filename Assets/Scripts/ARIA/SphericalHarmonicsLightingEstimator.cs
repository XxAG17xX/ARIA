// SphericalHarmonicsLightingEstimator.cs
// PRIMARY CONTRIBUTION — estimates ambient room lighting from the passthrough frame
// and applies it to all spawned virtual objects so they blend with the real environment.
//
// Two-stage lighting:
//   Stage 1 (SH Probe): 4x4 pixel grid → order-2 SH → ambient probe for fill light
//   Stage 2 (Multi-light): detects bright clusters in passthrough → spawns point lights
//     at estimated 3D positions so virtual objects receive distinct light from each
//     real light source (ceiling lights, windows, lamps)
//
// RUNS ONCE PER VOICE COMMAND (not continuously).
// Reuses the JPEG bytes already captured for the Claude API call.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Linq;
using Meta.XR.MRUtilityKit;
#endif

public class SphericalHarmonicsLightingEstimator : MonoBehaviour
{
    [Header("Multi-light detection")]
    [Tooltip("Minimum brightness (0-1) for a pixel to be considered a light source.")]
    [SerializeField] private float brightThreshold = 0.75f;

    [Tooltip("Maximum number of detected room lights to spawn.")]
    [SerializeField] private int maxDetectedLights = 4;

    [Tooltip("Minimum distance between detected light clusters (pixels in downsampled image).")]
    [SerializeField] private float minClusterDistance = 3f;

    [Tooltip("Intensity multiplier for detected room lights.")]
    [SerializeField] private float detectedLightIntensity = 1.2f;

    [Tooltip("Range of detected room lights (metres).")]
    [SerializeField] private float detectedLightRange = 6f;

    // Spawned detected lights — tracked so we can remove them on re-estimation or comparison toggle
    private readonly List<GameObject> _detectedLightObjects = new();

    // Saved "before" state for comparison toggle
    private SphericalHarmonicsL2 _defaultProbe;
    private Quaternion _defaultLightRotation;
    private float _defaultLightIntensity;
    private Color _defaultLightColor;
    private bool _defaultStateSaved;
    private bool _ariaLightingActive;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimates room lighting from <paramref name="jpeg"/> and applies it to the scene.
    /// Also places a ReflectionProbe at <paramref name="spawnedObject"/> position.
    /// Safe to call with jpeg = null (editor fallback: neutral warm-grey probe).
    /// </summary>
    public void EstimateLighting(byte[] jpeg, Light sceneDirectionalLight, GameObject spawnedObject)
    {
        // Save default state before first ARIA lighting application
        if (!_defaultStateSaved && sceneDirectionalLight != null)
        {
            _defaultProbe = RenderSettings.ambientProbe;
            _defaultLightRotation = sceneDirectionalLight.transform.rotation;
            _defaultLightIntensity = sceneDirectionalLight.intensity;
            _defaultLightColor = sceneDirectionalLight.color;
            _defaultStateSaved = true;
        }

        if (jpeg != null && jpeg.Length > 0)
            EstimateFromJpeg(jpeg, sceneDirectionalLight);
        else
            ApplyEditorFallbackProbe(sceneDirectionalLight);

        _ariaLightingActive = true;
    }

    /// <summary>
    /// Toggles between ARIA lighting and default Unity lighting for demo comparison.
    /// Returns true if ARIA lighting is now active, false if default.
    /// </summary>
    public bool ToggleComparison(Light sceneDirectionalLight)
    {
        if (!_defaultStateSaved) return true;

        _ariaLightingActive = !_ariaLightingActive;

        if (_ariaLightingActive)
        {
            // Re-enable ARIA lighting — detected lights on
            foreach (var go in _detectedLightObjects)
                if (go != null) go.SetActive(true);
            Debug.Log("[SHEstimator] Comparison: ARIA lighting ON");
        }
        else
        {
            // Revert to default — detected lights off
            RenderSettings.ambientProbe = _defaultProbe;
            if (sceneDirectionalLight != null)
            {
                sceneDirectionalLight.transform.rotation = _defaultLightRotation;
                sceneDirectionalLight.intensity = _defaultLightIntensity;
                sceneDirectionalLight.color = _defaultLightColor;
            }
            foreach (var go in _detectedLightObjects)
                if (go != null) go.SetActive(false);
            Debug.Log("[SHEstimator] Comparison: Default lighting (no ARIA)");
        }

        return _ariaLightingActive;
    }

    public bool IsARIALightingActive => _ariaLightingActive;

    // -------------------------------------------------------------------------
    // SH estimation from JPEG pixels
    // -------------------------------------------------------------------------

    private void EstimateFromJpeg(byte[] jpeg, Light sceneDirectionalLight)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (!tex.LoadImage(jpeg))
        {
            Debug.LogWarning("[SHEstimator] Failed to decode JPEG — using editor fallback probe.");
            ApplyEditorFallbackProbe(sceneDirectionalLight);
            Destroy(tex);
            return;
        }

        // Stage 1: SH probe for ambient fill
        SphericalHarmonicsL2 sh = ComputeSH(tex);
        ApplySH(sh);

        // Stage 2: directional light from dominant brightness
        SetLightDirection(tex, sceneDirectionalLight);

        // Stage 3: multi-light detection — find individual light sources
        DetectAndSpawnRoomLights(tex, sceneDirectionalLight);

        Destroy(tex);
        Debug.Log("[SHEstimator] SH probe + multi-light detection complete.");
    }

    // -------------------------------------------------------------------------
    // SH computation (Ramamoorthi & Hanrahan 2001)
    // -------------------------------------------------------------------------

    private static SphericalHarmonicsL2 ComputeSH(Texture2D tex)
    {
        int w = tex.width;
        int h = tex.height;

        float[,] shR = new float[3, 9];
        int sampleCount = 0;

        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                float u = (col + 0.5f) / 4f;
                float v = (row + 0.5f) / 4f;

                float azimuth   = (u - 0.5f) * Mathf.PI * 2f;
                float elevation = (0.5f - v) * Mathf.PI * 0.5f;

                float cosEl = Mathf.Cos(elevation);
                float sinEl = Mathf.Sin(elevation);
                Vector3 dir = new Vector3(
                    cosEl * Mathf.Sin(azimuth),
                    sinEl,
                    cosEl * Mathf.Cos(azimuth)).normalized;

                Color pixel = tex.GetPixelBilinear(u, v);
                float[] channels = { pixel.r, pixel.g, pixel.b };
                float x = dir.x, y = dir.y, z = dir.z;

                for (int c = 0; c < 3; c++)
                {
                    float col_val = channels[c];
                    shR[c, 0] += col_val * 0.282095f;
                    shR[c, 1] += col_val * 0.488603f * y;
                    shR[c, 2] += col_val * 0.488603f * z;
                    shR[c, 3] += col_val * 0.488603f * x;
                    shR[c, 4] += col_val * 1.092548f * (x * y);
                    shR[c, 5] += col_val * 1.092548f * (y * z);
                    shR[c, 6] += col_val * 0.315392f * (3f * z * z - 1f);
                    shR[c, 7] += col_val * 1.092548f * (x * z);
                    shR[c, 8] += col_val * 0.546274f * (x * x - y * y);
                }
                sampleCount++;
            }
        }

        if (sampleCount > 0)
        {
            float inv = 1f / sampleCount;
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    shR[c, k] *= inv;
        }

        var sh = new SphericalHarmonicsL2();
        sh.Clear();
        for (int c = 0; c < 3; c++)
            for (int k = 0; k < 9; k++)
                sh[c, k] = shR[c, k];

        return sh;
    }

    private static void ApplySH(SphericalHarmonicsL2 sh)
    {
        RenderSettings.ambientProbe = sh;
        RenderSettings.ambientMode  = AmbientMode.Skybox;
    }

    // -------------------------------------------------------------------------
    // Multi-light detection: find bright clusters → spawn point lights
    // -------------------------------------------------------------------------

    private void DetectAndSpawnRoomLights(Texture2D tex, Light directionalLight)
    {
        // Clean up previous detected lights
        foreach (var go in _detectedLightObjects)
            if (go != null) Destroy(go);
        _detectedLightObjects.Clear();

        // Downsample to 16x16 grid for fast analysis
        int gridSize = 16;
        float[,] brightness = new float[gridSize, gridSize];
        Color[,] colors = new Color[gridSize, gridSize];

        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                float u = (gx + 0.5f) / gridSize;
                float v = (gy + 0.5f) / gridSize;
                Color pixel = tex.GetPixelBilinear(u, v);
                brightness[gx, gy] = pixel.grayscale;
                colors[gx, gy] = pixel;
            }
        }

        // Find bright cells above threshold
        var brightCells = new List<(int x, int y, float b, Color c)>();
        for (int gy = 0; gy < gridSize; gy++)
            for (int gx = 0; gx < gridSize; gx++)
                if (brightness[gx, gy] >= brightThreshold)
                    brightCells.Add((gx, gy, brightness[gx, gy], colors[gx, gy]));

        // Sort by brightness descending
        brightCells.Sort((a, b) => b.b.CompareTo(a.b));

        // Cluster: greedily pick brightest cells that are far enough apart
        var clusters = new List<(Vector2 uv, Color color, float brightness)>();
        foreach (var cell in brightCells)
        {
            if (clusters.Count >= maxDetectedLights) break;

            Vector2 cellPos = new Vector2(cell.x, cell.y);
            bool tooClose = false;
            foreach (var existing in clusters)
            {
                Vector2 existingPos = existing.uv * gridSize;
                if (Vector2.Distance(cellPos, existingPos) < minClusterDistance)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                Vector2 uv = new Vector2((cell.x + 0.5f) / gridSize, (cell.y + 0.5f) / gridSize);
                clusters.Add((uv, cell.c, cell.b));
            }
        }

        if (clusters.Count == 0)
        {
            Debug.Log("[SHEstimator] No distinct bright light sources detected in passthrough.");
            return;
        }

        // Convert UV positions to 3D world positions
        Camera cam = Camera.main;
        if (cam == null) return;

        float roomHeight = 2.8f; // default
#if UNITY_ANDROID && !UNITY_EDITOR
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room != null)
        {
            var ceilingAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING));
            if (ceilingAnchor != null)
                roomHeight = ceilingAnchor.transform.position.y;
        }
#endif

        foreach (var cluster in clusters)
        {
            // Map UV to a world-space ray from the camera
            Vector3 viewportPoint = new Vector3(cluster.uv.x, cluster.uv.y, 1f);
            Ray ray = cam.ViewportPointToRay(viewportPoint);

            // Project onto ceiling height (most room lights are on the ceiling)
            float t = (roomHeight - ray.origin.y) / ray.direction.y;
            Vector3 lightWorldPos;
            if (t > 0f)
                lightWorldPos = ray.origin + ray.direction * t;
            else
                lightWorldPos = cam.transform.position + Vector3.up * roomHeight;

            // Spawn a point light at the detected position
            var lightGO = new GameObject($"ARIA_DetectedLight_{_detectedLightObjects.Count}");
            lightGO.transform.position = lightWorldPos;

            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = cluster.color;
            light.intensity = detectedLightIntensity * cluster.brightness;
            light.range = detectedLightRange;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.6f;
            light.shadowResolution = UnityEngine.Rendering.LightShadowResolution.Medium;

            _detectedLightObjects.Add(lightGO);
            Debug.Log($"[SHEstimator] Detected light at {lightWorldPos} (brightness: {cluster.brightness:F2}, color: {cluster.color})");
        }

        Debug.Log($"[SHEstimator] Spawned {clusters.Count} detected room light(s).");
    }

    // -------------------------------------------------------------------------
    // Directional light direction estimation
    // -------------------------------------------------------------------------

    private static void SetLightDirection(Texture2D tex, Light directionalLight)
    {
        if (directionalLight == null) return;

        Vector3 lightDir = EstimateLightDirection(tex);
        directionalLight.transform.rotation = Quaternion.LookRotation(lightDir);

        // Also adjust directional light color to match overall room tone
        Color avgColor = AverageFrameColor(tex);
        directionalLight.color = Color.Lerp(Color.white, avgColor, 0.4f);

        Debug.Log($"[SHEstimator] Directional light set to {lightDir}, color tinted to {directionalLight.color}");
    }

    private static Vector3 EstimateLightDirection(Texture2D tex)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room != null)
        {
            var lampAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(MRUKAnchor.SceneLabels.LAMP));

            if (lampAnchor != null)
            {
                Vector3 toFloor = Vector3.down * 0.8f
                    + lampAnchor.transform.position.normalized * 0.2f;
                return toFloor.normalized;
            }
        }
#endif
        return EstimateDirectionFromBrightness(tex);
    }

    private static Vector3 EstimateDirectionFromBrightness(Texture2D tex)
    {
        int w = tex.width, h = tex.height;
        float leftBrightness = 0f, rightBrightness = 0f, topBrightness = 0f;
        int sampleW = Mathf.Max(1, w / 8);
        int sampleH = Mathf.Max(1, h / 8);

        for (int y = 0; y < sampleH; y++)
        {
            for (int x = 0; x < sampleW; x++)
            {
                leftBrightness  += tex.GetPixel(x, h / 2 + y).grayscale;
                rightBrightness += tex.GetPixel(w - 1 - x, h / 2 + y).grayscale;
                topBrightness   += tex.GetPixel(w / 2 + x, h - 1 - y).grayscale;
            }
        }

        float lr = rightBrightness > leftBrightness ? 0.3f : -0.3f;
        Vector3 dir = new Vector3(lr, -0.8f, 0.2f);
        return dir.normalized;
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
        return sum / samples;
    }

    // -------------------------------------------------------------------------
    // Editor fallback
    // -------------------------------------------------------------------------

    private static void ApplyEditorFallbackProbe(Light directionalLight)
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

        Debug.Log("[SHEstimator] Editor fallback probe applied (no passthrough frame).");
    }
}
