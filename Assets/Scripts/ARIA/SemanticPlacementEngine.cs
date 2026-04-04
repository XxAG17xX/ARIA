// SemanticPlacementEngine.cs
// Places spawned objects at correct real-world positions using MRUK anchor data.
// Maps Claude's surface_label to MRUKAnchor.SceneLabels, finds matching anchors,
// and uses collision-aware placement to avoid overlapping real-world objects.
//
// Placement strategy per surface type:
//   FLOOR   — raycast from user gaze onto floor, validate clear spot with Physics.CheckBox
//   WALL    — find best wall via gaze direction, use GetBestPoseFromRaycast for alignment
//   TABLE   — place on top of volume anchor, offset to nearest clear edge
//   CEILING — snap to ceiling anchor
//   COUCH   — place on top of couch volume
//
// Falls back to 1.5m in front of user's camera in editor (no MRUK available).

using System.Linq;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using Meta.XR.MRUtilityKit;
#endif

public class SemanticPlacementEngine : MonoBehaviour
{
    [Tooltip("How far to offset objects from wall surfaces (metres).")]
    [SerializeField] private float wallOffset = 0.05f;

    [Tooltip("Minimum clear radius around a floor spawn point (metres).")]
    [SerializeField] private float floorClearanceRadius = 0.3f;

    [Tooltip("Max attempts to find a collision-free position before accepting best effort.")]
    [SerializeField] private int maxPlacementAttempts = 30;

    [Tooltip("How far apart to space multiple objects on the same surface (metres).")]
    [SerializeField] private float objectSpacing = 0.4f;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Positions <paramref name="obj"/> on the surface indicated by <paramref name="surfaceLabel"/>.
    /// Valid labels: FLOOR, WALL_FACE, CEILING, TABLE, COUCH, OTHER (defaults to FLOOR).
    /// </summary>
    public void Place(GameObject obj, string surfaceLabel)
    {
        if (obj == null) return;

        string label = (surfaceLabel ?? "FLOOR").ToUpperInvariant();

#if UNITY_ANDROID && !UNITY_EDITOR
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room != null)
        {
            PlaceWithMRUK(obj, label, room);
            return;
        }
#endif
        PlaceEditorFallback(obj, label);
    }

    // -------------------------------------------------------------------------
    // MRUK placement (Quest APK) — collision-aware
    // -------------------------------------------------------------------------

#if UNITY_ANDROID && !UNITY_EDITOR
    private void PlaceWithMRUK(GameObject obj, string label, MRUKRoom room)
    {
        switch (label)
        {
            case "FLOOR":
                PlaceOnFloor(obj, room);
                break;
            case "WALL_FACE":
                PlaceOnWall(obj, room);
                break;
            case "CEILING":
                PlaceOnCeiling(obj, room);
                break;
            case "TABLE":
                PlaceOnVolumeSurface(obj, room, MRUKAnchor.SceneLabels.TABLE);
                break;
            case "COUCH":
            case "SOFA":
                PlaceOnVolumeSurface(obj, room, MRUKAnchor.SceneLabels.COUCH);
                break;
            default:
                Debug.LogWarning($"[SemanticPlacement] Unknown label \"{label}\" — defaulting to FLOOR.");
                PlaceOnFloor(obj, room);
                break;
        }
    }

    // ----- FLOOR placement -----

    private void PlaceOnFloor(GameObject obj, MRUKRoom room)
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            PlaceFallbackFloor(obj, room);
            return;
        }

        // Primary: raycast from user gaze onto the floor
        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        else forward.Normalize();

        float floorY = GetFloorHeight(room);
        Vector3 gazeTarget = cam.transform.position + forward * 1.5f;
        gazeTarget.y = floorY;

        Bounds objBounds = GetObjectBounds(obj);
        Vector3 halfExtents = objBounds.extents;
        halfExtents.y = 0.05f; // thin slab for floor check

        // Try gaze position first, then spiral outward to find clear spot
        Vector3 bestPos = gazeTarget;
        if (IsPositionClear(room, gazeTarget, halfExtents))
        {
            bestPos = gazeTarget;
            Debug.Log($"[SemanticPlacement] Floor: gaze position clear for {obj.name}");
        }
        else
        {
            bestPos = FindClearPositionNear(room, gazeTarget, halfExtents, floorY);
            Debug.Log($"[SemanticPlacement] Floor: found clear position for {obj.name} at {bestPos}");
        }

        // Offset so object bottom sits ON the floor, not center-at-floor
        Bounds placedBounds = GetObjectBounds(obj);
        float bottomOffset = placedBounds.extents.y; // half-height
        bestPos.y = floorY + bottomOffset;

        obj.transform.position = bestPos;
        obj.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
    }

    private void PlaceFallbackFloor(GameObject obj, MRUKRoom room)
    {
        bool found = room.GenerateRandomPositionOnSurface(
            MRUK.SurfaceType.FACING_UP,
            floorClearanceRadius,
            new LabelFilter(MRUKAnchor.SceneLabels.FLOOR),
            out Vector3 pos, out Vector3 normal);

        if (!found)
        {
            var floorAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR));
            pos = floorAnchor != null ? floorAnchor.transform.position : Vector3.zero;
        }

        // Lift so bottom sits on floor
        Bounds b = GetObjectBounds(obj);
        pos.y += b.extents.y;
        obj.transform.position = pos;
        obj.transform.rotation = Quaternion.identity;
    }

    // ----- WALL placement -----

    private void PlaceOnWall(GameObject obj, MRUKRoom room)
    {
        Camera cam = Camera.main;
        Vector3 userPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 userFwd = cam != null ? cam.transform.forward : Vector3.forward;

        // Raycast from user gaze to find the wall they're looking at
        var labelFilter = new LabelFilter(MRUKAnchor.SceneLabels.WALL_FACE);
        Ray gazeRay = new Ray(userPos, userFwd);

        Pose bestPose = room.GetBestPoseFromRaycast(gazeRay, 10f, labelFilter,
            out MRUKAnchor hitAnchor, out Vector3 surfaceNormal);

        if (hitAnchor != null)
        {
            float halfDepth = GetObjectHalfDepth(obj);
            // Offset from wall so object doesn't clip
            Vector3 finalPos = bestPose.position + surfaceNormal * (halfDepth + wallOffset);

            // Check if this wall spot is clear (no RGB lights, shelves, etc.)
            Bounds objBounds = GetObjectBounds(obj);
            if (!room.IsPositionInSceneVolume(finalPos))
            {
                obj.transform.position = finalPos;
                obj.transform.rotation = Quaternion.LookRotation(surfaceNormal, Vector3.up);
                Debug.Log($"[SemanticPlacement] Wall: placed {obj.name} via gaze raycast");
                return;
            }

            // Gaze hit is blocked — slide along wall to find clear spot
            Vector3 wallRight = Vector3.Cross(surfaceNormal, Vector3.up).normalized;
            for (int i = 1; i <= maxPlacementAttempts; i++)
            {
                float offset = (i / 2) * objectSpacing * (i % 2 == 0 ? 1f : -1f);
                Vector3 candidate = finalPos + wallRight * offset;
                if (room.IsPositionInRoom(candidate) && !room.IsPositionInSceneVolume(candidate))
                {
                    obj.transform.position = candidate;
                    obj.transform.rotation = Quaternion.LookRotation(surfaceNormal, Vector3.up);
                    Debug.Log($"[SemanticPlacement] Wall: slid {obj.name} to clear spot (offset {offset:F2}m)");
                    return;
                }
            }
        }

        // Fallback: closest wall anchor
        var wallAnchor = room.Anchors
            .Where(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
            .OrderBy(a => Vector3.Distance(a.transform.position, userPos))
            .FirstOrDefault();

        if (wallAnchor != null)
        {
            Vector3 wallNormal = wallAnchor.transform.forward;
            float halfD = GetObjectHalfDepth(obj);
            obj.transform.position = wallAnchor.transform.position + wallNormal * (halfD + wallOffset);
            obj.transform.rotation = Quaternion.LookRotation(wallNormal, Vector3.up);
            Debug.Log($"[SemanticPlacement] Wall: fallback to closest anchor for {obj.name}");
        }
        else
        {
            PlaceEditorFallback(obj, "WALL_FACE");
        }
    }

    // ----- CEILING placement -----

    private void PlaceOnCeiling(GameObject obj, MRUKRoom room)
    {
        Camera cam = Camera.main;
        Vector3 userPos = cam != null ? cam.transform.position : Vector3.zero;

        var ceilingAnchor = room.Anchors.FirstOrDefault(
            a => a.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING));

        if (ceilingAnchor != null)
        {
            // Place above user position on ceiling
            float halfH = GetObjectHalfHeight(obj);
            Vector3 pos = new Vector3(userPos.x, ceilingAnchor.transform.position.y - halfH, userPos.z);
            obj.transform.position = pos;
            obj.transform.rotation = Quaternion.Euler(180f, 0f, 0f); // upside-down for hanging
        }
        else
        {
            obj.transform.position = new Vector3(userPos.x, 2.5f, userPos.z);
            obj.transform.rotation = Quaternion.identity;
        }

        Debug.Log($"[SemanticPlacement] Ceiling: placed {obj.name} at {obj.transform.position}");
    }

    // ----- VOLUME surface placement (TABLE / COUCH) -----

    private void PlaceOnVolumeSurface(GameObject obj, MRUKRoom room, MRUKAnchor.SceneLabels targetLabel)
    {
        Camera cam = Camera.main;
        Vector3 userPos = cam != null ? cam.transform.position : Vector3.zero;

        var anchor = room.Anchors
            .Where(a => a.HasAnyLabel(targetLabel))
            .OrderBy(a => Vector3.Distance(a.transform.position, userPos))
            .FirstOrDefault();

        if (anchor == null)
        {
            Debug.LogWarning($"[SemanticPlacement] No {targetLabel} anchor found — placing on floor.");
            PlaceOnFloor(obj, room);
            return;
        }

        // Volume top surface: anchor position + up by volume height
        float surfaceY = anchor.transform.position.y;
        if (anchor.VolumeBounds.HasValue)
        {
            // VolumeBounds is in local 2D space; the anchor's Y position is the top surface
            // Actually, anchor.transform.position is the center of the 2D face,
            // and the volume extends downward. Top surface = anchor Y position.
            surfaceY = anchor.transform.position.y;
        }

        float halfH = GetObjectHalfHeight(obj);
        Bounds objBounds = GetObjectBounds(obj);
        Vector3 halfExtents = new Vector3(objBounds.extents.x, 0.02f, objBounds.extents.z);

        // Try placing at the center of the anchor first
        Vector3 centerPos = new Vector3(anchor.transform.position.x, surfaceY + halfH, anchor.transform.position.z);

        if (IsPositionClear(room, centerPos, halfExtents))
        {
            obj.transform.position = centerPos;
            obj.transform.rotation = Quaternion.identity;
            Debug.Log($"[SemanticPlacement] {targetLabel}: placed {obj.name} at anchor center");
            return;
        }

        // Center occupied — search nearby positions on the surface
        // Use anchor's local axes to sample positions on the surface
        Vector3 anchorRight = anchor.transform.right;
        Vector3 anchorUp = anchor.transform.up; // up in local = forward on the 2D surface plane

        float searchRadius = anchor.VolumeBounds.HasValue
            ? Mathf.Max(anchor.VolumeBounds.Value.size.x, anchor.VolumeBounds.Value.size.y) * 0.4f
            : 0.3f;

        for (int i = 1; i <= maxPlacementAttempts; i++)
        {
            // Spiral outward from center
            float angle = i * 137.5f * Mathf.Deg2Rad; // golden angle for even spread
            float radius = searchRadius * Mathf.Sqrt((float)i / maxPlacementAttempts);
            Vector3 offset = anchorRight * (Mathf.Cos(angle) * radius) +
                             anchorUp * (Mathf.Sin(angle) * radius);
            Vector3 candidate = centerPos + offset;

            if (room.IsPositionInRoom(candidate) && !room.IsPositionInSceneVolume(candidate) &&
                IsPositionClear(room, candidate, halfExtents))
            {
                obj.transform.position = candidate;
                obj.transform.rotation = Quaternion.identity;
                Debug.Log($"[SemanticPlacement] {targetLabel}: found clear edge position for {obj.name}");
                return;
            }
        }

        // Accept best effort at center
        obj.transform.position = centerPos;
        obj.transform.rotation = Quaternion.identity;
        Debug.LogWarning($"[SemanticPlacement] {targetLabel}: no clear spot, placed {obj.name} at center anyway");
    }

    // -------------------------------------------------------------------------
    // Collision helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Checks if a position is clear of real-world scene volumes AND physics colliders.
    /// Combines MRUK scene volume check with Unity physics overlap check.
    /// </summary>
    private bool IsPositionClear(MRUKRoom room, Vector3 position, Vector3 halfExtents)
    {
        // Check against MRUK scene volumes (furniture detected by room scan)
        if (room.IsPositionInSceneVolume(position))
            return false;

        // Check against Unity physics colliders (other spawned objects)
        if (Physics.CheckBox(position, halfExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            return false;

        return true;
    }

    /// <summary>
    /// Starting from a preferred position, spiral outward to find a clear spot.
    /// </summary>
    private Vector3 FindClearPositionNear(MRUKRoom room, Vector3 preferred, Vector3 halfExtents, float fixedY)
    {
        for (int i = 1; i <= maxPlacementAttempts; i++)
        {
            float angle = i * 137.5f * Mathf.Deg2Rad; // golden angle spiral
            float radius = objectSpacing * Mathf.Sqrt((float)i / maxPlacementAttempts) * 2f;
            Vector3 candidate = preferred + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            candidate.y = fixedY;

            if (room.IsPositionInRoom(candidate) && IsPositionClear(room, candidate, halfExtents))
                return candidate;
        }

        // All attempts failed — return preferred with warning
        Debug.LogWarning($"[SemanticPlacement] Could not find clear floor position after {maxPlacementAttempts} attempts, using gaze target.");
        return preferred;
    }

    private float GetFloorHeight(MRUKRoom room)
    {
        var floorAnchor = room.Anchors.FirstOrDefault(
            a => a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR));
        return floorAnchor != null ? floorAnchor.transform.position.y : 0f;
    }
#endif

    // -------------------------------------------------------------------------
    // Editor fallback (no MRUK)
    // -------------------------------------------------------------------------

    private static void PlaceEditorFallback(GameObject obj, string label)
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            obj.transform.position = Vector3.zero;
            return;
        }

        Vector3 forward = cam.transform.forward;
        forward.y       = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        else forward.Normalize();

        Vector3 basePos = cam.transform.position + forward * 1.5f;

        switch (label)
        {
            case "WALL_FACE":
                obj.transform.position = basePos + Vector3.up * 1.2f;
                obj.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
                break;
            case "CEILING":
                obj.transform.position = basePos + Vector3.up * 2.2f;
                obj.transform.rotation = Quaternion.identity;
                break;
            default: // FLOOR, TABLE, etc.
                float halfH = GetObjectBounds(obj).extents.y;
                obj.transform.position = new Vector3(basePos.x, halfH, basePos.z);
                obj.transform.rotation = Quaternion.identity;
                break;
        }

        Debug.Log($"[SemanticPlacement] Editor fallback: placed \"{obj.name}\" at {obj.transform.position}");
    }

    // -------------------------------------------------------------------------
    // Geometry helpers
    // -------------------------------------------------------------------------

    private static Bounds GetObjectBounds(GameObject obj)
    {
        var renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.one * 0.2f);
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    private static float GetObjectHalfHeight(GameObject obj)
    {
        return GetObjectBounds(obj).extents.y;
    }

    private static float GetObjectHalfDepth(GameObject obj)
    {
        var b = GetObjectBounds(obj);
        return Mathf.Min(b.extents.x, b.extents.z);
    }
}
