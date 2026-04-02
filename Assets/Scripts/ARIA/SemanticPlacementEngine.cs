// SemanticPlacementEngine.cs
// Validates Claude's surface assignment against a constraint table, corrects invalid assignments,
// then uses MRUK FindSpawnPositions to place the object on the correct real surface.
//
// STUB — full implementation in next session.

using UnityEngine;

public class SemanticPlacementEngine : MonoBehaviour
{
    /// <summary>
    /// Places <paramref name="obj"/> on the surface type indicated by <paramref name="surfaceLabel"/>.
    /// Valid labels: FLOOR, WALL_FACE, CEILING.
    /// Invalid assignments are corrected to the nearest valid surface.
    /// </summary>
    public void Place(GameObject obj, string surfaceLabel)
    {
        // TODO: implement constraint table + MRUK FindSpawnPositions
        Debug.Log($"[SemanticPlacement] Place \"{obj.name}\" on {surfaceLabel} (stub)");
    }
}
