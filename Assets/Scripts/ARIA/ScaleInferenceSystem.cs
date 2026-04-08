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

    // Canonical real-world dimensions (width, height, depth) in metres.
    // Used by FitToSurface to cap object size to real-world proportions.
    private static readonly Dictionary<string, Vector3> CanonicalDimensions = new()
    {
        { "bed",        new Vector3(1.4f,  0.5f,  2.0f) },
        { "lamp",       new Vector3(0.3f,  1.5f,  0.3f) },
        { "floor lamp", new Vector3(0.3f,  1.5f,  0.3f) },
        { "desk lamp",  new Vector3(0.2f,  0.45f, 0.2f) },
        { "chair",      new Vector3(0.5f,  0.9f,  0.5f) },
        { "armchair",   new Vector3(0.8f,  0.9f,  0.8f) },
        { "table",      new Vector3(1.2f,  0.75f, 0.8f) },
        { "desk",       new Vector3(1.2f,  0.75f, 0.6f) },
        { "bookshelf",  new Vector3(0.8f,  1.8f,  0.3f) },
        { "bookcase",   new Vector3(0.8f,  1.8f,  0.3f) },
        { "plant",      new Vector3(0.3f,  0.6f,  0.3f) },
        { "sofa",       new Vector3(2.0f,  0.85f, 0.9f) },
        { "couch",      new Vector3(2.0f,  0.85f, 0.9f) },
        { "monitor",    new Vector3(0.6f,  0.45f, 0.2f) },
        { "tv",         new Vector3(1.0f,  0.6f,  0.1f) },
        { "television", new Vector3(1.0f,  0.6f,  0.1f) },
        { "painting",   new Vector3(0.8f,  0.6f,  0.05f) },
        { "wall_art",   new Vector3(0.8f,  0.6f,  0.05f) },
        { "picture",    new Vector3(0.5f,  0.5f,  0.05f) },
        { "clock",      new Vector3(0.25f, 0.25f, 0.05f) },
        { "mirror",     new Vector3(0.5f,  0.8f,  0.05f) },
        { "shelf",      new Vector3(0.8f,  0.3f,  0.25f) },
        { "poster",     new Vector3(0.6f,  0.9f,  0.02f) },
        { "frame",      new Vector3(0.4f,  0.5f,  0.05f) },
        { "vase",       new Vector3(0.15f, 0.3f,  0.15f) },
        { "mug",        new Vector3(0.08f, 0.1f,  0.08f) },
        { "cup",        new Vector3(0.08f, 0.1f,  0.08f) },
        { "book",       new Vector3(0.15f, 0.03f, 0.22f) },
        { "candle",     new Vector3(0.06f, 0.15f, 0.06f) },
        { "lantern",    new Vector3(0.15f, 0.3f,  0.15f) },
        { "rug",        new Vector3(2.0f,  0.02f, 1.5f) },
        { "carpet",     new Vector3(2.5f,  0.02f, 2.0f) },
        { "pillow",     new Vector3(0.4f,  0.15f, 0.4f) },
        { "cushion",    new Vector3(0.4f,  0.15f, 0.4f) },
        { "stool",      new Vector3(0.35f, 0.65f, 0.35f) },
    };

    /// <summary>
    /// Returns canonical real-world (width, height, depth) for a category.
    /// Supports exact match and partial substring matching.
    /// Returns Vector3.zero if no match found.
    /// </summary>
    public static Vector3 GetCanonicalDimensions(string category)
    {
        if (string.IsNullOrEmpty(category)) return Vector3.zero;
        string key = category.ToLowerInvariant();

        if (CanonicalDimensions.TryGetValue(key, out var dims))
            return dims;

        // Partial match (e.g. "modern floor lamp" → "floor lamp" → "lamp")
        foreach (var kv in CanonicalDimensions)
        {
            if (key.Contains(kv.Key) || kv.Key.Contains(key))
                return kv.Value;
        }
        return Vector3.zero;
    }

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

        // ALWAYS uniform scaling — use height as the reference, never squash/stretch
        // Claude's width/depth are informational but we only scale proportionally
        scaleY = Mathf.Clamp(scaleY, MinScale, MaxScale);

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

        obj.transform.localScale = Vector3.one * scaleY; // uniform — no squashing ever

        // Resize any existing BoxCollider to new bounds
        ResizeCollider(obj);

        Debug.Log($"[ScaleInference] \"{obj.name}\": native=({nativeW:F3}×{nativeH:F3}×{nativeD:F3})m, " +
                  $"target h={targetH:F2}m, uniform scale={scaleY:F3}");
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
        var ls = obj.transform.localScale;
        col.size = new Vector3(
            ls.x > 0.0001f ? b.size.x / ls.x : b.size.x,
            ls.y > 0.0001f ? b.size.y / ls.y : b.size.y,
            ls.z > 0.0001f ? b.size.z / ls.z : b.size.z);
    }
}
