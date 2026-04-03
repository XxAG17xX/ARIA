// SemanticPlacementEngine.cs
// Places a spawned object at the correct real-world position using MRUK anchor data.
// Maps Claude's surface_label to MRUKAnchor.SceneLabels, finds matching anchors,
// and positions the object using the appropriate placement strategy per surface type.
// Falls back to 1.5m in front of the user's camera in editor (no MRUK available).

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

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Positions <paramref name="obj"/> on the surface indicated by <paramref name="surfaceLabel"/>.
    /// Valid labels: FLOOR, WALL_FACE, CEILING, TABLE, COUCH, OTHER (corrected to FLOOR).
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
    // MRUK placement (Quest APK)
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

    private void PlaceOnFloor(GameObject obj, MRUKRoom room)
    {
        // Prefer a position 1.5m in front of the camera on the floor,
        // so "place a chair" lands where the user is looking, not randomly.
        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 target = cam.transform.position + forward * 1.5f;

            // Project onto floor height (y=0 in most MRUK rooms, or use floor anchor y)
            var floorAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR));
            float floorY = floorAnchor != null ? floorAnchor.transform.position.y : 0f;

            obj.transform.position = new Vector3(target.x, floorY, target.z);
            obj.transform.rotation = Quaternion.identity;
            return;
        }

        // Fallback: random on floor surface
        bool found = room.GenerateRandomPositionOnSurface(
            MRUK.SurfaceType.FACING_UP,
            floorClearanceRadius,
            new LabelFilter(MRUKAnchor.SceneLabels.FLOOR),
            out Vector3 pos,
            out Vector3 normal);

        if (!found)
        {
            var floorAnchor = room.Anchors.FirstOrDefault(
                a => a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR));
            pos = floorAnchor != null ? floorAnchor.transform.position : Vector3.zero;
        }

        obj.transform.position = pos;
        obj.transform.rotation = Quaternion.identity;
    }

    private void PlaceOnWall(GameObject obj, MRUKRoom room)
    {
        // Find the wall anchor closest to the user's current position
        var userPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;

        var wallAnchor = room.Anchors
            .Where(a => a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE))
            .OrderBy(a => Vector3.Distance(a.transform.position, userPos))
            .FirstOrDefault();

        if (wallAnchor == null)
        {
            PlaceEditorFallback(obj, "WALL_FACE");
            return;
        }

        // Position: surface of wall + offset so object doesn't clip
        Vector3 wallNormal = wallAnchor.transform.forward; // forward = normal pointing into room
        Vector3 wallPos    = wallAnchor.transform.position;
        float   halfDepth  = GetObjectHalfDepth(obj);

        obj.transform.position = wallPos + wallNormal * (halfDepth + wallOffset);
        // Face into the room (rotate so object's forward matches wall normal)
        obj.transform.rotation = Quaternion.LookRotation(wallNormal, Vector3.up);
    }

    private void PlaceOnCeiling(GameObject obj, MRUKRoom room)
    {
        var ceilingAnchor = room.Anchors.FirstOrDefault(
            a => a.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING));

        Vector3 pos = ceilingAnchor != null
            ? ceilingAnchor.transform.position + Vector3.down * GetObjectHalfDepth(obj)
            : new Vector3(0f, 2.5f, 0f);

        obj.transform.position = pos;
        obj.transform.rotation = Quaternion.identity;
    }

    private void PlaceOnVolumeSurface(GameObject obj, MRUKRoom room, MRUKAnchor.SceneLabels targetLabel)
    {
        var anchor = room.Anchors
            .Where(a => a.HasAnyLabel(targetLabel))
            .OrderBy(a => Vector3.Distance(
                a.transform.position,
                Camera.main != null ? Camera.main.transform.position : Vector3.zero))
            .FirstOrDefault();

        if (anchor == null)
        {
            Debug.LogWarning($"[SemanticPlacement] No {targetLabel} anchor found — placing on floor.");
            PlaceOnFloor(obj, room);
            return;
        }

        // For volume anchors (TABLE, COUCH), position is top-centre of volume
        // Offset upward by half the object's height so it sits on the surface
        float halfH = GetObjectHalfHeight(obj);
        obj.transform.position = anchor.transform.position + Vector3.up * halfH;
        obj.transform.rotation = Quaternion.identity;
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
        if (forward == Vector3.zero) forward = Vector3.forward;
        forward.Normalize();

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
                obj.transform.position = new Vector3(basePos.x, 0f, basePos.z);
                obj.transform.rotation = Quaternion.identity;
                break;
        }

        Debug.Log($"[SemanticPlacement] Editor fallback: placed \"{obj.name}\" at {obj.transform.position}");
    }

    // -------------------------------------------------------------------------
    // Geometry helpers
    // -------------------------------------------------------------------------

    private static float GetObjectHalfHeight(GameObject obj)
    {
        var renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return 0.1f;
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b.extents.y;
    }

    private static float GetObjectHalfDepth(GameObject obj)
    {
        var renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return 0.05f;
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return Mathf.Min(b.extents.x, b.extents.z);
    }
}
