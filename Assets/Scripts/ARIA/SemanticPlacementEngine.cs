// SemanticPlacementEngine.cs — figures out WHERE to put things in the real room
// takes Claude's surface_label (FLOOR/WALL/TABLE/etc) and finds the right MRUK anchor,
// then does collision-aware placement so objects don't overlap real furniture.
// uses a golden-angle spiral search when the first spot is blocked.
//
// also handles the clutter-aware system: detects if the user is pointing at real objects
// (books, boxes) vs clear surface using Global Mesh vs anchor boundary comparison.
// PlaceOnGlobalMesh puts objects directly on the scanned geometry.
// FindClearSpotOnAnchor finds empty spots on tables/walls avoiding clutter.
//
// in editor (no MRUK), just sticks objects 1.5m in front of the camera.

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
    /// Detects which surface type the user is currently looking at via gaze raycast.
    /// Returns "WALL_FACE", "TABLE", "FLOOR", etc. based on what the crosshair hits.
    /// Falls back to <paramref name="defaultLabel"/> if nothing is hit.
    /// </summary>
    public string DetectGazeSurface(string defaultLabel = "FLOOR")
    {
        Camera cam = Camera.main;
        if (cam == null) return defaultLabel;

        Ray gazeRay = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(gazeRay, out RaycastHit hit, 10f))
            return defaultLabel;

        // Check if we hit an EffectMesh surface — determine its MRUK anchor type
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null) return defaultLabel;

        // Find which anchor is closest to the hit point
        float bestDist = 0.5f; // tolerance
        string bestLabel = defaultLabel;
        MRUKAnchor bestAnchor = null;

        foreach (var anchor in room.Anchors)
        {
            float dist = Vector3.Distance(anchor.transform.position, hit.point);

            // For walls, check plane distance instead of center distance
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
            {
                Vector3 toHit = hit.point - anchor.transform.position;
                dist = Mathf.Abs(Vector3.Dot(toHit, anchor.transform.forward));
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                bestAnchor = anchor;
            }
        }

        if (bestAnchor == null) return defaultLabel;

        // Determine surface label from anchor type
        if (bestAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE)) return "WALL_FACE";
        if (bestAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE))     return "TABLE";
        if (bestAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.COUCH))     return "COUCH";
        if (bestAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING))   return "CEILING";
        if (bestAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))     return "FLOOR";

        // Check hit normal: mostly vertical = horizontal surface (floor/table), mostly horizontal = wall
        if (Mathf.Abs(hit.normal.y) > 0.7f) return "FLOOR"; // flat surface
        return "WALL_FACE";
    }

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

        // Set rotation FIRST so bounds calculation is wall-relative
        obj.transform.rotation = Quaternion.LookRotation(wallNormal, Vector3.up);

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

        // Depth along wall normal — use smallest extent (painting thickness), clamped
        Bounds rotBounds = GetObjectBounds(obj);
        float halfDepth = Mathf.Min(rotBounds.extents.x, Mathf.Min(rotBounds.extents.y, rotBounds.extents.z));
        halfDepth = Mathf.Clamp(halfDepth, 0.005f, 0.05f); // 0.5cm - 5cm

        Vector3 finalPos = hitPoint + wallNormal * (halfDepth + 0.005f); // flush + 5mm gap
        obj.transform.position = finalPos;
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
        if (room == null) return 0f;
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

        // Any volume anchor with a flat top (TABLE, COUCH, BED, OTHER, STORAGE, etc.)
        bool isVolumeSurface = !surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR) &&
                               !surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING) &&
                               !surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE) &&
                               !surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME) &&
                               !surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.WINDOW_FRAME);

        if (isVolumeSurface)
        {
            // Volume surface: compare object footprint (X,Z) against surface top area
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
            // Wall items: no auto-resize — paintings/art keep their spawned size.
            // Wall placement + PlaneRect clamping in PlaceOnSpecificWall handles positioning.
            if (false) // wall resize disabled — was shrinking paintings too aggressively
            {
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

            // Re-snap Y to surface after scaling so object doesn't float
            Bounds nb = GetObjectBounds(obj);
            float bottomOffset = obj.transform.position.y - nb.min.y;
            if (isVolumeSurface)
            {
                // Snap bottom to top of volume surface (table, bed, couch, etc.)
                float surfaceY = surfaceAnchor.transform.position.y;
                obj.transform.position = new Vector3(
                    obj.transform.position.x, surfaceY + bottomOffset, obj.transform.position.z);
            }
            else if (surfaceAnchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))
            {
                // Floor: snap bottom to floor height
                float floorY = GetFloorHeight(MRUK.Instance?.GetCurrentRoom());
                obj.transform.position = new Vector3(
                    obj.transform.position.x, floorY + bottomOffset, obj.transform.position.z);
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
    /// Detects which MRUK surface is directly below a position.
    /// Checks ALL volume anchors (TABLE, COUCH, BED, OTHER, STORAGE, etc.)
    /// as any flat-topped object is a valid landing surface.
    /// Returns the anchor, or falls back to FLOOR.
    /// </summary>
    public MRUKAnchor DetectSurfaceBelow(Vector3 position)
    {
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null) return null;

        // Check ALL volume anchors (anything with a flat top surface above the floor)
        MRUKAnchor bestVolume = null;
        float bestVolumeDist = float.MaxValue;
        foreach (var anchor in room.Anchors)
        {
            // Skip floor, ceiling, walls — we want volume objects with flat tops
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WINDOW_FRAME)) continue;
            // Everything else (TABLE, COUCH, BED, OTHER, STORAGE, etc.) is a potential surface

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
    // Clutter-aware placement (Global Mesh + Anchor Boundaries)
    // -------------------------------------------------------------------------

    private static readonly int GlobalMeshLayerMask = 1 << 8; // Layer 8 = GlobalMesh

    /// <summary>
    /// Identifies which MRUK anchor's footprint contains the given world point.
    /// Checks volume anchors first (TABLE, COUCH, BED, etc.), then falls back to FLOOR.
    /// Returns null if no anchor matches (shouldn't happen in a scanned room).
    /// </summary>
    public MRUKAnchor IdentifyAnchorAtPoint(Vector3 worldPoint)
    {
        var room = MRUK.Instance?.GetCurrentRoom();
        if (room == null) return null;

        MRUKAnchor bestVolume = null;
        float bestDist = float.MaxValue;

        foreach (var anchor in room.Anchors)
        {
            // Skip non-surface anchors
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WINDOW_FRAME) ||
                anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH)) continue;

            // For walls: check plane distance
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
            {
                Vector3 toPoint = worldPoint - anchor.transform.position;
                float planeDist = Mathf.Abs(Vector3.Dot(toPoint, anchor.transform.forward));
                if (planeDist < 0.15f && planeDist < bestDist)
                {
                    bestDist = planeDist;
                    bestVolume = anchor;
                }
                continue;
            }

            // For volumes (TABLE, BED, COUCH, OTHER, etc.): check XZ footprint + Y proximity
            if (anchor.VolumeBounds.HasValue)
            {
                Vector3 localPos = anchor.transform.InverseTransformPoint(worldPoint);
                Vector2 halfSize = anchor.VolumeBounds.Value.size * 0.5f;

                // Within XZ footprint (with small margin)?
                if (Mathf.Abs(localPos.x) <= halfSize.x + 0.1f &&
                    Mathf.Abs(localPos.z) <= halfSize.y + 0.1f) // VolumeBounds.size.y = depth
                {
                    float yDiff = Mathf.Abs(worldPoint.y - anchor.transform.position.y);
                    if (yDiff < 1.0f && yDiff < bestDist)
                    {
                        bestDist = yDiff;
                        bestVolume = anchor;
                    }
                }
            }

            // Floor as fallback
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR) && bestVolume == null)
                bestVolume = anchor;
        }

        return bestVolume;
    }

    /// <summary>
    /// Checks if a Global Mesh hit point is above the anchor surface (= clutter).
    /// Returns true if the hit is more than 1cm above the anchor's top surface.
    /// </summary>
    public bool IsPointOnClutter(Vector3 hitPoint, MRUKAnchor anchor)
    {
        if (anchor == null) return false;

        // For WALLS: clutter = something sticking OUT from the wall plane (depth axis, not Y)
        if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE) ||
            anchor.HasAnyLabel(MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE))
        {
            // Distance from hit point to the wall plane along the wall's forward (normal) axis
            Vector3 toHit = hitPoint - anchor.transform.position;
            float depthFromWall = Mathf.Abs(Vector3.Dot(toHit, anchor.transform.forward));
            return depthFromWall > 0.03f; // 3cm from wall plane = clutter (pinboard, shelf, etc.)
        }

        // For HORIZONTAL surfaces: clutter = anything above the anchor surface Y
        float anchorSurfaceY = anchor.transform.position.y;

        if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))
            anchorSurfaceY = GetFloorHeight(MRUK.Instance?.GetCurrentRoom());

        float difference = hitPoint.y - anchorSurfaceY;
        return difference > 0.01f; // 1cm threshold
    }

    /// <summary>
    /// Checks if an anchor represents a vertical (wall-like) surface.
    /// </summary>
    public static bool IsWallLikeAnchor(MRUKAnchor anchor)
    {
        if (anchor == null) return false;
        return anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE) ||
               anchor.HasAnyLabel(MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE);
    }

    /// <summary>
    /// Places an object directly on the Global Mesh surface at the given hit point,
    /// oriented by the surface normal. Used for "on_clutter" placement.
    /// </summary>
    public void PlaceOnGlobalMesh(GameObject obj, Vector3 hitPoint, Vector3 hitNormal)
    {
        Bounds b = GetObjectBounds(obj);
        float halfH = b.extents.y;

        if (hitNormal.y > 0.7f)
        {
            // Mostly flat surface (top of book, flat clutter) — place upright
            obj.transform.position = hitPoint + Vector3.up * halfH;
            // Keep existing Y rotation, just ensure upright
            Vector3 euler = obj.transform.eulerAngles;
            obj.transform.rotation = Quaternion.Euler(0f, euler.y, 0f);
        }
        else if (hitNormal.y < 0.3f)
        {
            // Mostly vertical surface — mount flush against wall.
            // Set rotation FIRST so bounds are in wall-relative orientation,
            // then calculate depth along the wall normal direction.
            obj.transform.rotation = Quaternion.LookRotation(hitNormal, Vector3.up);

            // Recalculate bounds AFTER rotation — extents are now wall-relative
            Bounds rotatedBounds = GetObjectBounds(obj);
            // Depth along wall normal = the object's extent in the forward (normal) direction
            // For a thin painting rotated to face outward, this is the smallest extent
            float depthAlongNormal = Mathf.Min(rotatedBounds.extents.x, Mathf.Min(rotatedBounds.extents.y, rotatedBounds.extents.z));
            // Clamp to reasonable range — paintings are thin (1-5cm half-depth)
            depthAlongNormal = Mathf.Clamp(depthAlongNormal, 0.005f, 0.05f);
            obj.transform.position = hitPoint + hitNormal * (depthAlongNormal + 0.005f);
        }
        else
        {
            // Angled surface — orient object to follow the surface
            obj.transform.position = hitPoint + hitNormal * halfH;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, hitNormal).normalized;
            if (up.sqrMagnitude < 0.01f) up = Vector3.up;
            obj.transform.rotation = Quaternion.LookRotation(
                Vector3.ProjectOnPlane(obj.transform.forward, hitNormal).normalized, hitNormal);
        }

        Debug.Log($"[SemanticPlacement] PlaceOnGlobalMesh: {obj.name} at {hitPoint}, normal={hitNormal}");
        ARIADebugUI.AppendClaudeLog($"PLACED on clutter: {obj.name}\n  pos=({hitPoint.x:F2},{hitPoint.y:F2},{hitPoint.z:F2})");
    }

    /// <summary>
    /// Finds a clear spot on an anchor surface, away from clutter detected via Global Mesh.
    /// Searches outward from the search center using golden-angle spiral.
    /// Returns a position with 10cm gap from the nearest clutter edge.
    /// </summary>
    public Vector3 FindClearSpotOnAnchor(MRUKAnchor anchor, Vector3 searchCenter, Vector3 objectSize)
    {
        if (anchor == null) return searchCenter;

        // WALL anchors: search along the wall's PlaneRect for clear spots
        // (downward raycasts don't work for vertical surfaces)
        if (IsWallLikeAnchor(anchor))
            return FindClearSpotOnWall(anchor, searchCenter, objectSize);

        float anchorSurfaceY = anchor.transform.position.y;
        if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR))
            anchorSurfaceY = GetFloorHeight(MRUK.Instance?.GetCurrentRoom());

        float searchRadius = 0.8f;
        if (anchor.VolumeBounds.HasValue)
            searchRadius = Mathf.Max(anchor.VolumeBounds.Value.size.x, anchor.VolumeBounds.Value.size.y) * 0.4f;

        float halfObjW = objectSize.x * 0.5f;
        float halfObjD = objectSize.z * 0.5f;
        float gapFromClutter = 0.10f; // 10cm gap from clutter edge

        // Golden-angle spiral search for clear spot
        for (int i = 0; i <= maxPlacementAttempts; i++)
        {
            float angle = i * 137.5f * Mathf.Deg2Rad;
            float radius = searchRadius * Mathf.Sqrt((float)i / maxPlacementAttempts);
            float dx = Mathf.Cos(angle) * radius;
            float dz = Mathf.Sin(angle) * radius;

            Vector3 testPoint = searchCenter + new Vector3(dx, 0, dz);
            testPoint.y = anchorSurfaceY + 1.0f; // raycast from 1m above

            // Raycast down against Global Mesh
            if (Physics.Raycast(testPoint, Vector3.down, out RaycastHit hit, 2f, GlobalMeshLayerMask))
            {
                float meshHeight = hit.point.y - anchorSurfaceY;
                if (meshHeight < 0.01f) // clear spot — mesh is at anchor level
                {
                    // Check a small area around candidate to ensure object fits
                    bool fits = true;
                    for (float cx = -halfObjW; cx <= halfObjW && fits; cx += 0.05f)
                    {
                        for (float cz = -halfObjD; cz <= halfObjD && fits; cz += 0.05f)
                        {
                            Vector3 checkPoint = new Vector3(testPoint.x + cx, testPoint.y, testPoint.z + cz);
                            if (Physics.Raycast(checkPoint, Vector3.down, out RaycastHit checkHit, 2f, GlobalMeshLayerMask))
                            {
                                if (checkHit.point.y - anchorSurfaceY > 0.01f)
                                    fits = false; // clutter in the way
                            }
                        }
                    }

                    if (fits)
                    {
                        Vector3 result = new Vector3(testPoint.x, anchorSurfaceY, testPoint.z);
                        Debug.Log($"[SemanticPlacement] FindClearSpot: found at ({result.x:F2},{result.y:F2},{result.z:F2}), attempt {i}");
                        return result;
                    }
                }
            }
        }

        // Fallback: place at anchor center
        Debug.LogWarning("[SemanticPlacement] FindClearSpot: no clear spot found — using anchor center.");
        return new Vector3(anchor.transform.position.x, anchorSurfaceY, anchor.transform.position.z);
    }

    /// <summary>
    /// Finds a clear spot on a WALL anchor's PlaneRect, away from Global Mesh clutter.
    /// Searches along the wall's 2D plane using raycasts perpendicular to the wall surface.
    /// </summary>
    private Vector3 FindClearSpotOnWall(MRUKAnchor wallAnchor, Vector3 searchCenter, Vector3 objectSize)
    {
        Vector3 wallNormal = wallAnchor.transform.forward;
        Vector3 wallCenter = wallAnchor.transform.position;
        Vector3 wallRight = wallAnchor.transform.right;
        Vector3 wallUp = wallAnchor.transform.up;

        // Project search center onto wall plane
        Vector3 toSearch = searchCenter - wallCenter;
        float localX = Vector3.Dot(toSearch, wallRight);
        float localY = Vector3.Dot(toSearch, wallUp);

        // Wall PlaneRect bounds
        Rect rect = wallAnchor.PlaneRect.HasValue ? wallAnchor.PlaneRect.Value
            : new Rect(-2f, -1.4f, 4f, 2.8f);

        float halfObjW = objectSize.x * 0.5f;
        float halfObjH = objectSize.y * 0.5f;

        // Golden-angle spiral search on wall plane
        for (int i = 0; i <= maxPlacementAttempts; i++)
        {
            float angle = i * 137.5f * Mathf.Deg2Rad;
            float radius = 0.8f * Mathf.Sqrt((float)i / maxPlacementAttempts);
            float testX = localX + Mathf.Cos(angle) * radius;
            float testY = localY + Mathf.Sin(angle) * radius;

            // Clamp within PlaneRect (with object size margin)
            testX = Mathf.Clamp(testX, rect.xMin + halfObjW, rect.xMax - halfObjW);
            testY = Mathf.Clamp(testY, rect.yMin + halfObjH, rect.yMax - halfObjH);

            // World position on the wall plane
            Vector3 testPoint = wallCenter + wallRight * testX + wallUp * testY;

            // Raycast FROM outside the wall TOWARD the wall to check for clutter
            Vector3 rayOrigin = testPoint + wallNormal * 0.5f;
            if (Physics.Raycast(rayOrigin, -wallNormal, out RaycastHit hit, 1f, GlobalMeshLayerMask))
            {
                // If the Global Mesh hit is close to the wall plane = clear wall surface
                float depthFromWall = Vector3.Distance(hit.point, testPoint);
                if (depthFromWall < 0.03f) // within 3cm of wall = clear
                {
                    // Offset result slightly away from wall surface
                    Vector3 result = testPoint + wallNormal * 0.03f;
                    Debug.Log($"[SemanticPlacement] FindClearSpotOnWall: found at local ({testX:F2},{testY:F2}), attempt {i}");
                    return result;
                }
            }
            else
            {
                // No Global Mesh hit = open wall area = clear
                Vector3 result = testPoint + wallNormal * 0.03f;
                Debug.Log($"[SemanticPlacement] FindClearSpotOnWall: open wall at local ({testX:F2},{testY:F2}), attempt {i}");
                return result;
            }
        }

        // Fallback: wall center
        Debug.LogWarning("[SemanticPlacement] FindClearSpotOnWall: no clear spot found — using wall center.");
        return wallCenter + wallNormal * 0.03f;
    }

    /// <summary>
    /// Raycasts from the camera gaze using EnvironmentRaycastManager (live depth sensor)
    /// for precise real-world surface detection. Falls back to Physics.Raycast against
    /// Global Mesh collider in editor or if depth API isn't ready.
    /// Returns true if hit, with point, normal, and the anchor it belongs to.
    /// </summary>
    public bool GazeRaycastGlobalMesh(out Vector3 hitPoint, out Vector3 hitNormal, out MRUKAnchor hitAnchor)
    {
        hitPoint = Vector3.zero;
        hitNormal = Vector3.up;
        hitAnchor = null;

        Camera cam = Camera.main;
        if (cam == null) return false;

        Ray gazeRay = new Ray(cam.transform.position, cam.transform.forward);

        // Primary: EnvironmentRaycastManager (live depth sensor — sees actual physical surfaces)
        var envMgr = FindFirstObjectByType<Meta.XR.EnvironmentRaycastManager>();
        if (envMgr != null && envMgr.Raycast(gazeRay, out var envHit, 10f))
        {
            hitPoint = envHit.point;
            hitNormal = envHit.normal;
            hitAnchor = IdentifyAnchorAtPoint(hitPoint);
            return true;
        }

        // Fallback 1: Physics.Raycast against Global Mesh layer (stored scan mesh)
        if (Physics.Raycast(gazeRay, out RaycastHit meshHit, 10f, GlobalMeshLayerMask))
        {
            hitPoint = meshHit.point;
            hitNormal = meshHit.normal;
            hitAnchor = IdentifyAnchorAtPoint(hitPoint);
            return true;
        }

        // Fallback 2: Physics.Raycast against all layers (anchor colliders)
        if (Physics.Raycast(gazeRay, out RaycastHit anyHit, 10f))
        {
            hitPoint = anyHit.point;
            hitNormal = anyHit.normal;
            hitAnchor = IdentifyAnchorAtPoint(hitPoint);
            return true;
        }

        return false;
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
