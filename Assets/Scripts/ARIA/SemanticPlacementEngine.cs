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

using Meta.XR.MRUtilityKit;

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
        Place(obj, surfaceLabel, null, null);
    }

    public void Place(GameObject obj, string surfaceLabel, MRUKAnchor specificAnchor)
    {
        Place(obj, surfaceLabel, specificAnchor, null);
    }

    /// <summary>
    /// Positions <paramref name="obj"/> on a specific MRUK anchor if provided,
    /// otherwise falls back to generic surface_label-based placement.
    /// <paramref name="nearAnchor"/> is a proximity hint (e.g. "near the door").
    /// </summary>
    public void Place(GameObject obj, string surfaceLabel, MRUKAnchor specificAnchor, MRUKAnchor nearAnchor)
    {
        if (obj == null) return;

        string label = (surfaceLabel ?? "FLOOR").ToUpperInvariant();

        var room = MRUK.Instance?.GetCurrentRoom();
        if (room != null)
        {
            if (specificAnchor != null)
            {
                PlaceOnSpecificAnchor(obj, specificAnchor, room);
                // If near_anchor hint, nudge position toward that anchor
                if (nearAnchor != null)
                    NudgeTowardAnchor(obj, nearAnchor, room);
                return;
            }
            PlaceWithMRUK(obj, label, room);
            if (nearAnchor != null)
                NudgeTowardAnchor(obj, nearAnchor, room);
            return;
        }

        PlaceEditorFallback(obj, label);
    }

    // -------------------------------------------------------------------------
    // MRUK placement (Quest APK) — collision-aware
    // -------------------------------------------------------------------------


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

    /// <summary>
    /// Nudges an already-placed object's XZ position toward a reference anchor
    /// (e.g. "near the door"). Keeps Y unchanged so it stays on its surface.
    /// Moves 70% of the way toward the anchor's base position, then collision checks.
    /// </summary>
    private void NudgeTowardAnchor(GameObject obj, MRUKAnchor nearAnchor, MRUKRoom room)
    {
        Vector3 objPos = obj.transform.position;
        Vector3 anchorPos = nearAnchor.transform.position;

        // Move XZ toward anchor, keep Y (stay on surface)
        Vector3 target = new Vector3(anchorPos.x, objPos.y, anchorPos.z);
        Vector3 nudged = Vector3.Lerp(objPos, target, 0.7f);

        // Offset slightly away from the anchor surface so object doesn't clip into it
        Vector3 anchorNormal = nearAnchor.transform.forward;
        Bounds b = GetObjectBounds(obj);
        float offset = Mathf.Max(b.extents.x, b.extents.z) + 0.1f;
        nudged += new Vector3(anchorNormal.x, 0, anchorNormal.z).normalized * offset;

        // Check clearance at nudged position
        Vector3 halfExtents = new Vector3(b.extents.x, 0.05f, b.extents.z);
        if (IsPositionClear(room, nudged, halfExtents))
        {
            obj.transform.position = nudged;
            Debug.Log($"[SemanticPlacement] Nudged {obj.name} toward {nearAnchor.name}");
            ARIADebugUI.AppendClaudeLog($"  → nudged near {nearAnchor.name}");
        }
        else
        {
            Debug.Log($"[SemanticPlacement] Nudge blocked — keeping original position for {obj.name}");
        }
    }

    // ----- Specific anchor placement -----

    private void PlaceOnSpecificAnchor(GameObject obj, MRUKAnchor anchor, MRUKRoom room)
    {
        if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
        {
            PlaceOnSpecificWall(obj, anchor, room);
        }
        else if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE))
        {
            PlaceOnSpecificVolume(obj, anchor, room);
        }
        else if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.COUCH))
        {
            PlaceOnSpecificVolume(obj, anchor, room);
        }
        else if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))
        {
            PlaceOnFloor(obj, room); // floor is unique, same logic
        }
        else if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING))
        {
            PlaceOnCeiling(obj, room);
        }
        else
        {
            Debug.LogWarning($"[SemanticPlacement] Unknown anchor type — defaulting to floor.");
            PlaceOnFloor(obj, room);
        }
    }

    private void PlaceOnSpecificWall(GameObject obj, MRUKAnchor wallAnchor, MRUKRoom room)
    {
        Camera cam = Camera.main;
        Vector3 userPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 userFwd = cam != null ? cam.transform.forward : Vector3.forward;

        Vector3 wallNormal = wallAnchor.transform.forward;
        Vector3 wallCenter = wallAnchor.transform.position;
        float halfDepth = GetObjectHalfDepth(obj);

        // Intersect gaze ray with this wall's plane for fine positioning
        Plane wallPlane = new Plane(wallNormal, wallCenter);
        Ray gazeRay = new Ray(userPos, userFwd);
        Vector3 hitPoint = wallCenter;

        if (wallPlane.Raycast(gazeRay, out float enter) && enter > 0)
        {
            hitPoint = gazeRay.GetPoint(enter);
            // Clamp to wall bounds
            if (wallAnchor.PlaneRect.HasValue)
            {
                Vector3 localHit = wallAnchor.transform.InverseTransformPoint(hitPoint);
                Rect rect = wallAnchor.PlaneRect.Value;
                Bounds objBounds = GetObjectBounds(obj);
                localHit.x = Mathf.Clamp(localHit.x,
                    rect.xMin + objBounds.extents.x, rect.xMax - objBounds.extents.x);
                localHit.y = Mathf.Clamp(localHit.y,
                    rect.yMin + objBounds.extents.y, rect.yMax - objBounds.extents.y);
                hitPoint = wallAnchor.transform.TransformPoint(localHit);
            }
        }

        Vector3 finalPos = hitPoint + wallNormal * (halfDepth + wallOffset);
        obj.transform.position = finalPos;
        obj.transform.rotation = Quaternion.LookRotation(wallNormal, Vector3.up);
        Debug.Log($"[SemanticPlacement] Specific wall: placed {obj.name} on {wallAnchor.name}");
        ARIADebugUI.AppendClaudeLog($"PLACED: {obj.name}\n  → wall at ({finalPos.x:F1},{finalPos.y:F1},{finalPos.z:F1})");
    }

    private void PlaceOnSpecificVolume(GameObject obj, MRUKAnchor anchor, MRUKRoom room)
    {
        float surfaceY = anchor.transform.position.y;
        float halfH = GetObjectHalfHeight(obj);
        Vector3 centerPos = new Vector3(
            anchor.transform.position.x, surfaceY + halfH, anchor.transform.position.z);

        Bounds objBounds = GetObjectBounds(obj);
        Vector3 halfExtents = new Vector3(objBounds.extents.x, 0.02f, objBounds.extents.z);

        if (IsPositionClear(room, centerPos, halfExtents))
        {
            obj.transform.position = centerPos;
            obj.transform.rotation = Quaternion.identity;
            Debug.Log($"[SemanticPlacement] Specific volume: placed {obj.name} at center of {anchor.name}");
            return;
        }

        // Spiral search for clear spot on this surface
        Vector3 anchorRight = anchor.transform.right;
        Vector3 anchorUp = anchor.transform.up;
        float searchRadius = anchor.VolumeBounds.HasValue
            ? Mathf.Max(anchor.VolumeBounds.Value.size.x, anchor.VolumeBounds.Value.size.y) * 0.4f
            : 0.3f;

        for (int i = 1; i <= maxPlacementAttempts; i++)
        {
            float angle = i * 137.5f * Mathf.Deg2Rad;
            float radius = searchRadius * Mathf.Sqrt((float)i / maxPlacementAttempts);
            Vector3 offset = anchorRight * (Mathf.Cos(angle) * radius) +
                             anchorUp * (Mathf.Sin(angle) * radius);
            Vector3 candidate = centerPos + offset;

            if (IsPositionClear(room, candidate, halfExtents))
            {
                obj.transform.position = candidate;
                obj.transform.rotation = Quaternion.identity;
                Debug.Log($"[SemanticPlacement] Specific volume: found clear spot for {obj.name}");
                return;
            }
        }

        obj.transform.position = centerPos;
        obj.transform.rotation = Quaternion.identity;
        Debug.LogWarning($"[SemanticPlacement] Specific volume: no clear spot, placed at center.");
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

        // Offset so object's actual bottom mesh sits ON the floor
        obj.transform.position = bestPos; // place at floor first
        Bounds placedBounds = GetObjectBounds(obj);
        float bottomToOrigin = obj.transform.position.y - placedBounds.min.y;
        bestPos.y = floorY + bottomToOrigin;

        obj.transform.position = bestPos;
        obj.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
        ARIADebugUI.AppendClaudeLog($"PLACED: {obj.name}\n  → floor at ({bestPos.x:F1},{bestPos.y:F1},{bestPos.z:F1})");
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

        // Lift so actual bottom sits on floor
        obj.transform.position = pos;
        Bounds b = GetObjectBounds(obj);
        float bottomToOrigin = obj.transform.position.y - b.min.y;
        pos.y += bottomToOrigin;
        obj.transform.position = pos;
        obj.transform.rotation = Quaternion.identity;
    }

    // ----- WALL placement -----

    private void PlaceOnWall(GameObject obj, MRUKRoom room)
    {
        Camera cam = Camera.main;
        Vector3 userPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 userFwd = cam != null ? cam.transform.forward : Vector3.forward;

        // Primary: Physics.Raycast from gaze to hit EffectMesh wall colliders
        // This gives us the EXACT point on the wall where the user is looking
        Ray gazeRay = new Ray(userPos, userFwd);

        if (Physics.Raycast(gazeRay, out RaycastHit hit, 10f))
        {
            float halfDepth = GetObjectHalfDepth(obj);
            Vector3 wallNormal = hit.normal;
            Vector3 finalPos = hit.point + wallNormal * (halfDepth + wallOffset);

            obj.transform.position = finalPos;
            obj.transform.rotation = Quaternion.LookRotation(wallNormal, Vector3.up);
            Debug.Log($"[SemanticPlacement] Wall: placed {obj.name} at gaze hit {hit.point} (Y={hit.point.y:F2}m)");
            ARIADebugUI.AppendClaudeLog($"PLACED: {obj.name}\n  → wall at ({finalPos.x:F1},{finalPos.y:F1},{finalPos.z:F1})");
            return;
        }

        // Fallback: MRUK GetBestPoseFromRaycast
        var labelFilter = new LabelFilter(MRUKAnchor.SceneLabels.WALL_FACE);
        Pose bestPose = room.GetBestPoseFromRaycast(gazeRay, 10f, labelFilter,
            out MRUKAnchor hitAnchor, out Vector3 surfaceNormal);

        if (hitAnchor != null)
        {
            float halfDepth = GetObjectHalfDepth(obj);
            Vector3 finalPos = bestPose.position + surfaceNormal * (halfDepth + wallOffset);
            // Use the user's gaze Y instead of anchor center Y
            finalPos.y = userPos.y + userFwd.y * Vector3.Distance(userPos, finalPos);
            // Clamp Y to stay within wall bounds
            float floorY = GetFloorHeight(room);
            var ceilAnchor = room.Anchors.FirstOrDefault(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING));
            float ceilingY = ceilAnchor != null ? ceilAnchor.transform.position.y : 2.8f;
            Bounds b = GetObjectBounds(obj);
            finalPos.y = Mathf.Clamp(finalPos.y, floorY + b.extents.y, ceilingY - b.extents.y);

            obj.transform.position = finalPos;
            obj.transform.rotation = Quaternion.LookRotation(surfaceNormal, Vector3.up);
            Debug.Log($"[SemanticPlacement] Wall: placed {obj.name} via MRUK at Y={finalPos.y:F2}m");
            return;
        }

        // Last fallback: closest wall anchor at gaze height
        var wallAnchor = room.Anchors
            .Where(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
            .OrderBy(a => Vector3.Distance(a.transform.position, userPos))
            .FirstOrDefault();

        if (wallAnchor != null)
        {
            Vector3 wallNormal = wallAnchor.transform.forward;
            float halfD = GetObjectHalfDepth(obj);
            Vector3 pos = wallAnchor.transform.position + wallNormal * (halfD + wallOffset);
            pos.y = userPos.y; // place at eye height
            obj.transform.position = pos;
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
    // Auto-fit: shrink object to fit available MRUK space
    // -------------------------------------------------------------------------


    /// <summary>
    /// Measures available clearance around the object using MRUK room data
    /// and shrinks the object if it clips walls, ceiling, or furniture.
    /// Called AFTER Place() so the object is already positioned.
    /// Only adjusts Y for floor-level objects — wall/table/ceiling placements keep their Y.
    /// </summary>
    public void FitToAvailableSpace(GameObject obj, MRUKRoom room)
    {
        if (obj == null || room == null) return;

        Bounds b = GetObjectBounds(obj);
        Vector3 objPos = obj.transform.position;

        // Measure clearance in each direction from object center
        float clearX = MeasureClearance(room, objPos, Vector3.right, b.extents.x * 2f);
        float clearZ = MeasureClearance(room, objPos, Vector3.forward, b.extents.z * 2f);

        // Get ceiling height for Y constraint
        float floorY = GetFloorHeight(room);
        var ceilAnchor = room.Anchors.FirstOrDefault(
            a => a.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING));
        float ceilingY = ceilAnchor != null ? ceilAnchor.transform.position.y : 2.8f;
        float clearY = ceilingY - floorY;

        // Determine if object is near floor level (within 0.3m above floor surface)
        // Wall, table, and ceiling placements should NOT have their Y reset
        bool isOnFloor = (objPos.y - floorY) < (b.extents.y + 0.3f);

        // Object dimensions in world space
        float objW = b.size.x;
        float objH = b.size.y;
        float objD = b.size.z;

        // Calculate how much we need to shrink to fit
        float scaleX = clearX > 0.1f && objW > clearX ? clearX / objW : 1f;
        float scaleY = clearY > 0.1f && objH > clearY ? (clearY * 0.85f) / objH : 1f; // 85% of room height max
        float scaleZ = clearZ > 0.1f && objD > clearZ ? clearZ / objD : 1f;

        float shrinkFactor = Mathf.Min(scaleX, scaleY, scaleZ);

        if (shrinkFactor < 0.95f) // only shrink if significantly too big
        {
            shrinkFactor = Mathf.Max(shrinkFactor, 0.1f); // never shrink below 10%
            obj.transform.localScale *= shrinkFactor;

            // Only re-snap Y to floor if object was actually on the floor
            if (isOnFloor)
            {
                Bounds newBounds = GetObjectBounds(obj);
                Vector3 pos = obj.transform.position;
                pos.y = floorY + newBounds.extents.y;
                obj.transform.position = pos;
            }

            Debug.Log($"[SemanticPlacement] FitToSpace: shrunk \"{obj.name}\" by {shrinkFactor:F2}x " +
                      $"(clearance: X={clearX:F2}m Z={clearZ:F2}m Y={clearY:F2}m, onFloor={isOnFloor})");
        }
        // If no shrinking needed, do NOT touch Y — preserve wall/table/ceiling positioning
    }

    /// <summary>Measures clearance from position in a direction using physics + MRUK.</summary>
    private static float MeasureClearance(MRUKRoom room, Vector3 from, Vector3 dir, float maxDist)
    {
        // Physics raycast to find nearest collider (walls, furniture from EffectMesh)
        if (Physics.Raycast(from, dir, out RaycastHit hit, maxDist * 2f))
            return hit.distance;
        if (Physics.Raycast(from, -dir, out RaycastHit hitNeg, maxDist * 2f))
            return hitNeg.distance;

        // Fallback: room dimensions
        return maxDist;
    }


    // -------------------------------------------------------------------------
    // Surface-aware fit — scale to fit specific surface, cap at canonical size
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shrinks object proportionally to fit a specific MRUK surface (table, wall, etc).
    /// Never grows beyond canonical real-world dimensions for the category.
    /// Called AFTER FitToAvailableSpace (room-level) for surface-specific refinement.
    /// </summary>
    public void FitToSurface(GameObject obj, MRUKAnchor surfaceAnchor, string category)
    {
        if (obj == null || surfaceAnchor == null) return;

        Bounds objBounds = GetObjectBounds(obj);
        Vector3 canonical = ScaleInferenceSystem.GetCanonicalDimensions(category);
        float shrinkRatio = 1f;

        if (surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE) ||
            surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.COUCH))
        {
            // Table/couch: compare object footprint (X,Z) against surface top area
            if (surfaceAnchor.VolumeBounds.HasValue)
            {
                Vector2 surfSize = surfaceAnchor.VolumeBounds.Value.size; // local 2D
                float surfW = surfSize.x;
                float surfD = surfSize.y; // VolumeBounds Y is depth in world XZ

                float ratioX = objBounds.size.x > 0.01f ? surfW / objBounds.size.x : 1f;
                float ratioZ = objBounds.size.z > 0.01f ? surfD / objBounds.size.z : 1f;
                shrinkRatio = Mathf.Min(ratioX, ratioZ, 1f); // never grow

                // Leave a 10% margin so object doesn't fill entire surface
                if (shrinkRatio < 0.95f)
                    shrinkRatio *= 0.9f;
            }
        }
        else if (surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
        {
            // Wall: compare object face (X,Y) against wall PlaneRect
            if (surfaceAnchor.PlaneRect.HasValue)
            {
                Rect wallRect = surfaceAnchor.PlaneRect.Value;
                float ratioX = objBounds.size.x > 0.01f ? wallRect.width / objBounds.size.x : 1f;
                float ratioY = objBounds.size.y > 0.01f ? wallRect.height / objBounds.size.y : 1f;
                shrinkRatio = Mathf.Min(ratioX, ratioY, 1f);

                // Leave 15% margin on walls (don't fill edge to edge)
                if (shrinkRatio < 0.95f)
                    shrinkRatio *= 0.85f;
            }
        }
        // FLOOR: no surface-specific shrink (FitToAvailableSpace handles room-level)

        // Enforce canonical max: if object currently exceeds canonical dimensions, shrink
        if (canonical.y > 0.01f)
        {
            float canonRatioY = canonical.y / Mathf.Max(objBounds.size.y, 0.001f);
            float canonRatioX = canonical.x > 0.01f ? canonical.x / Mathf.Max(objBounds.size.x, 0.001f) : 1f;
            float canonRatioZ = canonical.z > 0.01f ? canonical.z / Mathf.Max(objBounds.size.z, 0.001f) : 1f;
            float canonCap = Mathf.Min(canonRatioY, canonRatioX, canonRatioZ, 1f);
            shrinkRatio = Mathf.Min(shrinkRatio, canonCap);
        }

        if (shrinkRatio < 0.95f)
        {
            shrinkRatio = Mathf.Max(shrinkRatio, 0.1f); // never below 10%
            obj.transform.localScale *= shrinkRatio;

            // Re-snap Y to surface after scaling
            if (surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE) ||
                surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.COUCH))
            {
                Bounds nb = GetObjectBounds(obj);
                float surfaceY = surfaceAnchor.transform.position.y;
                float bottomOffset = obj.transform.position.y - nb.min.y;
                obj.transform.position = new Vector3(
                    obj.transform.position.x, surfaceY + bottomOffset, obj.transform.position.z);
            }

            Debug.Log($"[SemanticPlacement] FitToSurface: shrunk \"{obj.name}\" by {shrinkRatio:F2}x for {surfaceAnchor.name}");
            ARIADebugUI.AppendClaudeLog($"  → fit to {surfaceAnchor.name} ({shrinkRatio:F2}x)");
        }
    }

    /// <summary>
    /// Finds the nearest MRUK wall anchor to a world position.
    /// Returns the distance. Outputs surface position, normal, and anchor.
    /// </summary>
    public float FindNearestWall(Vector3 position, out Vector3 surfacePos, out Vector3 wallNormal, out MRUKAnchor wallAnchor)
    {
        surfacePos = position;
        wallNormal = Vector3.forward;
        wallAnchor = null;

        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null) return float.MaxValue;

        float bestDist = float.MaxValue;
        foreach (var anchor in room.Anchors)
        {
            if (!anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE)) continue;

            Vector3 wallCenter = anchor.transform.position;
            // Project position onto the wall plane
            Vector3 toPos = position - wallCenter;
            Vector3 normal = anchor.transform.forward;
            float dist = Mathf.Abs(Vector3.Dot(toPos, normal));

            if (dist < bestDist)
            {
                bestDist = dist;
                surfacePos = wallCenter + normal * Vector3.Dot(toPos - normal * dist, normal); // closest point on wall plane
                // Simpler: project onto wall plane
                surfacePos = position - normal * Vector3.Dot(toPos, normal);
                wallNormal = normal;
                wallAnchor = anchor;
            }
        }
        return bestDist;
    }

    /// <summary>
    /// Detects which MRUK surface (FLOOR, TABLE, COUCH) is directly below a position.
    /// Returns the anchor, or null if none found.
    /// </summary>
    public MRUKAnchor DetectSurfaceBelow(Vector3 position)
    {
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null) return null;

        // Check tables/couches first (they're above floor)
        MRUKAnchor bestVolume = null;
        float bestVolumeDist = float.MaxValue;
        foreach (var anchor in room.Anchors)
        {
            if (!anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE) &&
                !anchor.HasAnyLabel(MRUKAnchor.SceneLabels.COUCH)) continue;

            float surfaceY = anchor.transform.position.y;
            float yDiff = position.y - surfaceY;

            // Object should be above or at the surface (within 0.5m)
            if (yDiff >= -0.05f && yDiff < 0.5f)
            {
                // Check XZ proximity — object should be roughly over the surface
                Vector3 anchorPos = anchor.transform.position;
                float xzDist = Vector2.Distance(
                    new Vector2(position.x, position.z),
                    new Vector2(anchorPos.x, anchorPos.z));

                float surfaceRadius = anchor.VolumeBounds.HasValue
                    ? Mathf.Max(anchor.VolumeBounds.Value.size.x, anchor.VolumeBounds.Value.size.y) * 0.6f
                    : 0.5f;

                if (xzDist < surfaceRadius && yDiff < bestVolumeDist)
                {
                    bestVolumeDist = yDiff;
                    bestVolume = anchor;
                }
            }
        }
        if (bestVolume != null) return bestVolume;

        // Fallback: floor
        return room.Anchors.FirstOrDefault(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR));
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
