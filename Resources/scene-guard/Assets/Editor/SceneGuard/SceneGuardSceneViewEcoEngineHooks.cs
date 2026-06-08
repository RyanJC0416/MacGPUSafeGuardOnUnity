#if UNITY_EDITOR
using System;
using System.Reflection;
using EcoEngine.BigWorld;
using EcoEngine.Rendering.Universal;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Optional lightweight SceneView prep (filter off, camera enabled). Post-URP draw lives in SceneGuardSceneViewFallbackRenderer.
/// Default OFF — use FallbackRenderer material restore instead.
/// </summary>
[InitializeOnLoad]
public static class SceneGuardSceneViewEcoEngineHooks
{
    private const string EnabledPrefsKey = "SceneGuard.EcoEngineHooks.Enabled";
    private static bool hooksAttached;

    private static readonly MethodInfo SetSceneViewFilteringMethod =
        typeof(SceneView).GetMethod("SetSceneViewFiltering", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo SceneViewSearchFilterField =
        typeof(SceneView).GetField("m_SearchFilter", BindingFlags.Instance | BindingFlags.NonPublic);

    static SceneGuardSceneViewEcoEngineHooks()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        if (EditorPrefs.GetBool(EnabledPrefsKey, false))
            Attach();
    }

    public static bool IsAttached => hooksAttached;

    public static void Attach()
    {
        if (Application.platform != RuntimePlatform.OSXEditor || hooksAttached)
            return;

        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        hooksAttached = true;
        Debug.Log("[SceneGuardEcoHooks] Attached (prep only: filter off + camera enabled). Drawing via FallbackRenderer.");
    }

    public static void Detach()
    {
        EditorApplication.update -= OnEditorUpdate;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        hooksAttached = false;
        Debug.Log("[SceneGuardEcoHooks] Detached.");
    }

    private static void OnEditorUpdate()
    {
        if (!hooksAttached)
            return;

        DisableSceneFilteringOnAllSceneViews();
    }

    private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!hooksAttached || camera == null || camera.cameraType != CameraType.SceneView)
            return;

        DisableSceneFilteringOnAllSceneViews();

        if (!camera.enabled)
            camera.enabled = true;

        ForceGpuBatcherSceneView();
    }

    private static void DisableSceneFilteringOnAllSceneViews()
    {
        foreach (object view in SceneView.sceneViews)
        {
            var sceneView = view as SceneView;
            if (sceneView == null)
                continue;

            if (SceneViewSearchFilterField != null)
            {
                string filter = SceneViewSearchFilterField.GetValue(sceneView) as string;
                if (!string.IsNullOrEmpty(filter))
                    SceneViewSearchFilterField.SetValue(sceneView, string.Empty);
            }

            SetSceneViewFilteringMethod?.Invoke(sceneView, new object[] { false });
        }
    }

    private static void ForceGpuBatcherSceneView()
    {
        GeometryInstancingManager.Instance.ShowInSceneView = true;
        if (GPUBatcherRendererFeature.Instance != null)
            GPUBatcherRendererFeature.Instance.ShowInSceneView = true;
    }
}
#endif
