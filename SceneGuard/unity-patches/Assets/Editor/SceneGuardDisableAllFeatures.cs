using UnityEngine;
using UnityEditor;

/// <summary>
/// 一次性诊断：禁用 urp_renderer 上所有 RendererFeature，判断黑屏是否由某个 feature 导致。
/// </summary>
public static class SceneGuardDisableAllFeatures
{
    static void DisableAll()
    {
        string path = "Assets/Settings/urp_renderer.asset";
        var renderer = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        if (renderer == null)
        {
            Debug.LogWarning("[SceneGuard] urp_renderer.asset not found.");
            return;
        }

        var so = new SerializedObject(renderer);
        var list = so.FindProperty("m_RendererFeatures");
        if (list == null || !list.isArray)
        {
            Debug.LogWarning("[SceneGuard] m_RendererFeatures not found.");
            return;
        }

        int disabledCount = 0;
        for (int i = 0; i < list.arraySize; i++)
        {
            var elem = list.GetArrayElementAtIndex(i);
            var activeProp = elem.FindPropertyRelative("m_Active");
            if (activeProp != null && activeProp.boolValue)
            {
                activeProp.boolValue = false;
                disabledCount++;
            }
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(renderer);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        Debug.Log($"[SceneGuard] Disabled {disabledCount} features on urp_renderer. Magenta test will run on next domain reload.");
    }

    static void RestoreAll()
    {
        string path = "Assets/Settings/urp_renderer.asset";
        var renderer = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        if (renderer == null) return;

        var so = new SerializedObject(renderer);
        var list = so.FindProperty("m_RendererFeatures");
        if (list == null || !list.isArray) return;

        int enabledCount = 0;
        for (int i = 0; i < list.arraySize; i++)
        {
            var elem = list.GetArrayElementAtIndex(i);
            var activeProp = elem.FindPropertyRelative("m_Active");
            if (activeProp != null && !activeProp.boolValue)
            {
                activeProp.boolValue = true;
                enabledCount++;
            }
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(renderer);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        Debug.Log($"[SceneGuard] Restored {enabledCount} features on urp_renderer.");
    }
}
