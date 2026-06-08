using System.IO;
using EcoEngine.Rendering.Universal;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Mac Editor SceneView repair (Metal / URP).
/// Stable path: endCameraRendering → clear RT → DrawRenderers (original materials) → DrawGizmos → Submit.
/// Gizmos: ScriptableRenderContext.DrawGizmos in the repair pass + Handles.DrawGizmos in duringSceneGui.
/// Play-mode game-camera mirror is kept in source but disabled (Camera.Render side effects).
/// </summary>
[InitializeOnLoad]
public static class SceneGuardSceneViewFallbackRenderer
{
    /// <summary>Experimental play mirror; off by default — causes game LOD flicker / multi-camera conflicts.</summary>
    private const bool PlayMirrorPathEnabled = false;
    private enum CaptureMode
    {
        DrawRendererNoSubmit,
        DrawRendererWithSubmit,
        ClearWithSubmit,
        SubmitOnly
    }

    private enum RepairMode
    {
        SubmitOnly = 0,
        OriginalMaterials = 1,
        ProxyFallback = 2,
        SubmitThenOriginalMaterials = 3,
        MirrorGameCameraInPlay = 4
    }

    private const string AutoMirrorInPlayPrefsKey = "SceneGuard.SceneViewFallbackRenderer.AutoMirrorInPlay";
    private const string EditorPrefsKey = "SceneGuard.SceneViewFallbackRenderer.Enabled";
    private const string RepairModePrefsKey = "SceneGuard.SceneViewFallbackRenderer.RepairMode";
    private const string DiagnosticsPrefsKey = "SceneGuard.SceneViewFallbackRenderer.Diagnostics";
    private const string NativePipelineMigrationPrefsKey = "SceneGuard.SceneViewFallbackRenderer.NativePipeline20260608";
    private const string MaterialRestoreMigrationPrefsKey = "SceneGuard.SceneViewFallbackRenderer.MaterialRestore20260608";
    private const string PlayMirrorStabilityPrefsKey = "SceneGuard.SceneViewFallbackRenderer.PlayMirrorStability20260608";
    private const string MirrorExcludeUiPrefsKey = "SceneGuard.SceneViewFallbackRenderer.MirrorExcludeUi";
    private const string CommandFile = "Library/SceneGuard/command.txt";
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int LightDirId = Shader.PropertyToID("_LightDir");
    private static readonly int LightColorId = Shader.PropertyToID("_LightColor");
    private static readonly int AmbientColorId = Shader.PropertyToID("_AmbientColor");
    private static readonly int TopColorId = Shader.PropertyToID("_TopColor");
    private static readonly int BottomColorId = Shader.PropertyToID("_BottomColor");
    private static readonly int LightingScaleId = Shader.PropertyToID("_LightingScale");
    private static readonly int MainLightPositionId = Shader.PropertyToID("_MainLightPosition");
    private static readonly int MainLightColorId = Shader.PropertyToID("_MainLightColor");
    private static readonly int MainLightOcclusionProbesId = Shader.PropertyToID("_MainLightOcclusionProbes");
    private static readonly int CameraOpaqueTextureId = Shader.PropertyToID("_CameraOpaqueTexture");
    private static readonly int ExposureEnabledId = Shader.PropertyToID("_ExposureEnabled");
    private static readonly int VolumetricCloudsEnabledId = Shader.PropertyToID("_VolumetricCloudsEnabled");
    private static readonly ShaderTagId[] ForwardShaderTags =
    {
        new ShaderTagId("ForwardLit"),
        new ShaderTagId("ForwardOnly"),
        new ShaderTagId("UniversalForwardOnly"),
        new ShaderTagId("UniversalForward"),
        new ShaderTagId("Forward"),
        new ShaderTagId("SRPDefaultUnlit"),
    };
    private static Material skyboxMaterial;
    private static Material litFallbackMaterial;
    private static Material waterFallbackMaterial;
    private static Mesh skyboxMesh;
    private static Camera playMirrorCamera;
    private static RenderTexture playMirrorRT;
    private static RenderTexture opaqueCopyRT;
    private static bool persistentFallbackAttached;
    private static bool persistentBeginAttached;
    private static int lastDrawnCount = -1;
    private static double lastLogTime;
    private static double nextCommandPollTime;
    private static bool readbackPending;
    private static bool captureOnceRequested;
    private static CaptureMode captureMode;
    private static int renderPipelineDepth;
    private static bool mirrorRendering;
    private static bool playMirrorReady;
    private static double nextMirrorRenderTime;

    static SceneGuardSceneViewFallbackRenderer()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        EditorApplication.quitting += Cleanup;
        EditorApplication.update += OnEditorUpdate;
        if (PlayMirrorPathEnabled)
            EditorApplication.update += OnMirrorPreRenderUpdate;
        SceneView.duringSceneGui += OnDuringSceneGuiGizmos;
        RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
        RenderPipelineManager.endContextRendering += OnEndContextRendering;
        if (!EditorPrefs.HasKey(NativePipelineMigrationPrefsKey))
        {
            EditorPrefs.SetBool(EditorPrefsKey, false);
            EditorPrefs.SetBool(NativePipelineMigrationPrefsKey, true);
        }

        if (!EditorPrefs.HasKey(MaterialRestoreMigrationPrefsKey))
        {
            EditorPrefs.SetBool(EditorPrefsKey, true);
            EditorPrefs.SetInt(RepairModePrefsKey, (int)RepairMode.SubmitThenOriginalMaterials);
            EditorPrefs.SetBool("SceneGuard.EcoEngineHooks.Enabled", false);
            EditorPrefs.SetBool(MaterialRestoreMigrationPrefsKey, true);
        }

        // Historical migrations (play mirror off by default).
        if (!EditorPrefs.HasKey(PlayMirrorStabilityPrefsKey))
        {
            EditorPrefs.SetBool(AutoMirrorInPlayPrefsKey, false);
            if (GetRepairMode() == RepairMode.MirrorGameCameraInPlay)
                EditorPrefs.SetInt(RepairModePrefsKey, (int)RepairMode.SubmitThenOriginalMaterials);
            EditorPrefs.SetBool(PlayMirrorStabilityPrefsKey, true);
        }

        ApplyPersistentFallbackState();
        if (IsEnabled())
            Debug.Log("[SceneGuard] Mac SceneView material restore ON (Original Materials + Submit). EcoHooks OFF.");
        else
            Debug.Log("[SceneGuardDiag] SceneView repair OFF. Enable via Performance/SceneGuard/SceneView TCRender Fallback Enabled.");
    }

    [MenuItem("Performance/SceneGuard/Re-enable Sky RendererFeatures", false, 313)]
    private static void MenuReenableSkyFeatures()
    {
        ReenableSkyRendererFeatures();
        SceneView.RepaintAll();
    }

    // --- Play mirror menus (disabled; see PlayMirrorPathEnabled) ---
    // [MenuItem("Performance/SceneGuard/Repair Mode/Mirror Game Camera (Play)", false, 308)]
    // private static void SetModeMirrorGameCamera() => SetRepairMode(RepairMode.MirrorGameCameraInPlay);
    // [MenuItem("Performance/SceneGuard/Auto Mirror Game Camera In Play", false, 307)]
    // private static void ToggleAutoMirrorInPlay() { ... }
    // [MenuItem("Performance/SceneGuard/Mirror Exclude UI Layer", false, 306)]
    // private static void ToggleMirrorExcludeUi() { ... }

    [MenuItem("Performance/SceneGuard/Repair Mode/Submit + Original Materials", false, 309)]
    private static void SetModeSubmitThenOriginal() => SetRepairMode(RepairMode.SubmitThenOriginalMaterials);

    [MenuItem("Performance/SceneGuard/Repair Mode/Submit + Original Materials", true)]
    private static bool SetModeSubmitThenOriginalValidate()
    {
        Menu.SetChecked("Performance/SceneGuard/Repair Mode/Submit + Original Materials", GetRepairMode() == RepairMode.SubmitThenOriginalMaterials);
        return Application.platform == RuntimePlatform.OSXEditor;
    }

    [MenuItem("Performance/SceneGuard/Repair Mode/Submit Only", false, 310)]
    private static void SetModeSubmitOnly() => SetRepairMode(RepairMode.SubmitOnly);

    [MenuItem("Performance/SceneGuard/Repair Mode/Submit Only", true)]
    private static bool SetModeSubmitOnlyValidate()
    {
        Menu.SetChecked("Performance/SceneGuard/Repair Mode/Submit Only", GetRepairMode() == RepairMode.SubmitOnly);
        return Application.platform == RuntimePlatform.OSXEditor;
    }

    [MenuItem("Performance/SceneGuard/Repair Mode/Original Materials", false, 311)]
    private static void SetModeOriginalMaterials() => SetRepairMode(RepairMode.OriginalMaterials);

    [MenuItem("Performance/SceneGuard/Repair Mode/Original Materials", true)]
    private static bool SetModeOriginalMaterialsValidate()
    {
        Menu.SetChecked("Performance/SceneGuard/Repair Mode/Original Materials", GetRepairMode() == RepairMode.OriginalMaterials);
        return Application.platform == RuntimePlatform.OSXEditor;
    }

    [MenuItem("Performance/SceneGuard/Repair Mode/Proxy Fallback (Legacy)", false, 312)]
    private static void SetModeProxyFallback() => SetRepairMode(RepairMode.ProxyFallback);

    [MenuItem("Performance/SceneGuard/Repair Mode/Proxy Fallback (Legacy)", true)]
    private static bool SetModeProxyFallbackValidate()
    {
        Menu.SetChecked("Performance/SceneGuard/Repair Mode/Proxy Fallback (Legacy)", GetRepairMode() == RepairMode.ProxyFallback);
        return Application.platform == RuntimePlatform.OSXEditor;
    }

    private static void SetRepairMode(RepairMode mode)
    {
        EditorPrefs.SetInt(RepairModePrefsKey, (int)mode);
        if (mode != RepairMode.MirrorGameCameraInPlay)
        {
            playMirrorReady = false;
            ReleasePlayMirrorCamera();
        }

        Debug.Log($"[SceneGuard] SceneView repair mode set to {mode}.");
        SceneView.RepaintAll();
    }

    private static RepairMode GetRepairMode()
    {
        return (RepairMode)EditorPrefs.GetInt(RepairModePrefsKey, (int)RepairMode.SubmitThenOriginalMaterials);
    }

    private static void ReenableSkyRendererFeatures()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        string[] skyPatterns = { "NepheleSky", "GlobalVolumeCloud", "SkyAtmosphere", "Skybox" };
        string[] rendererPaths =
        {
            "Assets/Settings/urp_renderer.asset",
            "Assets/Settings/urp_role_renderer.asset",
            "Assets/Settings/urp_renderer_for_ui_scene.asset"
        };

        int enabledCount = 0;
        foreach (string path in rendererPaths)
        {
            ScriptableObject renderer = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (renderer == null)
                continue;

            SerializedObject so = new SerializedObject(renderer);
            SerializedProperty features = so.FindProperty("m_RendererFeatures");
            if (features == null || !features.isArray)
                continue;

            for (int i = 0; i < features.arraySize; i++)
            {
                Object featureRef = features.GetArrayElementAtIndex(i).objectReferenceValue;
                if (featureRef == null)
                    continue;

                SerializedObject featureSo = new SerializedObject(featureRef);
                string featureName = featureSo.FindProperty("m_Name")?.stringValue ?? featureRef.name;
                bool matchesSky = false;
                foreach (string pattern in skyPatterns)
                {
                    if (featureName.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchesSky = true;
                        break;
                    }
                }

                if (!matchesSky)
                    continue;

                SerializedProperty activeProp = featureSo.FindProperty("m_Active");
                if (activeProp == null || activeProp.boolValue)
                    continue;

                activeProp.boolValue = true;
                featureSo.ApplyModifiedProperties();
                enabledCount++;
            }
        }

        if (enabledCount > 0)
            Debug.Log($"[SceneGuard] Re-enabled {enabledCount} sky RendererFeature(s) for SceneView (in-memory).");
    }

    private static void PrepareSceneViewCamera(Camera camera)
    {
        if (camera == null || camera.cameraType != CameraType.SceneView)
            return;

        if (!camera.enabled)
        {
            camera.enabled = true;
            Debug.Log("[SceneGuard] SceneView camera was disabled; re-enabled.");
        }
    }

    private static void SetupUrLightingGlobals()
    {
        Light main = GetSceneDirectionalLight();
        if (main != null)
        {
            Vector3 dir = -main.transform.forward;
            Shader.SetGlobalVector(MainLightPositionId, new Vector4(dir.x, dir.y, dir.z, 0f));

            Color lightColor = main.color * main.intensity;
            Shader.SetGlobalColor(MainLightColorId, lightColor);
        }
        else
        {
            Shader.SetGlobalVector(MainLightPositionId, new Vector4(0.32f, -0.77f, 0.56f, 0f));
            Shader.SetGlobalColor(MainLightColorId, Color.white);
        }

        Shader.SetGlobalVector(MainLightOcclusionProbesId, Vector4.zero);
        // Do not set _MainLightLayerMask here — URP already owns it as uint; SetGlobalInteger conflicts on Metal.
        Shader.SetGlobalColor("_SubtractiveShadowColor", RenderSettings.subtractiveShadowColor);
    }

    /// <summary>
    /// Fallback redraw lacks URP eye-adaptation / volume stack; disable exposure and cloud blend hooks that blow up water on Metal.
    /// </summary>
    private static void StabilizeSceneViewShaderGlobals()
    {
        Shader.SetGlobalFloat(ExposureEnabledId, 0f);
        Shader.SetGlobalFloat(VolumetricCloudsEnabledId, 0f);
    }

    [MenuItem("Performance/SceneGuard/SceneView TCRender Fallback Enabled", false, 320)]
    private static void ToggleEnabled()
    {
        EditorPrefs.SetBool(EditorPrefsKey, !IsEnabled());
        ApplyPersistentFallbackState();
        SceneView.RepaintAll();
    }

    [MenuItem("Performance/SceneGuard/SceneView TCRender Fallback Enabled", true)]
    private static bool ToggleEnabledValidate()
    {
        Menu.SetChecked("Performance/SceneGuard/SceneView TCRender Fallback Enabled", IsEnabled());
        return Application.platform == RuntimePlatform.OSXEditor;
    }

    [MenuItem("Performance/SceneGuard/SceneView TCRender Diagnostics", false, 321)]
    private static void ToggleDiagnostics()
    {
        EditorPrefs.SetBool(DiagnosticsPrefsKey, !DiagnosticsEnabled());
        SceneView.RepaintAll();
    }

    [MenuItem("Performance/SceneGuard/SceneView TCRender Diagnostics", true)]
    private static bool ToggleDiagnosticsValidate()
    {
        Menu.SetChecked("Performance/SceneGuard/SceneView TCRender Diagnostics", DiagnosticsEnabled());
        return Application.platform == RuntimePlatform.OSXEditor;
    }

    [MenuItem("Performance/SceneGuard/Capture SceneView Render Chain Once", false, 322)]
    private static void CaptureOnce()
    {
        RequestCapture(CaptureMode.DrawRendererNoSubmit);
    }

    [MenuItem("Performance/SceneGuard/Capture SceneView DrawRenderer + Submit Once", false, 323)]
    private static void CaptureDrawRendererWithSubmitOnce()
    {
        RequestCapture(CaptureMode.DrawRendererWithSubmit);
    }

    [MenuItem("Performance/SceneGuard/Capture SceneView Clear + Submit Once", false, 324)]
    private static void CaptureClearWithSubmitOnce()
    {
        RequestCapture(CaptureMode.ClearWithSubmit);
    }

    [MenuItem("Performance/SceneGuard/Capture SceneView Submit Only Once", false, 326)]
    private static void CaptureSubmitOnlyOnce()
    {
        RequestCapture(CaptureMode.SubmitOnly);
    }

    [MenuItem("Performance/SceneGuard/Capture SceneView Lighting State Once", false, 325)]
    private static void CaptureLightingStateOnce()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        Camera sceneCamera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
        if (sceneCamera != null)
        {
            LogCameraState("lighting-diagnostic", sceneCamera);
            LogRenderers(sceneCamera);
        }
        else
        {
            Debug.LogWarning("[SceneGuardDiag] lighting diagnostic: no active SceneView camera.");
        }

        LogLightingState();
    }

    private static void OnBeginContextRendering(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras)
    {
        renderPipelineDepth++;
    }

    private static void OnEndContextRendering(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras)
    {
        renderPipelineDepth = Mathf.Max(0, renderPipelineDepth - 1);
    }

    #region Play mirror (experimental, disabled)

    private static bool MirrorExcludeUiEnabled()
    {
        return EditorPrefs.GetBool(MirrorExcludeUiPrefsKey, true);
    }

    /// <summary>Pre-render game pipeline into playMirrorRT (SRP must be idle).</summary>
    private static void OnMirrorPreRenderUpdate()
    {
        if (!ShouldUsePlayMirror())
        {
            playMirrorReady = false;
            ReleasePlayMirrorCamera();
            return;
        }

        if (renderPipelineDepth > 0 || mirrorRendering)
            return;

        if (EditorApplication.timeSinceStartup < nextMirrorRenderTime)
            return;

        SceneView sceneView = SceneView.lastActiveSceneView;
        Camera sceneCamera = sceneView != null ? sceneView.camera : null;
        if (sceneCamera == null)
            return;

        int width = sceneCamera.pixelWidth;
        int height = sceneCamera.pixelHeight;
        if (width <= 0 || height <= 0)
            return;

        if (TryRenderPlayMirror(sceneCamera, width, height))
        {
            playMirrorReady = true;
            nextMirrorRenderTime = EditorApplication.timeSinceStartup + 0.05;
        }
    }

    private static bool ShouldUsePlayMirror()
    {
        if (!Application.isPlaying || !IsEnabled())
            return false;
        if (GetRepairMode() != RepairMode.MirrorGameCameraInPlay)
            return false;
        return EditorWindow.focusedWindow is SceneView;
    }

    #endregion

    private static void OnEditorUpdate()
    {
        if (EditorApplication.timeSinceStartup < nextCommandPollTime)
            return;

        nextCommandPollTime = EditorApplication.timeSinceStartup + 0.5;
        if (!File.Exists(CommandFile))
            return;

        string command = "";
        try
        {
            command = File.ReadAllText(CommandFile).Trim();
            File.Delete(CommandFile);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[SceneGuardDiag] command bridge failed to read command: " + ex.Message);
            return;
        }

        ExecuteCommand(command);
    }

    private static void ExecuteCommand(string command)
    {
        Debug.Log("[SceneGuardDiag] command bridge executing: " + command);
        switch (command)
        {
            case "lighting":
                CaptureLightingStateOnce();
                break;
            case "submit-only":
                RequestCapture(CaptureMode.SubmitOnly);
                break;
            case "draw-submit":
                RequestCapture(CaptureMode.DrawRendererWithSubmit);
                break;
            case "clear-submit":
                RequestCapture(CaptureMode.ClearWithSubmit);
                break;
            default:
                Debug.LogWarning("[SceneGuardDiag] unknown command: " + command);
                break;
        }
    }

    private static void RequestCapture(CaptureMode mode)
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        StopManualCaptureCallbacks();
        captureOnceRequested = true;
        captureMode = mode;
        readbackPending = false;
        lastDrawnCount = -1;

        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        if (!persistentFallbackAttached)
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        Debug.Log($"[SceneGuardDiag] manual capture requested: mode={captureMode}; waiting for next SceneView render.");
        SceneView.RepaintAll();
    }

    /// <summary>
    /// Mac SceneView post-URP repair using current RepairMode (default: original materials + Submit).
    /// </summary>
    public static int ApplyMacPostUrpRepair(ScriptableRenderContext context, Camera camera, bool log = false)
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return -1;
        if (camera == null || camera.cameraType != CameraType.SceneView)
            return -1;

        return ApplySceneViewRepair(context, camera, submit: true, log);
    }

    private static bool IsEnabled()
    {
        return EditorPrefs.GetBool(EditorPrefsKey, false);
    }

    private static bool DiagnosticsEnabled()
    {
        return EditorPrefs.GetBool(DiagnosticsPrefsKey, false);
    }

    private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera == null || camera.cameraType != CameraType.SceneView)
            return;

        if (persistentBeginAttached || captureOnceRequested)
            PrepareSceneViewCamera(camera);

        if (!captureOnceRequested)
            return;

        LogCameraState("begin", camera);
        LogRenderers(camera);
    }

    private static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera == null || camera.cameraType != CameraType.SceneView)
            return;

        if (!captureOnceRequested)
        {
            if (persistentFallbackAttached)
                ApplySceneViewRepair(context, camera, submit: true, log: DiagnosticsEnabled());
            return;
        }

        LogCameraState("end-before-fallback", camera);

        if (!IsEnabled() && captureMode != CaptureMode.ClearWithSubmit)
        {
            RequestReadback(camera, "fallback-disabled");
            return;
        }

        if (captureMode == CaptureMode.ClearWithSubmit)
        {
            CommandBuffer clearCmd = new CommandBuffer { name = "SceneGuard SceneView Clear Submit Test" };
            clearCmd.ClearRenderTarget(false, true, Color.magenta);
            context.ExecuteCommandBuffer(clearCmd);
            clearCmd.Dispose();
            context.Submit();
            Debug.Log("[SceneGuardDiag] clear+submit command executed with magenta color.");
            RequestReadback(camera, "after-clear-submit");
            return;
        }

        if (captureMode == CaptureMode.SubmitOnly)
        {
            ResubmitOnly(context, submit: true, log: true);
            Debug.Log("[SceneGuardDiag] context.Submit invoked (submit-only capture).");
            RequestReadback(camera, "after-submit-only");
            return;
        }

        int drawnCount = ApplySceneViewRepair(context, camera, captureMode == CaptureMode.DrawRendererWithSubmit, log: true);
        if (captureMode == CaptureMode.DrawRendererWithSubmit)
            Debug.Log("[SceneGuardDiag] context.Submit invoked after SceneView repair.");

        if (drawnCount >= 0)
            RequestReadback(camera, captureMode == CaptureMode.DrawRendererWithSubmit ? "after-fallback-submit" : "after-fallback-command");
        else if (captureOnceRequested)
            FinishCapture();
    }

    private static int ApplySceneViewRepair(ScriptableRenderContext context, Camera camera, bool submit, bool log)
    {
        switch (GetRepairMode())
        {
            case RepairMode.SubmitOnly:
                return ResubmitOnly(context, submit, log);
            case RepairMode.ProxyFallback:
                return DrawProxyFallback(context, camera, submit, log);
            case RepairMode.MirrorGameCameraInPlay:
                return PlayMirrorPathEnabled
                    ? MirrorGameCameraToSceneView(context, camera, submit, log)
                    : DrawOriginalMaterials(context, camera, submit, log);
            case RepairMode.SubmitThenOriginalMaterials:
            case RepairMode.OriginalMaterials:
            default:
                return DrawOriginalMaterials(context, camera, submit, log);
        }
    }

    private static int ResubmitOnly(ScriptableRenderContext context, bool submit, bool log)
    {
        if (submit)
            context.Submit();

        if (log)
            LogRepairIfChanged("submit-only", 0);

        return 0;
    }

    #region Play mirror render helpers

    private static int MirrorGameCameraToSceneView(ScriptableRenderContext context, Camera sceneCamera, bool submit, bool log)
    {
        if (!Application.isPlaying)
            return DrawOriginalMaterials(context, sceneCamera, submit, log);

        RenderTexture sceneRT = sceneCamera.targetTexture;
        if (sceneRT == null)
            return 0;

        EnsurePlayMirrorRT(sceneRT.width, sceneRT.height, sceneRT.format);

        if (!playMirrorReady || playMirrorRT == null)
        {
            if (log)
                Debug.LogWarning("[SceneGuard] mirror-game-camera: play mirror RT not ready; falling back to original materials.");
            return DrawOriginalMaterials(context, sceneCamera, submit, log);
        }

        CommandBuffer cmd = new CommandBuffer { name = "SceneGuard Mirror Game Camera To SceneView" };
        cmd.Blit(playMirrorRT, sceneRT);
        context.ExecuteCommandBuffer(cmd);
        cmd.Dispose();

        DrawSceneViewGizmosOnTop(context, sceneCamera);
        if (submit)
            context.Submit();

        playMirrorReady = false;

        if (log)
            LogRepairIfChanged("mirror-game-camera-blit", 1);

        return 1;
    }

    private static bool TryRenderPlayMirror(Camera sceneCamera, int width, int height)
    {
        if (mirrorRendering || renderPipelineDepth > 0)
            return false;

        Camera gameCamera = GetPlayWorldCamera();
        if (gameCamera == null || sceneCamera == null)
            return false;

        Camera mirrorCamera = EnsurePlayMirrorCamera(gameCamera);
        if (mirrorCamera == null)
            return false;

        RenderTextureFormat format = sceneCamera.targetTexture != null
            ? sceneCamera.targetTexture.format
            : RenderTextureFormat.DefaultHDR;
        EnsurePlayMirrorRT(width, height, format);

        mirrorRendering = true;
        try
        {
            mirrorCamera.CopyFrom(gameCamera);
            SyncMirrorCameraPipeline(mirrorCamera, gameCamera);
            ApplyMirrorPoseFromSceneCamera(mirrorCamera, sceneCamera);
            if (MirrorExcludeUiEnabled())
                mirrorCamera.cullingMask = gameCamera.cullingMask & ~GetUiExcludeLayerMask();

            mirrorCamera.enabled = true;
            mirrorCamera.targetTexture = playMirrorRT;
            mirrorCamera.Render();
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[SceneGuard] mirror-game-camera pre-render failed: " + ex.Message);
            return false;
        }
        finally
        {
            mirrorCamera.targetTexture = null;
            mirrorCamera.enabled = false;
            mirrorRendering = false;
        }
    }

    private static Camera EnsurePlayMirrorCamera(Camera source)
    {
        if (source == null)
            return null;

        if (playMirrorCamera == null)
        {
            GameObject go = EditorUtility.CreateGameObjectWithHideFlags(
                "SceneGuard_PlayMirrorCamera",
                HideFlags.HideAndDontSave,
                typeof(Camera));
            playMirrorCamera = go.GetComponent<Camera>();
            playMirrorCamera.enabled = false;
            playMirrorCamera.cameraType = CameraType.Game;

            if (source.TryGetComponent(out UniversalAdditionalCameraData sourceData))
            {
                ComponentUtility.CopyComponent(sourceData);
                ComponentUtility.PasteComponentAsNew(go);
            }
        }

        return playMirrorCamera;
    }

    private static void ReleasePlayMirrorCamera()
    {
        if (playMirrorCamera == null)
            return;

        Object.DestroyImmediate(playMirrorCamera.gameObject);
        playMirrorCamera = null;
    }

    private static void SyncMirrorCameraPipeline(Camera mirrorCamera, Camera sourceCamera)
    {
        if (!sourceCamera.TryGetComponent(out UniversalAdditionalCameraData sourceData) ||
            !mirrorCamera.TryGetComponent(out UniversalAdditionalCameraData mirrorData))
            return;

        mirrorData.renderType = sourceData.renderType;
        mirrorData.SetRenderer(sourceData.GetRendererIndex());
        mirrorData.renderPostProcessing = sourceData.renderPostProcessing;
        mirrorData.volumeLayerMask = sourceData.volumeLayerMask;
        mirrorData.volumeTrigger = sourceData.volumeTrigger;
        mirrorData.antialiasing = sourceData.antialiasing;
        mirrorData.antialiasingQuality = sourceData.antialiasingQuality;
    }

    private static void EnsurePlayMirrorRT(int width, int height, RenderTextureFormat format)
    {
        if (playMirrorRT != null && playMirrorRT.width == width && playMirrorRT.height == height)
            return;

        if (playMirrorRT != null)
        {
            playMirrorRT.Release();
            Object.DestroyImmediate(playMirrorRT);
            playMirrorRT = null;
        }

        playMirrorRT = new RenderTexture(width, height, 24, format)
        {
            name = "SceneGuard_PlayMirrorRT"
        };
        playMirrorRT.Create();
    }

    private static int DrawOriginalMaterials(ScriptableRenderContext context, Camera camera, bool submit, bool log)
    {
        RenderTexture sceneRT = camera.targetTexture;
        if (sceneRT == null)
            return 0;

        if (!camera.TryGetCullingParameters(false, out ScriptableCullingParameters cullingParameters))
            return 0;

        CullingResults cullResults = context.Cull(ref cullingParameters);
        SetupUrLightingGlobals();
        StabilizeSceneViewShaderGlobals();

        CommandBuffer cmd = new CommandBuffer { name = "SceneGuard SceneView Original Material Resubmit" };
        cmd.SetRenderTarget(sceneRT);
        cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        cmd.ClearRenderTarget(clearDepth: true, clearColor: true, backgroundColor: Color.black);

        if (ShouldDrawSkybox(camera))
        {
            Mesh skyMesh = GetSkyboxMesh();
            if (skyMesh != null)
            {
                float radius = Mathf.Max(camera.farClipPlane * 0.95f, camera.nearClipPlane + 1f);
                Matrix4x4 skyMatrix = Matrix4x4.TRS(camera.transform.position, Quaternion.identity, Vector3.one * radius);
                if (!TryDrawSceneSkybox(cmd, skyMesh, skyMatrix))
                    DrawSkybox(cmd, camera, GetSkyboxMaterial(), useSceneSkyboxOnly: false);
            }
        }

        context.ExecuteCommandBuffer(cmd);
        cmd.Dispose();

        DrawCulledRenderers(context, camera, cullResults, overrideMaterial: null, transparentPass: false);
        BindSceneColorForTransparentPass(context, camera, sceneRT);
        DrawCulledRenderers(context, camera, cullResults, overrideMaterial: null, transparentPass: true);
        DrawWaterFallbackOverlay(context, camera, sceneRT);
        DrawSceneViewGizmosOnTop(context, camera);

        if (submit)
            context.Submit();

        if (log)
            LogRepairIfChanged("original-materials-batch", 1);

        return 1;
    }

    /// <summary>
    /// URP SceneView path skips gizmo submission on Mac; draw icons after fallback geometry into the same RT.
    /// </summary>
    private static void DrawSceneViewGizmosOnTop(ScriptableRenderContext context, Camera camera)
    {
        if (camera == null || camera.cameraType != CameraType.SceneView)
            return;

        SceneView sceneView = SceneView.currentDrawingSceneView;
        if (sceneView != null && !sceneView.drawGizmos)
            return;

        if (!Handles.ShouldRenderGizmos())
            return;

        context.DrawGizmos(camera, GizmoSubset.PreImageEffects);
        context.DrawGizmos(camera, GizmoSubset.PostImageEffects);
    }

    /// <summary>
    /// SceneView overlay pass may not receive gizmos when fallback owns the 3D RT; redraw during the handle pass.
    /// </summary>
    private static void OnDuringSceneGuiGizmos(SceneView sceneView)
    {
        if (!IsEnabled() || sceneView == null || Event.current.type != EventType.Repaint)
            return;

        Camera camera = sceneView.camera;
        if (camera == null || !sceneView.drawGizmos || !Handles.ShouldRenderGizmos())
            return;

        Handles.DrawGizmos(camera);
    }

    /// <summary>
    /// TCRender water needs screen-space buffers; fallback transparent pass is invisible — draw opaque placeholder.
    /// </summary>
    private static void DrawWaterFallbackOverlay(ScriptableRenderContext context, Camera camera, RenderTexture sceneRT)
    {
        Material waterMaterial = GetWaterFallbackMaterial(camera);
        if (waterMaterial == null)
            return;

        Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(camera);
        CommandBuffer cmd = new CommandBuffer { name = "SceneGuard SceneView Water Fallback" };
        cmd.SetRenderTarget(sceneRT);
        cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);

        int drawn = 0;
        foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (!IsWaterRenderer(renderer))
                continue;
            if ((camera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                continue;
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, renderer.bounds))
                continue;

            int subMeshCount = GetSubMeshCount(renderer);
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                cmd.DrawRenderer(renderer, waterMaterial, subMeshIndex);

            drawn++;
        }

        if (drawn == 0)
        {
            cmd.Dispose();
            return;
        }

        context.ExecuteCommandBuffer(cmd);
        cmd.Dispose();
    }

    private static bool IsWaterRenderer(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            return false;
        if (EditorUtility.IsPersistent(renderer))
            return false;

        Material[] materials = renderer.sharedMaterials;
        if (materials == null)
            return false;

        foreach (Material material in materials)
        {
            string shaderName = material != null && material.shader != null ? material.shader.name : null;
            if (string.IsNullOrEmpty(shaderName))
                continue;
            if (shaderName.StartsWith("TCRender/Water/", System.StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Material GetWaterFallbackMaterial(Camera camera)
    {
        if (waterFallbackMaterial == null)
        {
            Shader shader = Shader.Find("Hidden/SceneGuard/SceneViewWaterFallback");
            if (shader == null)
            {
                Debug.LogWarning("[SceneGuard] SceneView water fallback shader not found.");
                return null;
            }

            waterFallbackMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        Material litReference = GetLitFallbackMaterial(camera);
        if (litReference != null)
        {
            waterFallbackMaterial.SetVector(LightDirId, litReference.GetVector(LightDirId));
            waterFallbackMaterial.SetColor(LightColorId, litReference.GetColor(LightColorId));
            waterFallbackMaterial.SetColor(AmbientColorId, litReference.GetColor(AmbientColorId));
        }

        Color waterTint = new Color(0.10f, 0.36f, 0.48f, 1f);
        waterFallbackMaterial.SetColor(BaseColorId, waterTint);
        waterFallbackMaterial.SetFloat(LightingScaleId, 0.75f);
        return waterFallbackMaterial;
    }

    private static void DrawCulledRenderers(
        ScriptableRenderContext context,
        Camera camera,
        CullingResults cullResults,
        Material overrideMaterial,
        bool transparentPass)
    {
        SortingSettings sortingSettings = new SortingSettings(camera)
        {
            criteria = transparentPass ? SortingCriteria.CommonTransparent : SortingCriteria.CommonOpaque
        };

        DrawingSettings drawingSettings = new DrawingSettings(ForwardShaderTags[0], sortingSettings)
        {
            perObjectData = PerObjectData.Lightmaps | PerObjectData.LightProbe | PerObjectData.ReflectionProbes | PerObjectData.OcclusionProbe
        };
        for (int i = 1; i < ForwardShaderTags.Length; i++)
            drawingSettings.SetShaderPassName(i, ForwardShaderTags[i]);

        if (overrideMaterial != null)
        {
            drawingSettings.overrideMaterial = overrideMaterial;
            drawingSettings.overrideMaterialPassIndex = 0;
        }

        FilteringSettings filter = transparentPass
            ? new FilteringSettings(RenderQueueRange.transparent, camera.cullingMask)
            : new FilteringSettings(RenderQueueRange.opaque, camera.cullingMask);
        context.DrawRenderers(cullResults, ref drawingSettings, ref filter);
    }

    private static void BindSceneColorForTransparentPass(ScriptableRenderContext context, Camera camera, RenderTexture sceneRT)
    {
        if (sceneRT == null || camera == null)
            return;

        EnsureOpaqueCopyRT(sceneRT.width, sceneRT.height, sceneRT.format);
        CommandBuffer cmd = new CommandBuffer { name = "SceneGuard SceneView Bind Opaque Color" };
        cmd.Blit(sceneRT, opaqueCopyRT);
        cmd.SetGlobalTexture(CameraOpaqueTextureId, opaqueCopyRT);
        cmd.SetRenderTarget(sceneRT);
        cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        context.ExecuteCommandBuffer(cmd);
        cmd.Dispose();
    }

    private static void EnsureOpaqueCopyRT(int width, int height, RenderTextureFormat format)
    {
        if (opaqueCopyRT != null && opaqueCopyRT.width == width && opaqueCopyRT.height == height)
            return;

        ReleaseOpaqueCopyRT();
        opaqueCopyRT = new RenderTexture(width, height, 0, format) { name = "SceneGuard_OpaqueCopy" };
        opaqueCopyRT.Create();
    }

    private static void ReleaseOpaqueCopyRT()
    {
        if (opaqueCopyRT == null)
            return;

        opaqueCopyRT.Release();
        Object.DestroyImmediate(opaqueCopyRT);
        opaqueCopyRT = null;
    }

    /// <summary>
    /// Game camera uses SceneView pose + projection so editor gizmos (Scene camera) align with mirrored pixels.
    /// </summary>
    private static void ApplyMirrorPoseFromSceneCamera(Camera gameCamera, Camera sceneCamera)
    {
        gameCamera.transform.SetPositionAndRotation(sceneCamera.transform.position, sceneCamera.transform.rotation);
        gameCamera.nearClipPlane = sceneCamera.nearClipPlane;
        gameCamera.farClipPlane = sceneCamera.farClipPlane;
        gameCamera.orthographic = sceneCamera.orthographic;
        if (sceneCamera.orthographic)
            gameCamera.orthographicSize = sceneCamera.orthographicSize;
        else
            gameCamera.fieldOfView = sceneCamera.fieldOfView;

        gameCamera.ResetProjectionMatrix();
        gameCamera.projectionMatrix = sceneCamera.projectionMatrix;
    }

    private static int GetUiExcludeLayerMask()
    {
        int mask = 0;
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer >= 0)
            mask |= 1 << uiLayer;

        Camera[] cameras = Camera.allCameras;
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            if (cam == null || !cam.enabled || cam.cameraType != CameraType.Game)
                continue;
            if (cam.name.IndexOf("UI", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            mask |= cam.cullingMask;
        }

        return mask;
    }

    private static Camera GetPlayWorldCamera()
    {
        if (Camera.main != null && Camera.main.cameraType == CameraType.Game)
            return Camera.main;

        Camera[] cameras = Camera.allCameras;
        Camera best = null;
        float bestDepth = float.NegativeInfinity;
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            if (cam == null || !cam.enabled || cam.cameraType != CameraType.Game)
                continue;
            if (cam.name.IndexOf("SceneGuard", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (cam.name.IndexOf("UI", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (cam.depth > bestDepth)
            {
                bestDepth = cam.depth;
                best = cam;
            }
        }

        return best;
    }

    #endregion

    private static int DrawProxyFallback(ScriptableRenderContext context, Camera camera, bool submit, bool log)
    {
        Material litMaterial = GetLitFallbackMaterial(camera);
        Material skyMaterial = GetSkyboxMaterial();
        if (litMaterial == null || skyMaterial == null)
            return -1;

        CommandBuffer cmd = new CommandBuffer { name = "SceneGuard SceneView Fallback" };
        int drawnCount = 0;

        cmd.ClearRenderTarget(clearDepth: true, clearColor: true, backgroundColor: Color.black);

        if (ShouldDrawSkybox(camera))
            DrawSkybox(cmd, camera, skyMaterial, useSceneSkyboxOnly: false);

        foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (!ShouldDraw(renderer))
                continue;

            int subMeshCount = GetSubMeshCount(renderer);
            if (subMeshCount <= 0)
                continue;

            Color baseColor = GetRendererBaseColor(renderer);
            litMaterial.SetColor(BaseColorId, baseColor);

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                cmd.DrawRenderer(renderer, litMaterial, subMeshIndex);

            drawnCount++;
        }

        context.ExecuteCommandBuffer(cmd);
        if (submit)
            context.Submit();

        cmd.Dispose();

        if (log)
            LogDrawCountIfChanged(drawnCount);

        return drawnCount;
    }

    private static bool ShouldDrawSkybox(Camera camera)
    {
        if (camera.clearFlags != CameraClearFlags.Skybox)
            return false;

        SceneView sceneView = SceneView.lastActiveSceneView;
        return sceneView == null || sceneView.sceneViewState.skyboxEnabled;
    }

    private static void DrawSkybox(CommandBuffer cmd, Camera camera, Material fallbackMaterial, bool useSceneSkyboxOnly)
    {
        Mesh mesh = GetSkyboxMesh();
        if (mesh == null)
            return;

        float radius = Mathf.Max(camera.farClipPlane * 0.95f, camera.nearClipPlane + 1f);
        Matrix4x4 matrix = Matrix4x4.TRS(camera.transform.position, Quaternion.identity, Vector3.one * radius);

        if (TryDrawSceneSkybox(cmd, mesh, matrix))
            return;

        if (!useSceneSkyboxOnly)
            cmd.DrawMesh(mesh, matrix, fallbackMaterial, 0, 0);
    }

    private static bool TryDrawSceneSkybox(CommandBuffer cmd, Mesh mesh, Matrix4x4 matrix)
    {
        Material sceneSkybox = RenderSettings.skybox;
        if (sceneSkybox == null || sceneSkybox.shader == null || !sceneSkybox.shader.isSupported)
            return false;

        // Single-pass skyboxes (Procedural, Cubemap, URP Nephele) use pass 0.
        if (sceneSkybox.passCount != 1)
            return false;

        cmd.DrawMesh(mesh, matrix, sceneSkybox, 0, 0);
        return true;
    }

    private static bool ShouldDraw(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            return false;
        if (EditorUtility.IsPersistent(renderer))
            return false;

        Material[] materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0)
            return false;

        foreach (Material material in materials)
        {
            string shaderName = material != null && material.shader != null ? material.shader.name : null;
            if (!string.IsNullOrEmpty(shaderName) && shaderName.StartsWith("TCRender/"))
                return true;
        }

        return false;
    }

    private static Color GetRendererBaseColor(Renderer renderer)
    {
        Material[] materials = renderer.sharedMaterials;
        if (materials == null)
            return new Color(0.78f, 0.78f, 0.78f, 1f);

        foreach (Material material in materials)
        {
            if (material == null)
                continue;

            if (material.HasProperty(BaseColorId))
                return material.GetColor(BaseColorId);
            if (material.HasProperty("_Color"))
                return material.GetColor("_Color");
        }

        return new Color(0.78f, 0.78f, 0.78f, 1f);
    }

    private static Light GetSceneDirectionalLight()
    {
        if (RenderSettings.sun != null && RenderSettings.sun.enabled && RenderSettings.sun.type == LightType.Directional)
            return RenderSettings.sun;

        foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
        {
            if (light == null || light.type != LightType.Directional)
                continue;
            if (EditorUtility.IsPersistent(light))
                continue;
            if (!light.enabled || !light.gameObject.activeInHierarchy)
                continue;
            return light;
        }

        return null;
    }

    private static void LogCameraState(string stage, Camera camera)
    {
        RenderTexture rt = camera.targetTexture;
        string rtInfo = rt == null
            ? "targetTexture=null"
            : $"targetTexture={rt.width}x{rt.height} fmt={rt.format} gfxFmt={rt.graphicsFormat} depth={rt.depth}";

        Debug.Log(
            $"[SceneGuardDiag] camera {stage}: enabled={camera.enabled} active={camera.gameObject.activeInHierarchy} " +
            $"clear={camera.clearFlags} bg={camera.backgroundColor} cullingMask=0x{camera.cullingMask:X8} " +
            $"near={camera.nearClipPlane:F3} far={camera.farClipPlane:F3} depth={camera.depth:F3} " +
            $"pipeline={(RenderPipelineManager.currentPipeline != null ? RenderPipelineManager.currentPipeline.GetType().FullName : "null")} " +
            rtInfo);
    }

    private static void LogRenderers(Camera camera)
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
        int total = 0;
        int active = 0;
        int inFrustum = 0;

        foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (!HasTCRenderMaterial(renderer))
                continue;

            total++;
            bool isActive = renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy && !EditorUtility.IsPersistent(renderer);
            bool visibleByFrustum = isActive && GeometryUtility.TestPlanesAABB(planes, renderer.bounds);
            if (isActive)
                active++;
            if (visibleByFrustum)
                inFrustum++;

            Debug.Log(
                $"[SceneGuardDiag] renderer name={renderer.name} type={renderer.GetType().Name} " +
                $"active={isActive} isVisible={renderer.isVisible} inFrustum={visibleByFrustum} layer={renderer.gameObject.layer} " +
                $"boundsCenter={renderer.bounds.center} boundsSize={renderer.bounds.size} subMeshes={GetSubMeshCount(renderer)}");

            LogMaterials(renderer);
        }

        Debug.Log($"[SceneGuardDiag] renderer summary: totalTCRender={total} active={active} inSceneViewFrustum={inFrustum}");
    }

    private static void LogLightingState()
    {
        Debug.Log(
            $"[SceneGuardDiag] lighting RenderSettings: ambientMode={RenderSettings.ambientMode} " +
            $"ambientLight={RenderSettings.ambientLight} ambientIntensity={RenderSettings.ambientIntensity:0.###} " +
            $"defaultReflectionMode={RenderSettings.defaultReflectionMode} sun={(RenderSettings.sun != null ? RenderSettings.sun.name : "null")}");

        int directionalCount = 0;
        foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
        {
            if (light == null || light.type != LightType.Directional)
                continue;

            directionalCount++;
            bool persistent = EditorUtility.IsPersistent(light);
            Debug.Log(
                $"[SceneGuardDiag] directional light name={light.name} enabled={light.enabled} " +
                $"active={light.gameObject.activeInHierarchy} persistent={persistent} intensity={light.intensity:0.###} " +
                $"color={light.color} forward={light.transform.forward} layer={light.gameObject.layer} " +
                $"cullingMask=0x{light.cullingMask:X8} shadows={light.shadows} renderMode={light.renderMode}");
        }

        Debug.Log($"[SceneGuardDiag] lighting summary: directionalCount={directionalCount}");
    }

    private static bool HasTCRenderMaterial(Renderer renderer)
    {
        if (renderer == null)
            return false;

        Material[] materials = renderer.sharedMaterials;
        if (materials == null)
            return false;

        foreach (Material material in materials)
        {
            string shaderName = material != null && material.shader != null ? material.shader.name : null;
            if (!string.IsNullOrEmpty(shaderName) && shaderName.StartsWith("TCRender/"))
                return true;
        }

        return false;
    }

    private static void LogMaterials(Renderer renderer)
    {
        Material[] materials = renderer.sharedMaterials;
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
            {
                Debug.Log($"[SceneGuardDiag] material[{i}]=null");
                continue;
            }

            Shader shader = material.shader;
            string shaderName = shader != null ? shader.name : "null";
            string passNames = "";
            for (int pass = 0; pass < material.passCount; pass++)
                passNames += (pass == 0 ? "" : ",") + material.GetPassName(pass);

            Debug.Log(
                $"[SceneGuardDiag] material[{i}] name={material.name} shader={shaderName} " +
                $"shaderSupported={(shader != null && shader.isSupported)} renderQueue={material.renderQueue} " +
                $"passCount={material.passCount} passes=[{passNames}] " +
                $"tags(RenderType={material.GetTag("RenderType", false, "n/a")}, Queue={material.GetTag("Queue", false, "n/a")}) " +
                $"props(_Surface={GetFloat(material, "_Surface")}, _Blend={GetFloat(material, "_Blend")}, _Alpha={GetFloat(material, "_Alpha")}, " +
                $"_SrcBlend={GetFloat(material, "_SrcBlend")}, _DstBlend={GetFloat(material, "_DstBlend")}, _ZWrite={GetFloat(material, "_ZWrite")}, _Cull={GetFloat(material, "_Cull")}, " +
                $"_BaseColor={GetColor(material, "_BaseColor")}) keywords=[{string.Join(",", material.shaderKeywords)}]");
        }
    }

    private static string GetFloat(Material material, string propertyName)
    {
        return material.HasProperty(propertyName) ? material.GetFloat(propertyName).ToString("0.###") : "n/a";
    }

    private static string GetColor(Material material, string propertyName)
    {
        return material.HasProperty(propertyName) ? material.GetColor(propertyName).ToString() : "n/a";
    }

    private static int GetSubMeshCount(Renderer renderer)
    {
        if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            return skinned.sharedMesh.subMeshCount;

        if (renderer is MeshRenderer)
        {
            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            return meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.subMeshCount : 0;
        }

        return renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;
    }

    private static Material GetSkyboxMaterial()
    {
        if (skyboxMaterial != null)
            return skyboxMaterial;

        Shader shader = Shader.Find("Hidden/SceneGuard/SceneViewSkyboxFallback");
        if (shader == null)
        {
            Debug.LogWarning("[SceneGuard] SceneView skybox fallback shader not found.");
            return null;
        }

        skyboxMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        skyboxMaterial.SetColor(TopColorId, new Color(0.55f, 0.72f, 0.92f, 1f));
        skyboxMaterial.SetColor(BottomColorId, new Color(0.72f, 0.76f, 0.80f, 1f));
        Debug.Log($"[SceneGuardDiag] skybox fallback shader={shader.name} supported={shader.isSupported}");
        return skyboxMaterial;
    }

    private static Material GetLitFallbackMaterial(Camera camera)
    {
        if (litFallbackMaterial == null)
        {
            Shader shader = Shader.Find("Hidden/SceneGuard/SceneViewLitFallback");
            if (shader == null)
            {
                Debug.LogWarning("[SceneGuard] SceneView lit fallback shader not found.");
                return null;
            }

            litFallbackMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Debug.Log($"[SceneGuardDiag] lit fallback shader={shader.name} supported={shader.isSupported}");
        }

        Light directional = GetSceneDirectionalLight();
        Vector3 lightForward = directional != null ? directional.transform.forward : new Vector3(-0.32f, -0.77f, 0.56f);
        float lightIntensity = directional != null ? Mathf.Min(directional.intensity, 1.2f) : 1f;
        Color lightColor = directional != null ? directional.color * lightIntensity : new Color(1f, 0.96f, 0.84f, 1f);
        Color ambient = GetAmbientColor();

        litFallbackMaterial.SetVector(LightDirId, lightForward);
        litFallbackMaterial.SetColor(LightColorId, lightColor);
        litFallbackMaterial.SetColor(AmbientColorId, ambient);
        litFallbackMaterial.SetFloat(LightingScaleId, 0.55f);
        return litFallbackMaterial;
    }

    private static Color GetAmbientColor()
    {
        switch (RenderSettings.ambientMode)
        {
            case AmbientMode.Flat:
                return RenderSettings.ambientLight * RenderSettings.ambientIntensity;
            case AmbientMode.Trilight:
                return RenderSettings.ambientEquatorColor * RenderSettings.ambientIntensity;
            default:
                return RenderSettings.ambientSkyColor * RenderSettings.ambientIntensity * 0.5f;
        }
    }

    private static Mesh GetSkyboxMesh()
    {
        if (skyboxMesh != null)
            return skyboxMesh;

        skyboxMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        return skyboxMesh;
    }

    private static void LogRepairIfChanged(string modeLabel, int drawnCount)
    {
        double now = EditorApplication.timeSinceStartup;
        if (drawnCount == lastDrawnCount && now - lastLogTime < 30.0)
            return;

        lastDrawnCount = drawnCount;
        lastLogTime = now;
        Debug.Log($"[SceneGuardDiag] repair mode={modeLabel} drew {drawnCount} renderer(s).");
    }

    private static void LogDrawCountIfChanged(int drawnCount)
    {
        LogRepairIfChanged("proxy-fallback", drawnCount);
    }

    private static void RequestReadback(Camera camera, string label)
    {
        if (!captureOnceRequested || readbackPending || camera == null || camera.targetTexture == null)
        {
            if (captureOnceRequested && camera != null && camera.targetTexture == null)
                FinishCapture();
            return;
        }

        RenderTexture rt = camera.targetTexture;
        if (rt.width <= 0 || rt.height <= 0)
            return;

        Vector2 centerPixel = new Vector2(rt.width * 0.5f, rt.height * 0.5f);
        string sampleRendererName = "none";
        Vector2 rendererPixel = centerPixel;
        Vector3 rendererViewport = Vector3.zero;
        if (TryGetFirstVisibleTCRendererPixel(camera, rt, out Renderer sampleRenderer, out rendererPixel, out rendererViewport))
            sampleRendererName = sampleRenderer.name;

        readbackPending = true;
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
        {
            readbackPending = false;
            FinishCapture();

            if (request.hasError)
            {
                Debug.LogWarning($"[SceneGuardDiag] readback {label}: GPU readback error.");
                return;
            }

            try
            {
                var data = request.GetData<Color32>();
                Color32 center = SampleColor(data, rt.width, rt.height, centerPixel);
                Color32 rendererColor = SampleColor(data, rt.width, rt.height, rendererPixel);
                Debug.Log(
                    $"[SceneGuardDiag] readback {label}: rt={rt.width}x{rt.height} samples={data.Length} " +
                    $"centerPx={centerPixel} center={FormatColor(center)} " +
                    $"renderer={sampleRendererName} viewport={rendererViewport} pixel={rendererPixel} color={FormatColor(rendererColor)}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SceneGuardDiag] readback {label}: failed to sample Color32 data: {ex.GetType().Name}: {ex.Message}");
            }
        });

        DetachRenderCallbacks();
    }

    private static void FinishCapture()
    {
        captureOnceRequested = false;
        DetachRenderCallbacks();
        Debug.Log("[SceneGuardDiag] manual capture finished; render callbacks detached.");
    }

    private static void DetachRenderCallbacks()
    {
        StopManualCaptureCallbacks();
    }

    private static void StopManualCaptureCallbacks()
    {
        if (!persistentBeginAttached)
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        if (!persistentFallbackAttached)
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
    }

    private static bool TryGetFirstVisibleTCRendererPixel(Camera camera, RenderTexture rt, out Renderer sampleRenderer, out Vector2 pixel, out Vector3 viewport)
    {
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
        foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (!ShouldDraw(renderer) || !GeometryUtility.TestPlanesAABB(planes, renderer.bounds))
                continue;

            viewport = camera.WorldToViewportPoint(renderer.bounds.center);
            pixel = new Vector2(
                Mathf.Clamp(viewport.x * rt.width, 0, rt.width - 1),
                Mathf.Clamp(viewport.y * rt.height, 0, rt.height - 1));
            sampleRenderer = renderer;
            return true;
        }

        sampleRenderer = null;
        pixel = new Vector2(rt.width * 0.5f, rt.height * 0.5f);
        viewport = Vector3.zero;
        return false;
    }

    private static Color32 SampleColor(Unity.Collections.NativeArray<Color32> data, int width, int height, Vector2 pixel)
    {
        if (data.Length == 0)
            return new Color32(0, 0, 0, 0);

        int x = Mathf.Clamp(Mathf.RoundToInt(pixel.x), 0, width - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(pixel.y), 0, height - 1);
        int index = Mathf.Clamp(y * width + x, 0, data.Length - 1);
        return data[index];
    }

    private static string FormatColor(Color32 color)
    {
        return $"RGBA32({color.r},{color.g},{color.b},{color.a})";
    }

    private static void Cleanup()
    {
        DetachRenderCallbacks();
        EditorApplication.update -= OnEditorUpdate;
        if (PlayMirrorPathEnabled)
            EditorApplication.update -= OnMirrorPreRenderUpdate;
        SceneView.duringSceneGui -= OnDuringSceneGuiGizmos;
        RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
        RenderPipelineManager.endContextRendering -= OnEndContextRendering;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        persistentFallbackAttached = false;
        persistentBeginAttached = false;
        captureOnceRequested = false;
        playMirrorReady = false;
        mirrorRendering = false;
        renderPipelineDepth = 0;
        if (litFallbackMaterial != null)
            Object.DestroyImmediate(litFallbackMaterial);
        if (waterFallbackMaterial != null)
            Object.DestroyImmediate(waterFallbackMaterial);
        if (skyboxMaterial != null)
            Object.DestroyImmediate(skyboxMaterial);
        if (playMirrorRT != null)
        {
            playMirrorRT.Release();
            Object.DestroyImmediate(playMirrorRT);
            playMirrorRT = null;
        }

        ReleasePlayMirrorCamera();

        ReleaseOpaqueCopyRT();
        litFallbackMaterial = null;
        waterFallbackMaterial = null;
        skyboxMaterial = null;
    }

    private static void ApplyPersistentFallbackState()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        persistentFallbackAttached = false;
        persistentBeginAttached = false;

        if (!IsEnabled())
        {
            Debug.Log("[SceneGuard] SceneView repair disabled.");
            return;
        }

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        persistentFallbackAttached = true;
        persistentBeginAttached = true;
        Debug.Log($"[SceneGuard] SceneView repair enabled: mode={GetRepairMode()}.");
    }
}
