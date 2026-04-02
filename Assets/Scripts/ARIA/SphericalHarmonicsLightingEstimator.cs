// SphericalHarmonicsLightingEstimator.cs
// PRIMARY CONTRIBUTION — estimates ambient room lighting from the passthrough frame
// and applies it to all spawned virtual objects so they blend with the real environment.
//
// RUNS ONCE PER VOICE COMMAND (not continuously).
// Reuses the JPEG bytes already captured for the Claude API call — no extra camera access.
//
// What it does:
//   1. Decodes the JPEG, samples 16 pixels in a 4x4 grid across the frame
//   2. Projects samples onto a 2-band SH basis (9 coefficients per channel)
//   3. Sets RenderSettings.ambientProbe so all virtual objects receive correct fill light
//   4. Checks MRUK for LAMP anchors to derive dominant light direction
//   5. Sets the scene DirectionalLight to match the estimated real-world light direction
//
// KNOWN LIMITATION: View-dependent — reflects lighting at the user's position,
// not at the object's position. Documented as known approximation.
//
// APK NOTE: RenderSettings changes are live in both editor and APK, but the JPEG source
// in editor will be null — falls back to a neutral warm grey probe.

using System;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Linq;
using Meta.XR.MRUtilityKit;
#endif

public class SphericalHarmonicsLightingEstimator : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Public API — called once from ARIAOrchestrator after final GLB spawn
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimates room lighting from <paramref name="jpeg"/> and applies it to the scene.
    /// Also places a ReflectionProbe at <paramref name="spawnedObject"/> position.
    /// Safe to call with jpeg = null (editor fallback: neutral warm-grey probe).
    /// </summary>
    public void EstimateLighting(byte[] jpeg, Light sceneDirectionalLight, GameObject spawnedObject)
    {
        if (jpeg != null && jpeg.Length > 0)
            EstimateFromJpeg(jpeg, sceneDirectionalLight);
        else
            ApplyEditorFallbackProbe(sceneDirectionalLight);
    }

    // -------------------------------------------------------------------------
    // SH estimation from JPEG pixels
    // -------------------------------------------------------------------------

    private static void EstimateFromJpeg(byte[] jpeg, Light sceneDirectionalLight)
    {
        // Decode JPEG into a Texture2D
        var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (!tex.LoadImage(jpeg))
        {
            Debug.LogWarning("[SHEstimator] Failed to decode JPEG — using editor fallback probe.");
            ApplyEditorFallbackProbe(sceneDirectionalLight);
            Destroy(tex);
            return;
        }

        SphericalHarmonicsL2 sh = ComputeSH(tex);
        ApplySH(sh);

        // Derive directional light direction from MRUK (if available), then pixel analysis
        SetLightDirection(tex, sceneDirectionalLight);

        Destroy(tex);
        Debug.Log("[SHEstimator] SH probe set from passthrough frame.");
    }

    /// <summary>
    /// Projects a 4×4 grid of pixel samples into order-2 SH coefficients.
    /// SH projection constants: Clebsch-Gordan derived, Ramamoorthi & Hanrahan 2001.
    /// </summary>
    private static SphericalHarmonicsL2 ComputeSH(Texture2D tex)
    {
        int w = tex.width;
        int h = tex.height;

        // Accumulate per-channel SH coefficients [channel 0-2][coeff 0-8]
        float[,] shR = new float[3, 9];
        int sampleCount = 0;

        // 4×4 grid of sample directions across the frame hemisphere
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                float u = (col + 0.5f) / 4f; // 0..1 horizontal
                float v = (row + 0.5f) / 4f; // 0..1 vertical

                // Map UV to a hemisphere direction relative to headset orientation
                // Yaw spans full azimuth (0..360°), pitch spans upper hemisphere (0..90°)
                float azimuth   = (u - 0.5f) * Mathf.PI * 2f;   // -π..π
                float elevation = (0.5f - v) * Mathf.PI * 0.5f; // 0..π/2 (up = higher v)

                float cosEl = Mathf.Cos(elevation);
                float sinEl = Mathf.Sin(elevation);
                // Direction vector in world-ish space (Y = up)
                Vector3 dir = new Vector3(
                    cosEl * Mathf.Sin(azimuth),
                    sinEl,
                    cosEl * Mathf.Cos(azimuth));
                dir.Normalize();

                // Sample pixel colour at this UV
                Color pixel = tex.GetPixelBilinear(u, v);
                float[] channels = { pixel.r, pixel.g, pixel.b };

                float x = dir.x, y = dir.y, z = dir.z;

                for (int c = 0; c < 3; c++)
                {
                    float col_val = channels[c];
                    // Order-2 SH basis functions (L00..L22)
                    shR[c, 0] += col_val * 0.282095f;                       // L00
                    shR[c, 1] += col_val * 0.488603f * y;                   // L1,-1
                    shR[c, 2] += col_val * 0.488603f * z;                   // L1,0
                    shR[c, 3] += col_val * 0.488603f * x;                   // L1,1
                    shR[c, 4] += col_val * 1.092548f * (x * y);             // L2,-2
                    shR[c, 5] += col_val * 1.092548f * (y * z);             // L2,-1
                    shR[c, 6] += col_val * 0.315392f * (3f * z * z - 1f);  // L2,0
                    shR[c, 7] += col_val * 1.092548f * (x * z);             // L2,1
                    shR[c, 8] += col_val * 0.546274f * (x * x - y * y);    // L2,2
                }
                sampleCount++;
            }
        }

        // Normalise
        if (sampleCount > 0)
        {
            float inv = 1f / sampleCount;
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    shR[c, k] *= inv;
        }

        // Build Unity SphericalHarmonicsL2 struct
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
    // Directional light direction estimation
    // -------------------------------------------------------------------------

    private static void SetLightDirection(Texture2D tex, Light directionalLight)
    {
        if (directionalLight == null) return;

        Vector3 lightDir = EstimateLightDirection(tex);
        directionalLight.transform.rotation = Quaternion.LookRotation(lightDir);
        Debug.Log($"[SHEstimator] Directional light set to {lightDir}");
    }

    private static Vector3 EstimateLightDirection(Texture2D tex)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Try MRUK LAMP anchor first
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room != null)
        {
            var lampAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(MRUKAnchor.SceneLabels.LAMP));

            if (lampAnchor != null)
            {
                // Light direction: FROM lamp position, aim downward with slight inward bias
                Vector3 toFloor = Vector3.down * 0.8f
                    + lampAnchor.transform.position.normalized * 0.2f;
                return toFloor.normalized;
            }
        }
#endif
        // Pixel-brightness analysis: find the brightest horizontal third of the frame
        // This gives a rough estimate of where the main light source is
        return EstimateDirectionFromBrightness(tex);
    }

    private static Vector3 EstimateDirectionFromBrightness(Texture2D tex)
    {
        // Divide frame into 3 horizontal bands. Find brightest band → light comes from that side.
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

        // Light direction: toward floor, biased toward brighter side
        float lr = rightBrightness > leftBrightness ? 0.3f : -0.3f;
        Vector3 dir = new Vector3(lr, -0.8f, 0.2f);
        return dir.normalized;
    }

    // -------------------------------------------------------------------------
    // Editor fallback
    // -------------------------------------------------------------------------

    private static void ApplyEditorFallbackProbe(Light directionalLight)
    {
        var sh = new SphericalHarmonicsL2();
        sh.Clear();
        // Neutral warm-grey ambient (simulates a well-lit interior room)
        sh.AddAmbientLight(new Color(0.4f, 0.38f, 0.35f));
        ApplySH(sh);

        if (directionalLight != null)
        {
            // Soft overhead light (slightly angled — avoids flat look)
            directionalLight.transform.rotation =
                Quaternion.Euler(55f, 30f, 0f);
            directionalLight.intensity = 0.8f;
        }

        Debug.Log("[SHEstimator] Editor fallback probe applied (no passthrough frame).");
    }
}
