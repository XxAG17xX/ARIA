// BillboardFaceCamera.cs
// Simple billboard that rotates a world-space UI to always face the main camera.
// Attach to any GameObject (e.g., confirm button canvas on light spheres).

using UnityEngine;

public class BillboardFaceCamera : MonoBehaviour
{
    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // Face the camera — look in the opposite direction of the camera's forward
        Vector3 lookDir = transform.position - cam.transform.position;
        if (lookDir.sqrMagnitude < 0.001f) return;
        transform.rotation = Quaternion.LookRotation(lookDir.normalized);
    }
}
