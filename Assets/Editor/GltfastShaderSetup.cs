// GltfastShaderSetup.cs
// Ensures GLTFast shader graphs survive build stripping by adding them
// to Always Included Shaders. Runs once on editor load.

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class GltfastShaderSetup
{
    static GltfastShaderSetup()
    {
        // Find GLTFast's URP shader graphs
        string[] shaderNames =
        {
            "Shader Graphs/glTF-pbrMetallicRoughness",
            "Shader Graphs/glTF-pbrSpecularGlossiness",
            "Shader Graphs/glTF-unlit",
            "glTF/PbrMetallicRoughness",
            "glTF/PbrSpecularGlossiness",
            "glTF/Unlit",
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
        };

        var graphicsSettings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>(
            "ProjectSettings/GraphicsSettings.asset");

        SerializedObject so = new SerializedObject(graphicsSettings);
        SerializedProperty shaders = so.FindProperty("m_AlwaysIncludedShaders");

        bool changed = false;
        foreach (string sn in shaderNames)
        {
            Shader s = Shader.Find(sn);
            if (s == null) continue;

            // Check if already included
            bool found = false;
            for (int i = 0; i < shaders.arraySize; i++)
            {
                if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == s)
                { found = true; break; }
            }

            if (!found)
            {
                shaders.InsertArrayElementAtIndex(shaders.arraySize);
                shaders.GetArrayElementAtIndex(shaders.arraySize - 1).objectReferenceValue = s;
                changed = true;
                Debug.Log($"[ARIA] Added \"{sn}\" to Always Included Shaders.");
            }
        }

        if (changed)
        {
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[ARIA] GLTFast shaders added to Always Included Shaders — rebuild required.");
        }
    }
}
