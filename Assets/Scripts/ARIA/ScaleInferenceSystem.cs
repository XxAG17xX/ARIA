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
    /// </summary>
    /// <param name="obj">The root GameObject of the spawned GLB.</param>
    /// <param name="targetHeightMetres">Height from Claude's placement JSON.</param>
    /// <param name="category">Object category (e.g. "lamp") for canonical lookup.</param>
    public void ApplyScale(GameObject obj, float targetHeightMetres, string category = null)
    {
        if (obj == null) return;

        // Use Claude's height. If it seems wrong, fall back to canonical.
        float targetH = ResolveTargetHeight(targetHeightMetres, category);

        // Get native bounding box before any scaling (localScale may already be non-1)
        Bounds native = ARIAOrchestrator.CalculateMeshBounds(obj);
        float  nativeH = native.size.y;

        if (nativeH < 0.0001f)
        {
            Debug.LogWarning($"[ScaleInference] \"{obj.name}\" has near-zero height — skipping scale.");
            return;
        }

        float scaleFactor = targetH / nativeH;

        // Clamp to sane range
        scaleFactor = Mathf.Clamp(scaleFactor, MinScale, MaxScale);

        // Sanity-check against room height (if known)
        if (roomHeightMetres > 0.1f)
        {
            float maxAllowedH = roomHeightMetres * 0.8f;
            if (targetH > maxAllowedH)
            {
                scaleFactor = (maxAllowedH / nativeH);
                scaleFactor = Mathf.Clamp(scaleFactor, MinScale, MaxScale);
                Debug.LogWarning($"[ScaleInference] \"{obj.name}\" clamped to 80% of room height.");
            }
        }

        obj.transform.localScale = Vector3.one * scaleFactor;

        // Resize any existing BoxCollider to new bounds
        ResizeCollider(obj);

        Debug.Log($"[ScaleInference] \"{obj.name}\": native={nativeH:F3}m, " +
                  $"target={targetH:F3}m, scale={scaleFactor:F3}");
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
