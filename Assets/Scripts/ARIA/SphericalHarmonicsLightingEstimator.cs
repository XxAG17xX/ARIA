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
    [Tooltip("Minimum brightness (0-1) for a pixel to be considered a light source. " +
             "Quest passthrough is often underexposed — use lower values (0.4-0.6).")]
    [SerializeField] private float brightThreshold = 0.5f;

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

    // Saved ARIA state so we can toggle back
    private SphericalHarmonicsL2 _ariaProbe;
    private Quaternion _ariaLightRotation;
    private float _ariaLightIntensity;
    private Color _ariaLightColor;
    private bool _ariaStateSaved;

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

        // Save ARIA state so toggle can restore it
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
            // Restore ARIA lighting — probe, directional light, detected lights
            if (_ariaStateSaved)
            {
                RenderSettings.ambientProbe = _ariaProbe;
                if (sceneDirectionalLight != null)
                {
                    sceneDirectionalLight.transform.rotation = _ariaLightRotation;
                    sceneDirectionalLight.intensity = _ariaLightIntensity;
                    sceneDirectionalLight.color = _ariaLightColor;
                }
            }
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
    public int DetectedLightCount => _detectedLightObjects.Count;

    /// <summary>Returns a human-readable summary of the last lighting estimation.</summary>
    public string GetLightingSummary()
    {
        if (_detectedLightObjects.Count == 0)
            return "No lights detected. Try lowering threshold or scanning again.";

        string summary = $"{_detectedLightObjects.Count} light(s) detected: ";
        foreach (var go in _detectedLightObjects)
        {
            if (go == null) continue;
            var light = go.GetComponent<Light>();
            if (light != null)
            {
                string colorName = light.color.r > light.color.b ? "warm" : "cool";
                summary += $"[{colorName} {light.intensity:F1}] ";
            }
        }
        return summary;
    }

    /// <summary>
    /// Adds a per-object directional light aimed from the nearest detected ceiling light
    /// toward the object. Creates realistic shadows matching the real room's light direction.
    /// Call after EstimateLighting() and after the object is placed.
    /// </summary>
    public void AddPerObjectLight(GameObject obj)
    {
        if (obj == null || _detectedLightObjects.Count == 0) return;

        // Find the nearest detected ceiling light to this object
        Vector3 objPos = obj.transform.position;
        GameObject nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var lightGO in _detectedLightObjects)
        {
            if (lightGO == null) continue;
            float dist = Vector3.Distance(lightGO.transform.position, objPos);
            if (dist < nearestDist) { nearestDist = dist; nearest = lightGO; }
        }

        if (nearest == null) return;

        // Create a directional light as child of the object, aimed FROM the ceiling light
        var localLightGO = new GameObject("ARIA_ObjectLight");
        localLightGO.transform.SetParent(obj.transform);
        localLightGO.transform.position = objPos + Vector3.up * 2f; // above the object

        // Point FROM the detected light TOWARD the object
        Vector3 lightDir = (objPos - nearest.transform.position).normalized;
        localLightGO.transform.rotation = Quaternion.LookRotation(lightDir);

        var light = localLightGO.AddComponent<Light>();
        light.type = LightType.Directional;

        // Match color from the detected ceiling light
        var sourceLightComp = nearest.GetComponent<Light>();
        if (sourceLightComp != null)
        {
            light.color = sourceLightComp.color;
            light.intensity = sourceLightComp.intensity * 0.5f; // softer than room light
        }
        else
        {
            light.color = new Color(1f, 0.9f, 0.7f); // warm default
            light.intensity = 0.8f;
        }

        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.5f;
        light.shadowBias = 0.05f;
        light.shadowNormalBias = 0.4f;
        // Limit shadow distance for performance on Quest
        light.shadowNearPlane = 0.1f;
        light.cullingMask = ~0; // light everything

        Debug.Log($"[SHEstimator] Per-object directional light on \"{obj.name}\" from ceiling light at {nearest.transform.position}");
    }

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
    // 4-photo room scan estimation (360° coverage)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimates lighting from 4 passthrough photos taken at different headings.
    /// Combines SH probes from all photos for full 360° ambient coverage.
    /// Detects lights across all photos for complete room light map.
    /// </summary>
    public void EstimateFromRoomScan(byte[][] photos, float[] headings, Light sceneDirectionalLight)
    {
        // Save default state
        if (!_defaultStateSaved && sceneDirectionalLight != null)
        {
            _defaultProbe = RenderSettings.ambientProbe;
            _defaultLightRotation = sceneDirectionalLight.transform.rotation;
            _defaultLightIntensity = sceneDirectionalLight.intensity;
            _defaultLightColor = sceneDirectionalLight.color;
            _defaultStateSaved = true;
        }

        // Clean up previous detected lights
        foreach (var go in _detectedLightObjects)
            if (go != null) Destroy(go);
        _detectedLightObjects.Clear();

        var combinedSH = new SphericalHarmonicsL2();
        combinedSH.Clear();
        int totalSamples = 0;
        Color totalColor = Color.black;
        Vector3 weightedLightDir = Vector3.zero;
        float totalBrightness = 0f;

        float cameraFOV = 60f; // approximate Quest 3 horizontal FOV per eye
        Camera cam = Camera.main;
        if (cam != null) cameraFOV = cam.fieldOfView;

        for (int i = 0; i < photos.Length; i++)
        {
            if (photos[i] == null || photos[i].Length == 0) continue;

            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(photos[i])) { Destroy(tex); continue; }

            float headingRad = headings[i] * Mathf.Deg2Rad;

            // Compute SH with world-space directions based on heading
            SphericalHarmonicsL2 photoSH = ComputeSHWithHeading(tex, headingRad, cameraFOV);

            // Accumulate SH coefficients
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    combinedSH[c, k] += photoSH[c, k];
            totalSamples++;

            // Accumulate average color
            Color avgCol = AverageFrameColor(tex);
            totalColor += avgCol;

            // Detect bright clusters in this photo → project to 3D world positions
            DetectAndSpawnRoomLightsWithHeading(tex, headingRad);

            // Weighted light direction from brightness distribution
            Vector3 photoDir = EstimateDirectionFromBrightnessWithHeading(tex, headingRad);
            float photoBrightness = avgCol.grayscale;
            weightedLightDir += photoDir * photoBrightness;
            totalBrightness += photoBrightness;

            Destroy(tex);
            Debug.Log($"[SHEstimator] Room scan photo {i}: heading={headings[i]:F0}°, brightness={photoBrightness:F2}");
        }

        if (totalSamples == 0)
        {
            Debug.LogWarning("[SHEstimator] No valid room scan photos — using fallback.");
            ApplyEditorFallbackProbe(sceneDirectionalLight);
            return;
        }

        // Average the SH coefficients
        float inv = 1f / totalSamples;
        for (int c = 0; c < 3; c++)
            for (int k = 0; k < 9; k++)
                combinedSH[c, k] *= inv;

        ApplySH(combinedSH);

        // Set directional light from brightness-weighted direction across all photos
        if (sceneDirectionalLight != null && totalBrightness > 0.01f)
        {
            Vector3 lightDir = (weightedLightDir / totalBrightness).normalized;
            if (lightDir.sqrMagnitude < 0.5f) lightDir = new Vector3(0f, -0.8f, 0.2f);
            sceneDirectionalLight.transform.rotation = Quaternion.LookRotation(lightDir);
            Color avgColor = totalColor / totalSamples;
            sceneDirectionalLight.color = Color.Lerp(Color.white, avgColor, 0.4f);
            // Keep intensity at least as bright as default (passthrough is often darker)
            sceneDirectionalLight.intensity = Mathf.Max(sceneDirectionalLight.intensity, 1.0f);
        }

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

        Debug.Log($"[SHEstimator] Room scan complete: {totalSamples} photos, {_detectedLightObjects.Count} lights detected.");
    }

    /// <summary>Compute SH coefficients with pixel directions rotated by camera heading.</summary>
    private static SphericalHarmonicsL2 ComputeSHWithHeading(Texture2D tex, float headingRad, float fovDeg)
    {
        float[,] shR = new float[3, 9];
        int sampleCount = 0;
        float halfFovRad = fovDeg * 0.5f * Mathf.Deg2Rad;

        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                float u = (col + 0.5f) / 4f;
                float v = (row + 0.5f) / 4f;

                // Map UV to direction relative to camera, then rotate by heading
                float localAzimuth = (u - 0.5f) * halfFovRad * 2f;
                float elevation = (v - 0.5f) * halfFovRad; // vertical

                float worldAzimuth = localAzimuth + headingRad;

                float cosEl = Mathf.Cos(elevation);
                float sinEl = Mathf.Sin(elevation);
                Vector3 dir = new Vector3(
                    cosEl * Mathf.Sin(worldAzimuth),
                    sinEl,
                    cosEl * Mathf.Cos(worldAzimuth)).normalized;

                Color pixel = tex.GetPixelBilinear(u, v);
                float[] channels = { pixel.r, pixel.g, pixel.b };
                float x = dir.x, y = dir.y, z = dir.z;

                for (int c = 0; c < 3; c++)
                {
                    float val = channels[c];
                    shR[c, 0] += val * 0.282095f;
                    shR[c, 1] += val * 0.488603f * y;
                    shR[c, 2] += val * 0.488603f * z;
                    shR[c, 3] += val * 0.488603f * x;
                    shR[c, 4] += val * 1.092548f * (x * y);
                    shR[c, 5] += val * 1.092548f * (y * z);
                    shR[c, 6] += val * 0.315392f * (3f * z * z - 1f);
                    shR[c, 7] += val * 1.092548f * (x * z);
                    shR[c, 8] += val * 0.546274f * (x * x - y * y);
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

    /// <summary>Detect bright clusters in a photo and spawn lights using heading for 3D projection.</summary>
    private void DetectAndSpawnRoomLightsWithHeading(Texture2D tex, float headingRad)
    {
        int gridSize = 16;
        Camera cam = Camera.main;
        if (cam == null) return;

        float roomHeight = 2.8f;
#if UNITY_ANDROID && !UNITY_EDITOR
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room != null)
        {
            var ceil = room.Anchors.FirstOrDefault(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING));
            if (ceil != null) roomHeight = ceil.transform.position.y;
        }
#endif

        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                float u = (gx + 0.5f) / gridSize;
                float v = (gy + 0.5f) / gridSize;
                Color pixel = tex.GetPixelBilinear(u, v);

                if (pixel.grayscale < brightThreshold) continue;
                if (_detectedLightObjects.Count >= maxDetectedLights) return;

                // Map UV + heading to world direction
                float fov = cam.fieldOfView;
                float localAz = (u - 0.5f) * fov * Mathf.Deg2Rad;
                float el = (v - 0.5f) * fov * Mathf.Deg2Rad;
                float worldAz = localAz + headingRad;

                Vector3 dir = new Vector3(
                    Mathf.Cos(el) * Mathf.Sin(worldAz),
                    Mathf.Sin(el),
                    Mathf.Cos(el) * Mathf.Cos(worldAz)).normalized;

                // Project to ceiling
                Ray ray = new Ray(cam.transform.position, dir);
                float t = (roomHeight - ray.origin.y) / ray.direction.y;
                Vector3 lightPos = t > 0f ? ray.origin + ray.direction * t
                                          : cam.transform.position + Vector3.up * roomHeight;

                // Check distance from existing detected lights
                bool tooClose = false;
                foreach (var existing in _detectedLightObjects)
                {
                    if (existing != null && Vector3.Distance(existing.transform.position, lightPos) < 1f)
                    { tooClose = true; break; }
                }
                if (tooClose) continue;

                var lightGO = new GameObject($"ARIA_DetectedLight_{_detectedLightObjects.Count}");
                lightGO.transform.position = lightPos;
                var light = lightGO.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = pixel;
                light.intensity = detectedLightIntensity * pixel.grayscale * 0.6f;
                light.range = detectedLightRange * 1.5f;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 0.4f;
                light.shadowBias = 0.05f;
                light.shadowNormalBias = 0.4f;

                _detectedLightObjects.Add(lightGO);
            }
        }
    }

    private static Vector3 EstimateDirectionFromBrightnessWithHeading(Texture2D tex, float headingRad)
    {
        int w = tex.width, h = tex.height;
        float leftB = 0f, rightB = 0f, topB = 0f, bottomB = 0f;
        int sw = Mathf.Max(1, w / 8), sh = Mathf.Max(1, h / 8);

        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                leftB   += tex.GetPixel(x, h / 2 + y).grayscale;
                rightB  += tex.GetPixel(w - 1 - x, h / 2 + y).grayscale;
                topB    += tex.GetPixel(w / 2 + x, h - 1 - y).grayscale;
                bottomB += tex.GetPixel(w / 2 + x, y).grayscale;
            }
        }

        // Horizontal bias rotated by heading
        float lr = (rightB - leftB) / Mathf.Max(rightB + leftB, 0.01f);
        float tb = (topB - bottomB) / Mathf.Max(topB + bottomB, 0.01f);

        float cosH = Mathf.Cos(headingRad), sinH = Mathf.Sin(headingRad);
        Vector3 dir = new Vector3(
            sinH * 0.5f + lr * cosH * 0.3f,
            -0.7f + tb * 0.3f,
            cosH * 0.5f - lr * sinH * 0.3f);
        return dir.normalized;
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

    [Tooltip("Boost factor for SH probe intensity. Passthrough images are often underexposed. " +
             "2.0 = double brightness. Adjust to match perceived room brightness.")]
    [SerializeField] private float probeIntensityBoost = 2.5f;

    private void ApplySH(SphericalHarmonicsL2 sh)
    {
        // Boost SH coefficients to compensate for underexposed passthrough
        if (probeIntensityBoost > 1.01f)
        {
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    sh[c, k] *= probeIntensityBoost;
        }
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
            light.intensity = detectedLightIntensity * cluster.brightness * 0.6f; // softer to avoid blobs
            light.range = detectedLightRange * 1.5f; // wider coverage
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.4f;
            light.shadowBias = 0.05f;
            light.shadowNormalBias = 0.4f;

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

    private void ApplyEditorFallbackProbe(Light directionalLight)
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
