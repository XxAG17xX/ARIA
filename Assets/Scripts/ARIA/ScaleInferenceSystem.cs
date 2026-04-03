// ScaleInferenceSystem.cs
// Scales a GLTFast-spawned object so its real-world height matches the LLM-specified
// height in metres. Uses a canonical height dictionary keyed by object category.
// After scaling, resizes the BoxCollider to match new bounds.

using System.Collections.Generic;
using UnityEngine;

public class ScaleInferenceSystem : MonoBehaviour
{
    [Tooltip("Optional: actual room height in metres, read from MRUK at runtime. " +
             "Used as sanity-check ceiling. If 0, no ceiling check is applied.")]
    public float roomHeightMetres = 0f;

    // Canonical real-world heights (metres) keyed by lowercase object category.
    // Used when Claude's height_metres seems unreasonable or as a cross-check.
    private static readonly Dictionary<string, float> CanonicalHeights = new()
    {
        { "lamp",       1.5f  },
        { "floor lamp", 1.5f  },
        { "desk lamp",  0.45f },
        { "chair",      0.9f  },
        { "armchair",   0.9f  },
        { "table",      0.75f },
        { "desk",       0.75f },
        { "bookshelf",  1.8f  },
        { "bookcase",   1.8f  },
        { "plant",      0.6f  },
        { "sofa",       0.85f },
        { "couch",      0.85f },
        { "bed",        0.5f  },  // mattress height
        { "monitor",    0.45f },
        { "tv",         0.6f  },
        { "television", 0.6f  },
        { "vase",       0.3f  },
        { "mug",        0.1f  },
        { "cup",        0.1f  },
        { "book",       0.03f },
        { "clock",      0.25f },
        { "painting",   0.6f  },
        { "picture",    0.5f  },
        { "candle",     0.15f },
        { "lantern",    0.3f  },
        { "rug",        0.02f },
        { "carpet",     0.02f },
        { "pillow",     0.15f },
        { "cushion",    0.15f },
        { "shelf",      0.3f  },
        { "stool",      0.65f },
    };

    private const float MinScale = 0.05f;
    private const float MaxScale = 3.0f;

    // -------------------------------------------------------------------------
    // Public API — called from ARIAOrchestrator after GLTFast instantiation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scales <paramref name="obj"/> so its bounding-box height equals
    /// <paramref name="targetHeightMetres"/>, then resizes any BoxCollider.
    /// Optionally uses width/depth targets for proportional (non-uniform) scaling.
    /// </summary>
    public void ApplyScale(GameObject obj, float targetHeightMetres, string category = null,
                           float targetWidthMetres = 0f, float targetDepthMetres = 0f)
    {
        if (obj == null) return;

        // Use Claude's height. If it seems wrong, fall back to canonical.
        float targetH = ResolveTargetHeight(targetHeightMetres, category);

        // Get native bounding box before any scaling
        Bounds native = ARIAOrchestrator.CalculateMeshBounds(obj);
        float  nativeH = native.size.y;
        float  nativeW = native.size.x;
        float  nativeD = native.size.z;

        if (nativeH < 0.0001f)
        {
            Debug.LogWarning($"[ScaleInference] \"{obj.name}\" has near-zero height — skipping scale.");
            return;
        }

        // Determine scale per axis
        float scaleY = targetH / nativeH;
        float scaleX = scaleY; // default: uniform from height
        float scaleZ = scaleY;

        // If Claude provided width/depth, use non-uniform scaling
        bool proportional = targetWidthMetres > 0.01f || targetDepthMetres > 0.01f;
        if (proportional)
        {
            if (targetWidthMetres > 0.01f && nativeW > 0.0001f)
                scaleX = targetWidthMetres / nativeW;
            if (targetDepthMetres > 0.01f && nativeD > 0.0001f)
                scaleZ = targetDepthMetres / nativeD;
        }

        // Clamp each axis
        scaleX = Mathf.Clamp(scaleX, MinScale, MaxScale);
        scaleY = Mathf.Clamp(scaleY, MinScale, MaxScale);
        scaleZ = Mathf.Clamp(scaleZ, MinScale, MaxScale);

        // Sanity-check against room height (if known)
        if (roomHeightMetres > 0.1f)
        {
            float maxAllowedH = roomHeightMetres * 0.8f;
            if (targetH > maxAllowedH)
            {
                scaleY = maxAllowedH / nativeH;
                scaleY = Mathf.Clamp(scaleY, MinScale, MaxScale);
                Debug.LogWarning($"[ScaleInference] \"{obj.name}\" clamped to 80% of room height.");
            }
        }

        obj.transform.localScale = proportional
            ? new Vector3(scaleX, scaleY, scaleZ)
            : Vector3.one * scaleY;

        // Resize any existing BoxCollider to new bounds
        ResizeCollider(obj);

        string scaleStr = proportional
            ? $"scale=({scaleX:F3}, {scaleY:F3}, {scaleZ:F3})"
            : $"scale={scaleY:F3}";
        Debug.Log($"[ScaleInference] \"{obj.name}\": native=({nativeW:F3}×{nativeH:F3}×{nativeD:F3})m, " +
                  $"target=({targetWidthMetres:F2}×{targetH:F2}×{targetDepthMetres:F2})m, {scaleStr}");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static float ResolveTargetHeight(float llmHeight, string category)
    {
        // Use LLM value if it's plausible (between 1 cm and 3 m)
        if (llmHeight > 0.01f && llmHeight < 3.0f)
            return llmHeight;

        // Fall back to canonical
        if (category != null)
        {
            string key = category.ToLowerInvariant();
            if (CanonicalHeights.TryGetValue(key, out float canonical))
            {
                Debug.LogWarning($"[ScaleInference] LLM height {llmHeight} out of range — " +
                                 $"using canonical {canonical}m for \"{key}\".");
                return canonical;
            }

            // Partial match (e.g. "modern floor lamp" → "lamp")
            foreach (var kv in CanonicalHeights)
            {
                if (key.Contains(kv.Key) || kv.Key.Contains(key))
                    return kv.Value;
            }
        }

        // Last resort: 1 metre
        Debug.LogWarning($"[ScaleInference] No canonical height for \"{category}\" — defaulting to 1m.");
        return 1.0f;
    }

    private static void ResizeCollider(GameObject obj)
    {
        var col = obj.GetComponent<BoxCollider>();
        if (col == null) return;

        Bounds b  = ARIAOrchestrator.CalculateMeshBounds(obj);
        col.center = obj.transform.InverseTransformPoint(b.center);
        col.size   = b.size; // world-space size, but InverseTransformPoint handles localScale
    }
}
