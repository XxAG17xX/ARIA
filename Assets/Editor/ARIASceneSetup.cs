using UnityEngine;
using UnityEditor;
using System.Reflection;

public static class ARIASceneSetup
{
    [MenuItem("ARIA/Wire ARIA_Manager")]
    public static void WireManager()
    {
        var manager = GameObject.Find("ARIA_Manager");
        if (manager == null) { Debug.LogError("[ARIA Setup] ARIA_Manager not found."); return; }

        var orch = manager.GetComponent<ARIAOrchestrator>();
        if (orch == null) { Debug.LogError("[ARIA Setup] ARIAOrchestrator not found."); return; }

        var t = typeof(ARIAOrchestrator);
        void Set(string field, object val) {
            var f = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) f.SetValue(orch, val);
            else Debug.LogWarning("[ARIA Setup] Missing field: " + field);
        }

        Set("placementEngine", manager.GetComponent<SemanticPlacementEngine>());
        Set("scaleSystem",     manager.GetComponent<ScaleInferenceSystem>());
        Set("shEstimator",     manager.GetComponent<SphericalHarmonicsLightingEstimator>());
        Set("shadowReceiver",  manager.GetComponent<ShadowReceiverSetup>());
        Set("spawnRoot",       manager.GetComponent<Transform>());

        var light = GameObject.Find("Directional Light")?.GetComponent<Light>();
        Set("sceneDirectionalLight", light);

        EditorUtility.SetDirty(manager);
        Debug.Log("[ARIA Setup] All fields wired on ARIA_Manager.");
    }
}
