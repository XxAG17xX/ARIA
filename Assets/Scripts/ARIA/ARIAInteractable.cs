// ARIAInteractable.cs
// Handles post-grab-release behavior for spawned virtual objects.
// Grab is triggered externally by ARIADebugUI (A button on right controller).
// Floor items: gravity drop onto nearest surface, then proportional resize.
// Wall items: magnet-snap to nearest wall within threshold, or fall if too far.

using System.Collections;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public enum SurfaceCategory { FloorItem, WallItem }

public class ARIAInteractable : MonoBehaviour
{
    [Header("Surface Classification")]
    public SurfaceCategory category = SurfaceCategory.FloorItem;
    public string objectCategory = ""; // text category ("bed", "painting", etc.)

    [Header("Wall Snap")]
    [Tooltip("Maximum distance from a wall to trigger magnet snap (metres).")]
    [SerializeField] private float wallSnapThreshold = 0.15f;

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

    // ── Floor items: fall with gravity, resize on landing ──────────────

    private void ApplyGravityDrop()
    {
        if (_rb == null) return;

        // Verify there's something below to land on (EffectMesh colliders)
        if (!Physics.Raycast(transform.position, Vector3.down, 10f))
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            Debug.LogWarning($"[ARIAInteractable] No ground below {gameObject.name}, keeping kinematic");
            return;
        }

        _rb.useGravity = true;
        _rb.isKinematic = false;
        _settleCoroutine = StartCoroutine(WaitForSettle());
    }

    private IEnumerator WaitForSettle()
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
            if (landedOn != null)
            {
                // Correct rotation to upright — floor/table items should stand up, not lie on side
                if (category == SurfaceCategory.FloorItem)
                {
                    Vector3 euler = transform.eulerAngles;
                    transform.rotation = Quaternion.Euler(0f, euler.y, 0f); // keep Y rotation, zero tilt
                }

                // Reset to original scale before fitting — prevents multiplicative shrink
                transform.localScale = originalScale;
                _placementEngine.FitToSurface(gameObject, landedOn, objectCategory);

                // Snap bottom to surface — fixes floating after physics settle
                Bounds b = ARIAOrchestrator.CalculateMeshBounds(gameObject);
                float surfaceY = landedOn.transform.position.y;
                float bottomToOrigin = transform.position.y - b.min.y;
                transform.position = new Vector3(transform.position.x, surfaceY + bottomToOrigin, transform.position.z);

                Debug.Log($"[ARIAInteractable] {gameObject.name} settled on {landedOn.name}");
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
