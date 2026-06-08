using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Side-by-side Game vs SceneView URP output comparison (Mac Editor evidence).
/// Captures camera state at beginCameraRendering and RT readback at endCameraRendering
/// with no material redraw or mirror. Writes Library/SceneGuard/game_vs_scene_trace_latest.txt.
/// </summary>
[InitializeOnLoad]
public static class SceneGuardGameVsSceneViewTrace
{
    private const string OutputPath = "Library/SceneGuard/game_vs_scene_trace_latest.txt";
    private const string CommandFile = "Library/SceneGuard/command.txt";
    private const int MaxFramesWithoutGame = 180;

    private enum TraceState
    {
        Idle,
        Collecting,
        Done
    }

    private struct CameraReadbackResult
    {
        public string Label;
        public int Width;
        public int Height;
        public Color32 CenterColor;
        public float NonBlackPct;
        public float BrightPct;
        public float AvgLum;
        public float MaxLum;
        public bool Valid;
    }

    private struct CameraStateSnapshot
    {
        public string Name;
        public CameraType Type;
        public bool Enabled;
        public CameraClearFlags ClearFlags;
        public int CullingMask;
        public bool AllowHdr;
        public bool AllowMsaa;
        public float Depth;
        public string TargetTextureSummary;
        public RenderTexture TargetTexture;
    }

    private static TraceState state = TraceState.Idle;
    private static readonly StringBuilder report = new StringBuilder();
    private static double nextCommandPollTime;
    private static int framesWithoutGame;
    private static int repaintAttempts;
    private static bool readbackPending;
    private static bool sceneEndCaptured;
    private static bool gameEndCaptured;
    private static CameraStateSnapshot sceneBeginState;
    private static CameraStateSnapshot gameBeginState;
    private static CameraReadbackResult sceneReadback;
    private static CameraReadbackResult gameReadback;
    private static Camera lastSceneCamera;
    private static Camera lastGameCamera;

    static SceneGuardGameVsSceneViewTrace()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        EditorApplication.update += OnEditorUpdate;
    }

    public static bool IsTraceActive => state == TraceState.Collecting;

    [MenuItem("Performance/SceneGuard/Trace Game vs SceneView (Compare)", false, 317)]
    private static void MenuTrace()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        BeginTrace("menu");
    }

    [MenuItem("Performance/SceneGuard/Trace Game vs SceneView (Compare)", true)]
    private static bool MenuTraceValidate()
    {
        return Application.platform == RuntimePlatform.OSXEditor;
    }

    private static void OnEditorUpdate()
    {
        if (state == TraceState.Collecting)
        {
            framesWithoutGame++;
            if (!sceneEndCaptured || (!gameEndCaptured && framesWithoutGame > 30 && repaintAttempts < 4))
            {
                if (framesWithoutGame % 30 == 0 && repaintAttempts < 4)
                {
                    repaintAttempts++;
                    RepaintGameView();
                    SceneView.RepaintAll();
                    LogLine($"repaint wave {repaintAttempts} (scene={sceneEndCaptured}, game={gameEndCaptured})");
                }
            }

            if (sceneEndCaptured && (gameEndCaptured || framesWithoutGame >= MaxFramesWithoutGame) && !readbackPending)
                FinalizeTrace();
        }

        if (EditorApplication.timeSinceStartup < nextCommandPollTime)
            return;

        nextCommandPollTime = EditorApplication.timeSinceStartup + 0.5;
        if (!File.Exists(CommandFile))
            return;

        string command;
        try
        {
            command = File.ReadAllText(CommandFile).Trim();
            File.Delete(CommandFile);
        }
        catch (Exception ex)
        {
            LogLine("command read failed: " + ex.Message);
            return;
        }

        if (command == "trace-game-vs-scene")
            BeginTrace("command");
    }

    private static void BeginTrace(string source)
    {
        if (state == TraceState.Collecting)
        {
            LogLine($"compare trace already running; ignored request from {source}.");
            return;
        }

        if (SceneGuardSceneViewPipelineTrace.IsTraceActive)
        {
            LogLine($"single-camera trace is running; ignored request from {source}.");
            return;
        }

        report.Clear();
        report.AppendLine("========== SceneGuard Game vs SceneView Compare Trace ==========");
        report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Unity: {Application.unityVersion}");
        report.AppendLine($"Platform: {Application.platform}");
        report.AppendLine($"GraphicsDevice: {SystemInfo.graphicsDeviceType} / {SystemInfo.graphicsDeviceName}");
        report.AppendLine($"PlayMode: isPlaying={Application.isPlaying}");
        report.AppendLine($"Trigger: {source}");
        report.AppendLine();

        LogPipelineSummary();
        LogDiscoveredCameras();
        LogRendererFeatures();

        state = TraceState.Collecting;
        framesWithoutGame = 0;
        repaintAttempts = 0;
        readbackPending = false;
        sceneEndCaptured = false;
        gameEndCaptured = false;
        sceneReadback = default;
        gameReadback = default;
        lastSceneCamera = null;
        lastGameCamera = null;

        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

        LogLine("compare trace started; repainting GameView + SceneView...");
        RepaintGameView();
        SceneView.RepaintAll();
        FlushReport();
    }

    private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (state != TraceState.Collecting || camera == null)
            return;

        if (camera.cameraType == CameraType.SceneView)
        {
            lastSceneCamera = camera;
            sceneBeginState = CaptureCameraState(camera);
            report.AppendLine("--- SceneView beginCameraRendering ---");
            AppendCameraState(report, sceneBeginState);
            report.AppendLine();
            FlushReport();

            if (!camera.enabled)
            {
                camera.enabled = true;
                LogLine("WARN: SceneView camera was disabled at begin; forced enabled for compare.");
            }
        }
        else if (camera.cameraType == CameraType.Game)
        {
            framesWithoutGame = 0;
            lastGameCamera = camera;
            gameBeginState = CaptureCameraState(camera);
            report.AppendLine("--- Game beginCameraRendering ---");
            AppendCameraState(report, gameBeginState);
            report.AppendLine();
            FlushReport();
        }
    }

    private static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (state != TraceState.Collecting || readbackPending || camera == null)
            return;

        if (camera.cameraType == CameraType.SceneView && !sceneEndCaptured)
        {
            sceneEndCaptured = true;
            LogLine("--- SceneView endCameraRendering (URP end, no Submit, no patch) ---");
            RequestReadback(camera, "sceneview-urp-end", result =>
            {
                sceneReadback = result;
                if (!gameEndCaptured)
                {
                    RepaintGameView();
                    SceneView.RepaintAll();
                }
            });
            return;
        }

        if (camera.cameraType == CameraType.Game && !gameEndCaptured)
        {
            gameEndCaptured = true;
            LogLine("--- Game endCameraRendering (URP end, no Submit, no patch) ---");
            RequestReadback(camera, "game-urp-end", result => { gameReadback = result; });
        }
    }

    private static void FinalizeTrace()
    {
        state = TraceState.Done;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

        report.AppendLine("=== Side-by-Side Readback (after URP endCameraRendering) ===");
        AppendReadbackRow(report, sceneReadback, "SceneView");
        AppendReadbackRow(report, gameReadback, "Game");
        report.AppendLine();

        report.AppendLine("=== Camera State Diff (beginCameraRendering) ===");
        if (lastSceneCamera != null && lastGameCamera != null)
            AppendStateDiff(report, sceneBeginState, gameBeginState);
        else
        {
            if (lastSceneCamera == null)
                report.AppendLine("SceneView camera: NOT observed this trace.");
            if (lastGameCamera == null)
                report.AppendLine("Game camera: NOT observed — open Game tab, ensure Main Camera exists, or enter Play.");
        }

        report.AppendLine();
        WriteComparisonConclusion();
        FlushReport();
        LogLine("compare trace complete => " + Path.GetFullPath(OutputPath));
    }

    private static void LogPipelineSummary()
    {
        report.AppendLine("=== Pipeline ===");
        var pipe = GraphicsSettings.defaultRenderPipeline;
        report.AppendLine(pipe != null
            ? $"defaultRenderPipeline={pipe.name} ({pipe.GetType().FullName})"
            : "defaultRenderPipeline=NULL");
        report.AppendLine($"QualitySettings.renderPipeline={(QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline.name : "NULL")}");
        report.AppendLine();

        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null)
        {
            report.AppendLine("=== SceneView Editor State ===");
            report.AppendLine($"sceneLighting={sv.sceneLighting} skyboxEnabled={sv.sceneViewState.skyboxEnabled}");
            report.AppendLine();
        }
    }

    private static void LogDiscoveredCameras()
    {
        report.AppendLine("=== Cameras in memory (Camera.allCameras) ===");
        Camera[] all = Camera.allCameras;
        if (all == null || all.Length == 0)
        {
            report.AppendLine("(none)");
            report.AppendLine();
            return;
        }

        for (int i = 0; i < all.Length; i++)
        {
            Camera cam = all[i];
            if (cam == null)
                continue;

            report.Append($"  [{i}] {cam.name} type={cam.cameraType} enabled={cam.enabled} depth={cam.depth}");
            if (cam.cameraType == CameraType.Game && cam == Camera.main)
                report.Append(" [Camera.main]");
            report.AppendLine();
        }

        report.AppendLine();
    }

    private static void LogRendererFeatures()
    {
        report.AppendLine("=== RendererFeatures (urp_renderer) ===");
        const string path = "Assets/Settings/urp_renderer.asset";
        ScriptableObject rd = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        if (rd == null)
        {
            report.AppendLine("urp_renderer.asset NOT FOUND");
            report.AppendLine();
            return;
        }

        SerializedObject so = new SerializedObject(rd);
        SerializedProperty features = so.FindProperty("m_RendererFeatures");
        if (features == null || !features.isArray)
        {
            report.AppendLine("m_RendererFeatures missing");
            report.AppendLine();
            return;
        }

        for (int i = 0; i < features.arraySize; i++)
        {
            UnityEngine.Object featureRef = features.GetArrayElementAtIndex(i).objectReferenceValue;
            if (featureRef == null)
                continue;

            SerializedObject fso = new SerializedObject(featureRef);
            string name = fso.FindProperty("m_Name")?.stringValue ?? featureRef.name;
            bool active = fso.FindProperty("m_Active")?.boolValue ?? false;
            SerializedProperty showProp = fso.FindProperty("ShowInSceneView");
            string show = showProp != null ? showProp.boolValue.ToString() : "N/A";
            report.AppendLine($"  [{i}] {name} active={active} ShowInSceneView={show} type={featureRef.GetType().Name}");
        }

        report.AppendLine();
    }

    private static CameraStateSnapshot CaptureCameraState(Camera camera)
    {
        RenderTexture rt = camera.targetTexture;
        return new CameraStateSnapshot
        {
            Name = camera.name,
            Type = camera.cameraType,
            Enabled = camera.enabled,
            ClearFlags = camera.clearFlags,
            CullingMask = camera.cullingMask,
            AllowHdr = camera.allowHDR,
            AllowMsaa = camera.allowMSAA,
            Depth = camera.depth,
            TargetTexture = rt,
            TargetTextureSummary = rt == null
                ? "NULL (backbuffer / internal)"
                : $"{rt.width}x{rt.height} fmt={rt.format} gfx={rt.graphicsFormat} depth={rt.depth}"
        };
    }

    private static void AppendCameraState(StringBuilder sb, CameraStateSnapshot s)
    {
        sb.AppendLine($"  name={s.Name} type={s.Type} enabled={s.Enabled} depth={s.Depth}");
        sb.AppendLine($"  clearFlags={s.ClearFlags} cullingMask=0x{s.CullingMask:X8}");
        sb.AppendLine($"  allowHDR={s.AllowHdr} allowMSAA={s.AllowMsaa}");
        sb.AppendLine($"  targetTexture={s.TargetTextureSummary}");
    }

    private static void AppendStateDiff(StringBuilder sb, CameraStateSnapshot scene, CameraStateSnapshot game)
    {
        DiffLine(sb, "enabled", scene.Enabled.ToString(), game.Enabled.ToString());
        DiffLine(sb, "clearFlags", scene.ClearFlags.ToString(), game.ClearFlags.ToString());
        DiffLine(sb, "cullingMask", $"0x{scene.CullingMask:X8}", $"0x{game.CullingMask:X8}");
        DiffLine(sb, "allowHDR", scene.AllowHdr.ToString(), game.AllowHdr.ToString());
        DiffLine(sb, "allowMSAA", scene.AllowMsaa.ToString(), game.AllowMsaa.ToString());
        DiffLine(sb, "targetTexture", scene.TargetTextureSummary, game.TargetTextureSummary);
    }

    private static void DiffLine(StringBuilder sb, string field, string sceneVal, string gameVal)
    {
        string mark = sceneVal == gameVal ? " " : "!";
        sb.AppendLine($"  {mark} {field}: SceneView={sceneVal} | Game={gameVal}");
    }

    private static void AppendReadbackRow(StringBuilder sb, CameraReadbackResult r, string label)
    {
        if (!r.Valid)
        {
            sb.AppendLine($"  {label}: NO READBACK (camera did not render or targetTexture unavailable)");
            return;
        }

        sb.AppendLine(
            $"  {label}: rt={r.Width}x{r.Height} center={FormatColor(r.CenterColor)} " +
            $"nonBlack={r.NonBlackPct:F2}% bright={r.BrightPct:F2}% avgLum={r.AvgLum:F5} maxLum={r.MaxLum:F5}");
    }

    private static void WriteComparisonConclusion()
    {
        report.AppendLine("=== Comparison Conclusion ===");

        if (!sceneReadback.Valid)
        {
            report.AppendLine("SceneView readback missing — ensure SceneView window is visible.");
            return;
        }

        if (!gameReadback.Valid)
        {
            report.AppendLine("Game readback missing — open Game tab, add Main Camera, or enter Play and re-run.");
            report.AppendLine("SceneView-only: URP still produced " +
                              (sceneReadback.NonBlackPct > 1f ? "some" : "no") +
                              " pixels at endCameraRendering.");
            return;
        }

        bool sceneHasContent = sceneReadback.NonBlackPct > 1f || sceneReadback.MaxLum > 0.05f;
        bool gameHasContent = gameReadback.NonBlackPct > 1f || gameReadback.MaxLum > 0.05f;

        if (gameHasContent && !sceneHasContent)
        {
            report.AppendLine("DIVERGENCE: Game RT has URP output; SceneView RT is empty/black.");
            report.AppendLine("=> EcoEngine SceneView branch (isSceneView / EmitWorldGeometryForSceneView) likely skips draws on Mac Metal.");
            report.AppendLine("=> Align begin-state diffs above; engine-source fix required for 1:1 restore.");
        }
        else if (!gameHasContent && !sceneHasContent)
        {
            report.AppendLine("Both Game and SceneView RT empty at URP end — scene may have no visible geometry or cameras misconfigured.");
        }
        else if (sceneHasContent && gameHasContent)
        {
            report.AppendLine("Both cameras received URP output — SceneView black may be Editor composite/post-URP, not missing geometry pass.");
            report.AppendLine("Compare center colors and lum; check Submit/composite after endCameraRendering.");
        }
        else if (!gameHasContent && sceneHasContent)
        {
            report.AppendLine("Unusual: SceneView has output but Game does not — verify Game camera target and GameView visibility.");
        }

        report.AppendLine();
        report.AppendLine("Re-run: Performance/SceneGuard/Trace Game vs SceneView (Compare)");
        report.AppendLine("Or: echo trace-game-vs-scene > Library/SceneGuard/command.txt");
    }

    private static void RequestReadback(Camera camera, string label, Action<CameraReadbackResult> onComplete)
    {
        RenderTexture rt = camera != null ? camera.targetTexture : null;
        if (rt == null || rt.width <= 0 || rt.height <= 0)
        {
            LogLine($"readback {label}: SKIP (targetTexture null — may render to backbuffer)");
            onComplete?.Invoke(default);
            return;
        }

        Vector2 center = new Vector2(rt.width * 0.5f, rt.height * 0.5f);
        readbackPending = true;
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
        {
            readbackPending = false;
            CameraReadbackResult result = default;
            result.Label = label;
            result.Width = rt.width;
            result.Height = rt.height;

            if (request.hasError)
            {
                LogLine($"readback {label}: GPU ERROR");
            }
            else
            {
                try
                {
                    NativeArray<Color32> data = request.GetData<Color32>();
                    AnalyzePixels(label, data, rt.width, rt.height, center, out result);
                    result.Valid = true;
                }
                catch (Exception ex)
                {
                    LogLine($"readback {label}: parse failed {ex.Message}");
                }
            }

            onComplete?.Invoke(result);
        });
    }

    private static void AnalyzePixels(
        string label,
        NativeArray<Color32> data,
        int w,
        int h,
        Vector2 center,
        out CameraReadbackResult result)
    {
        result = new CameraReadbackResult { Label = label, Width = w, Height = h };
        int total = data.Length;
        int nonBlack = 0;
        int bright = 0;
        float maxLum = 0f;
        float sumLum = 0f;

        for (int i = 0; i < total; i++)
        {
            Color32 c = data[i];
            float lum = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
            sumLum += lum;
            if (lum > maxLum)
                maxLum = lum;
            if (lum > 0.01f)
                nonBlack++;
            if (lum > 0.08f)
                bright++;
        }

        result.CenterColor = Sample(data, w, h, center);
        result.NonBlackPct = total > 0 ? 100f * nonBlack / total : 0f;
        result.BrightPct = total > 0 ? 100f * bright / total : 0f;
        result.AvgLum = total > 0 ? sumLum / total : 0f;
        result.MaxLum = maxLum;

        LogLine(
            $"readback {label}: rt={w}x{h} center={FormatColor(result.CenterColor)} " +
            $"nonBlack={result.NonBlackPct:F2}% bright={result.BrightPct:F2}% avgLum={result.AvgLum:F5} maxLum={result.MaxLum:F5}");
    }

    private static Color32 Sample(NativeArray<Color32> data, int w, int h, Vector2 pixel)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(pixel.x), 0, w - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(pixel.y), 0, h - 1);
        return data[Mathf.Clamp(y * w + x, 0, data.Length - 1)];
    }

    private static string FormatColor(Color32 c) => $"RGBA32({c.r},{c.g},{c.b},{c.a})";

    private static void RepaintGameView()
    {
        Type gameViewType = typeof(Editor).Assembly.GetType("UnityEditor.GameView");
        if (gameViewType == null)
            return;

        UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(gameViewType);
        for (int i = 0; i < windows.Length; i++)
        {
            if (windows[i] is EditorWindow window)
                window.Repaint();
        }
    }

    private static void LogLine(string line)
    {
        string full = "[SceneGuardCompare] " + line;
        report.AppendLine(full);
        Debug.Log(full);
        FlushReport();
    }

    private static void FlushReport()
    {
        try
        {
            string dir = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(OutputPath, report.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneGuardCompare] failed to write report: " + ex.Message);
        }
    }
}
