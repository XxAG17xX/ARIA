// ARIAInteractable.cs — what happens when you grab and let go of objects
// attached to every spawned object. classifies them as FloorItem, WallItem, or ClutterItem.
// floor stuff falls with gravity and settles on whatever surface it lands on (uses GlobalMesh
// raycast so it lands on actual clutter, not just flat anchor planes).
// wall stuff snaps to nearest wall if within 8cm, otherwise falls.
// clutter stuff is like floor but skips the rotation correction (keeps its surface angle).
// tracks originalScale so repeated grab-release doesn't keep shrinking the object.

using System.Collections;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public enum SurfaceCategory { FloorItem, WallItem, ClutterItem }

public class ARIAInteractable : MonoBehaviour
{
    [Header("Surface Classification")]
    public SurfaceCategory category = SurfaceCategory.FloorItem;
    public string objectCategory = ""; // text category ("bed", "painting", etc.)

    [Header("Wall Snap")]
    [Tooltip("Maximum distance from a wall to trigger magnet snap (metres).")]
    [SerializeField] private float wallSnapThreshold = 0.08f;

    [Header("Settle Detection")]
    [Tooltip("Seconds to wait before checking if object has settled after drop.")]
    [SerializeField] private float settleDelay = 0.8f;
    [Tooltip("Maximum seconds to wait for object to stop moving.")]
    [SerializeField] private float settleTimeout = 5f;

    private Rigidbody _rb;
    private SemanticPlacementEngine _placementEngine;
    private Coroutine _settleCoroutine;
    private bool _isGrabbed;

    /// <summary>Original scale at spawn time — used to reset before FitToSurface so shrink isn't multiplicative.</summary>
    [HideInInspector] public Vector3 originalScale = Vector3.one;

    /// <summary>When true, FitToSurface is skipped on grab-release. Set by Claude adjustment or thumbstick scaling.</summary>
    [HideInInspector] public bool disableAutoResize;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _placementEngine = FindFirstObjectByType<SemanticPlacementEngine>();
    }

    private void Start()
    {
        // Capture the post-placement scale as the "original" (after ApplyScale + FitToAvailableSpace)
        originalScale = transform.localScale;
    }

    // ── Called by ARIADebugUI when A button pressed/released ────────────

    /// <summary>Start grab: make kinematic, stop physics.</summary>
    public void StartGrab()
    {
        _isGrabbed = true;
        if (_settleCoroutine != null)
        {
            StopCoroutine(_settleCoroutine);
            _settleCoroutine = null;
        }
        if (_rb != null)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
        }
        Debug.Log($"[ARIAInteractable] {gameObject.name} grabbed");
    }

    /// <summary>End grab: trigger surface-appropriate behavior.</summary>
    public void EndGrab()
    {
        _isGrabbed = false;
        Debug.Log($"[ARIAInteractable] {gameObject.name} released (category={category})");

        if (category == SurfaceCategory.WallItem)
            TryWallSnap();
        else
            ApplyGravityDrop();
    }

    public bool IsGrabbed => _isGrabbed;

    // called by orchestrator after Claude adjustment changes position/scale.
    // triggers gravity so the object settles naturally on the Global Mesh.
    public void TriggerGravitySettle()
    {
        if (_isGrabbed) return; // don't mess with grabbed objects
        ApplyGravityDrop();
    }

    // ── Floor items: fall with gravity, resize on landing ──────────────

    private void ApplyGravityDrop()
    {
        if (_rb == null) return;

        // check if there's Global Mesh or anchor collider below to land on.
        // if the object is on a real-world surface that only EnvironmentRaycast sees
        // (no Global Mesh, e.g. object placed after room scan), keep kinematic —
        // gravity would make it fall through to the floor since there's no collider.
        int gmLayer = LayerMask.GetMask("GlobalMesh");
        bool hasGlobalMesh = Physics.Raycast(transform.position, Vector3.down, 2f, gmLayer);
        bool hasAnyGround = Physics.Raycast(transform.position, Vector3.down, 10f);

        if (!hasAnyGround)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            Debug.LogWarning($"[ARIAInteractable] No ground below {gameObject.name}, keeping kinematic");
            return;
        }

        if (!hasGlobalMesh)
        {
            // on a surface only visible to EnvironmentRaycast (post-scan object).
            // stay kinematic — gravity would drop it to the anchor floor below.
            _rb.isKinematic = true;
            _rb.useGravity = false;
            Debug.Log($"[ARIAInteractable] {gameObject.name} on non-mesh surface, keeping kinematic until grabbed");
            return;
        }

        _rb.useGravity = true;
        _rb.isKinematic = false;
        _settleCoroutine = StartCoroutine(WaitForSettle());
    }

    public IEnumerator WaitForSettle()
    {
        yield return new WaitForSeconds(settleDelay);

        float elapsed = 0f;
        while (elapsed < settleTimeout)
        {
            // Abort if re-grabbed
            if (_rb == null || _isGrabbed)
            {
                _settleCoroutine = null;
                yield break;
            }

            if (_rb.linearVelocity.sqrMagnitude < 0.003f)
                break;

            elapsed += Time.deltaTime;
            yield return null;
        }

        // Freeze in place
        if (_rb != null)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        // Detect what surface we landed on and resize
        if (_placementEngine != null)
        {
            MRUKAnchor landedOn = _placementEngine.DetectSurfaceBelow(transform.position);

            // Snap Y to the ACTUAL surface (GlobalMesh or EnvironmentRaycast) not just the anchor plane.
            float actualSurfaceY = landedOn?.transform.position.y ?? 0f;
            int globalMeshLayer = LayerMask.GetMask("GlobalMesh");
            Vector3 rayOrigin = transform.position + Vector3.up * 0.5f;
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit meshHit, 2f, globalMeshLayer))
            {
                actualSurfaceY = Mathf.Max(actualSurfaceY, meshHit.point.y);
            }

            if (landedOn != null)
            {
                // Correct rotation to upright — floor/table items should stand up, not lie on side
                // ClutterItems skip this (they were oriented to a surface normal and should keep it)
                if (category == SurfaceCategory.FloorItem)
                {
                    Vector3 euler = transform.eulerAngles;
                    transform.rotation = Quaternion.Euler(0f, euler.y, 0f);
                }

                // auto-resize to fit surface — unless user manually set the size
                // (via Claude adjustment or thumbstick). once manually sized, stays that size forever.
                if (!disableAutoResize)
                {
                    transform.localScale = originalScale;
                    _placementEngine.FitToSurface(gameObject, landedOn, objectCategory);
                }

                // Snap bottom to actual surface (GlobalMesh Y, not just anchor Y)
                Bounds b = ARIAOrchestrator.CalculateMeshBounds(gameObject);
                float bottomToOrigin = transform.position.y - b.min.y;
                transform.position = new Vector3(transform.position.x, actualSurfaceY + bottomToOrigin, transform.position.z);

                Debug.Log($"[ARIAInteractable] {gameObject.name} settled on {landedOn.name} " +
                          $"(anchorY={landedOn.transform.position.y:F2}, meshY={actualSurfaceY:F2})");
            }
        }

        _settleCoroutine = null;
    }

    // ── Wall items: snap to nearest wall or fall ───────────────────────

    private void TryWallSnap()
    {
        if (_placementEngine == null || _rb == null)
        {
            ApplyGravityDrop();
            return;
        }

        float dist = _placementEngine.FindNearestWall(
            transform.position, out Vector3 surfacePos, out Vector3 wallNormal, out MRUKAnchor wallAnchor);

        if (dist <= wallSnapThreshold && wallAnchor != null)
        {
            Bounds b = ARIAOrchestrator.CalculateMeshBounds(gameObject);
            float halfDepth = Mathf.Min(b.extents.x, b.extents.z);

            Vector3 snapPos = surfacePos + wallNormal * (halfDepth + 0.03f);
            snapPos.y = transform.position.y; // keep user-chosen height

            // Clamp Y within wall bounds
            if (wallAnchor.PlaneRect.HasValue)
            {
                Vector3 localPos = wallAnchor.transform.InverseTransformPoint(snapPos);
                Rect rect = wallAnchor.PlaneRect.Value;
                localPos.y = Mathf.Clamp(localPos.y, rect.yMin + b.extents.y, rect.yMax - b.extents.y);
                snapPos = wallAnchor.transform.TransformPoint(localPos);
            }

            transform.position = snapPos;
            transform.rotation = Quaternion.LookRotation(wallNormal, Vector3.up);

            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;

            // Reset to original scale before fitting — prevents multiplicative shrink
            transform.localScale = originalScale;
            _placementEngine.FitToSurface(gameObject, wallAnchor, objectCategory);

            Debug.Log($"[ARIAInteractable] {gameObject.name} snapped to wall {wallAnchor.name} (dist={dist:F2}m)");
            ARIADebugUI.AppendClaudeLog($"{gameObject.name} snapped to {wallAnchor.name}");
        }
        else
        {
            Debug.Log($"[ARIAInteractable] {gameObject.name} too far from wall ({dist:F2}m), dropping");
            ApplyGravityDrop();
        }
    }

    // ── Classification helper ─────────────────────────────────────────

    private static readonly string[] WallCategories =
    {
        "painting", "wall_art", "clock", "mirror", "shelf", "poster",
        "picture", "frame", "wall decor", "sconce", "tapestry"
    };

    public static SurfaceCategory ClassifyObject(string category, string surfaceLabel)
    {
        // Clutter-placed objects skip rotation snap on settle
        if (!string.IsNullOrEmpty(surfaceLabel) &&
            surfaceLabel.Equals("CLUTTER", System.StringComparison.OrdinalIgnoreCase))
            return SurfaceCategory.ClutterItem;

        if (!string.IsNullOrEmpty(surfaceLabel) &&
            surfaceLabel.Equals("WALL_FACE", System.StringComparison.OrdinalIgnoreCase))
            return SurfaceCategory.WallItem;

        if (!string.IsNullOrEmpty(category))
        {
            string lower = category.ToLowerInvariant();
            foreach (string wallCat in WallCategories)
            {
                if (lower.Contains(wallCat))
                    return SurfaceCategory.WallItem;
            }
        }

        return SurfaceCategory.FloorItem;
    }
}
