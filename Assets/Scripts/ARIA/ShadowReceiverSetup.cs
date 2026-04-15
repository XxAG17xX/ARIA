// ShadowReceiverSetup.cs — creates the PTRL material for shadow rendering
// finds Meta's HighlightsAndShadows shader (from the MRUK package) and makes
// a material from it. the orchestrator uses this material on invisible floor/wall
// planes so Unity light shadows show up on top of the passthrough camera feed.
// has multiple fallback paths in case the shader isn't found (Resources, package path, etc).

using System.Linq;
using UnityEngine;

using Meta.XR.MRUtilityKit;

public class ShadowReceiverSetup : MonoBehaviour
{
    [Header("PTRL Shader Settings")]
    [Tooltip("Shadow darkness (0 = invisible, 1 = fully black).")]
    [Range(0f, 1f)]
    [SerializeField] private float shadowIntensity = 0.7f;

    [Tooltip("How strongly additional lights create highlights on real surfaces.")]
    [Range(0f, 1f)]
    [SerializeField] private float highlightAttenuation = 0.8f;

    [Tooltip("Opacity of highlight effect on real surfaces.")]
    [Range(0f, 1f)]
    [SerializeField] private float highlightOpacity = 0.3f;

    [Tooltip("Depth bias for environment occlusion (prevents z-fighting).")]
    [Range(0f, 0.2f)]
    [SerializeField] private float depthBias = 0.06f;

    [Tooltip("The EffectMesh GameObject. If null, searches automatically.")]
    [SerializeField] private GameObject effectMeshObject;

    private Material _ptrlMaterial;

    /// <summary>Returns the PTRL material for use by shadow floor. Call Configure() first.</summary>
    public Material PTRLMaterial => _ptrlMaterial;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void Configure()
    {
        // Only create the PTRL material — do NOT apply to EffectMesh on startup.
        // The wireframe (RoomBoxEffects) must stay visible.
        // PTRL material is used later by TogglePTRL() on a separate shadow floor.
        CreatePTRLMaterial();
    }

    /// <summary>
    /// Re-applies material to newly spawned EffectMesh renderers.
    /// Call after MRUK loads new room data.
    /// </summary>
    public void Reconfigure()
    {
        // Re-create material if needed — still don't touch EffectMesh
        CreatePTRLMaterial();
    }

    // -------------------------------------------------------------------------
    // PTRL material creation
    // -------------------------------------------------------------------------

    private void CreatePTRLMaterial()
    {
        if (_ptrlMaterial != null) return;

        // Try to load Meta's pre-made TransparentSceneAnchor material (exact material from PTRL sample)
        var ptrlMat = Resources.Load<Material>("TransparentSceneAnchor");
        if (ptrlMat == null)
        {
            // Try loading from package path
            ptrlMat = UnityEngine.Resources.FindObjectsOfTypeAll<Material>()
                .FirstOrDefault(m => m.name == "TransparentSceneAnchor");
        }
        if (ptrlMat != null)
        {
            _ptrlMaterial = new Material(ptrlMat); // clone it
            _ptrlMaterial.SetFloat("_ShadowIntensity", shadowIntensity);
            _ptrlMaterial.SetFloat("_HighLightAttenuation", highlightAttenuation);
            _ptrlMaterial.SetFloat("_HighlightOpacity", highlightOpacity);
            _ptrlMaterial.SetFloat("_EnvironmentDepthBias", depthBias);
            Debug.Log("[ShadowReceiver] Using Meta's TransparentSceneAnchor material.");
            return;
        }

        // Fallback: create from shader
        var shader = Shader.Find("Meta/MRUK/Scene/HighlightsAndShadows");
        if (shader != null)
        {
            _ptrlMaterial = new Material(shader);
            _ptrlMaterial.SetFloat("_ShadowIntensity", shadowIntensity);
            _ptrlMaterial.SetFloat("_HighLightAttenuation", highlightAttenuation);
            _ptrlMaterial.SetFloat("_HighlightOpacity", highlightOpacity);
            _ptrlMaterial.SetFloat("_EnvironmentDepthBias", depthBias);
            Debug.Log("[ShadowReceiver] Created PTRL material from shader.");
            return;
        }

        // Fallback: try our original shader
        shader = Shader.Find("ARIA/ShadowReceiver");
        if (shader != null)
        {
            _ptrlMaterial = new Material(shader);
            _ptrlMaterial.SetFloat("_ShadowStrength", shadowIntensity);
            Debug.LogWarning("[ShadowReceiver] PTRL shader not found, using ARIA fallback.");
            return;
        }

        Debug.LogError("[ShadowReceiver] No shadow shader found. Ensure MRUK package is installed.");
    }

    // -------------------------------------------------------------------------
    // EffectMesh configuration (Quest APK)
    // -------------------------------------------------------------------------


    private void ConfigureEffectMesh()
    {
        if (_ptrlMaterial == null) return;

        if (effectMeshObject != null)
        {
            var renderers = effectMeshObject.GetComponentsInChildren<Renderer>(true);
            ApplyToRenderers(renderers);
            Debug.Log($"[ShadowReceiver] PTRL applied to {renderers.Length} assigned EffectMesh renderer(s).");
            return;
        }

        var effectMesh = FindFirstObjectByType<EffectMesh>();
        if (effectMesh != null)
        {
            // Apply to existing renderers
            var renderers = effectMesh.GetComponentsInChildren<Renderer>(true);
            ApplyToRenderers(renderers);
            Debug.Log($"[ShadowReceiver] PTRL applied to {renderers.Length} EffectMesh renderer(s).");

            // Also set the material on the EffectMesh component so future spawned surfaces use it
            effectMesh.MeshMaterial = _ptrlMaterial;
            Debug.Log("[ShadowReceiver] Set PTRL material on EffectMesh component for future surfaces.");
        }
        else
        {
            Debug.LogWarning("[ShadowReceiver] No EffectMesh found in scene.");
        }
    }


    // -------------------------------------------------------------------------
    // Editor fallback
    // -------------------------------------------------------------------------

    private void ConfigureEditorFallback()
    {
        if (_ptrlMaterial == null) return;

        var existing = GameObject.Find("ARIA_ShadowFloor");
        if (existing != null)
        {
            ApplyToRenderers(existing.GetComponentsInChildren<Renderer>());
            return;
        }

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "ARIA_ShadowFloor";
        floor.transform.localScale = new Vector3(1f, 1f, 1f);
        floor.transform.position = Vector3.zero;

        var col = floor.GetComponent<Collider>();
        if (col != null) Destroy(col);

        ApplyToRenderers(floor.GetComponentsInChildren<Renderer>());
        Debug.Log("[ShadowReceiver] Editor fallback: created ARIA_ShadowFloor with PTRL material.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void ApplyToRenderers(Renderer[] renderers)
    {
        foreach (var r in renderers)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = true;
            r.sharedMaterial = _ptrlMaterial;
        }
    }
}
