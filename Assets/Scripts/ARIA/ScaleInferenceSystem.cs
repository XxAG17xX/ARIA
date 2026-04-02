// ScaleInferenceSystem.cs
// Computes mesh bounding-box height after GLTFast instantiation and scales the object
// so its real-world height matches the LLM-specified height in metres.
//
// STUB — full implementation in next session.

using UnityEngine;

public class ScaleInferenceSystem : MonoBehaviour
{
    /// <summary>
    /// Scales <paramref name="obj"/> so its bounding-box height equals <paramref name="targetHeightMetres"/>.
    /// </summary>
    public void ApplyScale(GameObject obj, float targetHeightMetres)
    {
        // TODO: compute Renderer bounds across all children, derive scale factor, apply
        Debug.Log($"[ScaleInference] Scale \"{obj.name}\" to {targetHeightMetres} m (stub)");
    }
}
