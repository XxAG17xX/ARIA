// ShadowReceiverSetup.cs
// Configures MRUK EffectMesh surfaces (floor, walls) to receive shadows from virtual objects.
// Uses the custom ARIA/ShadowReceiver shader: transparent everywhere, dark overlay in shadows.
//
// Call Configure() once after the scene/room is loaded.
// In editor, creates a simple floor plane as a fallback shadow receiver for testing.
//
// Scene setup required (done manually in Unity):
//   1. Create a child GameObject under MRUK or ARIA_Manager.
//   2. Add EffectMesh component, set Labels = FLOOR | WALL_FACE, GenerateOnStart = true.
//   3. Assign this ShadowReceiverSetup script's shadowReceiverMaterial to the EffectMesh
//      renderer, or leave it null to let Configure() create the material automatically.

using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using Meta.XR.MRUtilityKit;
#endif

public class ShadowReceiverSetup : MonoBehaviour
{
    [Tooltip("The ARIA/ShadowReceiver material. If null, created automatically from the shader.")]
    [SerializeField] private Material shadowReceiverMaterial;

    [Tooltip("How dark the shadow overlay is (0 = invisible, 1 = black).")]
    [Range(0f, 1f)]
    [SerializeField] private float shadowStrength = 0.65f;

    [Tooltip("The EffectMesh GameObject that MRUK uses to generate room surface meshes. " +
             "If null, Configure() will search for one.")]
    [SerializeField] private GameObject effectMeshObject;

    private Material _runtimeMaterial;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies the shadow-receiver material to all MRUK EffectMesh surface renderers.
    /// Call once from ARIAOrchestrator.Start() or from MRUK's SceneLoadedEvent.
    /// </summary>
    public void Configure()
    {
        EnsureMaterial();

#if UNITY_ANDROID && !UNITY_EDITOR
        ConfigureEffectMesh();
#else
        ConfigureEditorFallback();
#endif
    }

    // -------------------------------------------------------------------------
    // MRUK / EffectMesh configuration (Quest APK)
    // -------------------------------------------------------------------------

#if UNITY_ANDROID && !UNITY_EDITOR
    private void ConfigureEffectMesh()
    {
        // Find EffectMesh component — either the assigned object or search in scene
        MRUKAnchor effectMeshComp = null;

        if (effectMeshObject != null)
        {
            // Try the assigned object first
            var renderers = effectMeshObject.GetComponentsInChildren<Renderer>(true);
            ApplyToRenderers(renderers);
            Debug.Log($"[ShadowReceiver] Applied to {renderers.Length} renderer(s) on assigned EffectMesh.");
            return;
        }

        // Search for EffectMesh in the scene
        var effectMesh = FindFirstObjectByType<Meta.XR.MRUtilityKit.EffectMesh>();
        if (effectMesh != null)
        {
            var renderers = effectMesh.GetComponentsInChildren<Renderer>(true);
            ApplyToRenderers(renderers);
            Debug.Log($"[ShadowReceiver] Applied to {renderers.Length} EffectMesh renderer(s).");
        }
        else
        {
            Debug.LogWarning("[ShadowReceiver] No EffectMesh found in scene. " +
                "Add an EffectMesh component to a GameObject with FLOOR/WALL_FACE labels.");
        }
    }
#endif

    // -------------------------------------------------------------------------
    // Editor fallback — simple floor plane for shadow testing
    // -------------------------------------------------------------------------

    private void ConfigureEditorFallback()
    {
        // Check if a fallback plane already exists
        var existing = GameObject.Find("ARIA_ShadowFloor");
        if (existing != null)
        {
            ApplyToRenderers(existing.GetComponentsInChildren<Renderer>());
            return;
        }

        // Create a flat plane at y=0 to catch shadows in editor
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "ARIA_ShadowFloor";
        floor.transform.localScale = new Vector3(1f, 1f, 1f); // 10x10m (plane default)
        floor.transform.position   = Vector3.zero;

        // Remove default collider (we only want the visual shadow effect)
        var col = floor.GetComponent<Collider>();
        if (col != null) Destroy(col);

        ApplyToRenderers(floor.GetComponentsInChildren<Renderer>());
        Debug.Log("[ShadowReceiver] Editor fallback: created ARIA_ShadowFloor plane.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void ApplyToRenderers(Renderer[] renderers)
    {
        foreach (var r in renderers)
        {
            r.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows     = true;
            r.sharedMaterial     = _runtimeMaterial;
        }
    }

    private void EnsureMaterial()
    {
        if (_runtimeMaterial != null) return;

        if (shadowReceiverMaterial != null)
        {
            // Clone the assigned material so we can modify strength without affecting the asset
            _runtimeMaterial = new Material(shadowReceiverMaterial);
        }
        else
        {
            // Create from shader at runtime
            var shader = Shader.Find("ARIA/ShadowReceiver");
            if (shader == null)
            {
                Debug.LogError("[ShadowReceiver] ARIA/ShadowReceiver shader not found. " +
                    "Ensure Assets/Shaders/ARIA/ShadowReceiver.shader is in the project.");
                return;
            }
            _runtimeMaterial = new Material(shader);
        }

        _runtimeMaterial.SetFloat("_ShadowStrength", shadowStrength);
        _runtimeMaterial.SetColor("_ShadowColor", Color.black);
    }
}
