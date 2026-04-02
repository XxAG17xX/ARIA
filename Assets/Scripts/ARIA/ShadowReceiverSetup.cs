// ShadowReceiverSetup.cs
// Configures the MRUK scene mesh to receive shadows from spawned objects.
// Uses an occlusion shader at zero opacity — mesh is invisible but active as a shadow surface.
// Shadows fall in the direction estimated by SphericalHarmonicsLightingEstimator.
//
// STUB — full implementation in next session.

using UnityEngine;

public class ShadowReceiverSetup : MonoBehaviour
{
    [SerializeField] private Material shadowReceiverMaterial;

    /// <summary>
    /// Applies the shadow-receiver material to the MRUK scene mesh.
    /// Call once after the MRUK room is loaded.
    /// </summary>
    public void Configure()
    {
        // TODO: find MRUK scene mesh GameObject, set shadowReceiverMaterial,
        //       set ShadowCastingMode.Off + receiveShadows = true
        Debug.Log("[ShadowReceiver] Configure (stub)");
    }
}
