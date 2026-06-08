using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
/// <summary>
/// Evidence-based SceneView pipeline trace. No material redraw, no guessing.
/// Stages (sequential SceneView repaints):
///   1) after URP endCameraRendering (before any patch)
///   2) after context.Submit only
///   3) after magenta clear + Submit (RT path control)
/// Writes Library/SceneGuard/pipeline_trace_latest.txt and mirrors key lines to Console.
/// </summary>
[InitializeOnLoad]
public static class SceneGuardSceneViewPipelineTrace
{
    private const string OutputPath = "Library/SceneGuard/pipeline_trace_latest.txt";
    private const string CommandFile = "Library/SceneGuard/command.txt";

    private enum TraceStep
    {
        Idle,
        AfterUrpEnd,
        AfterSubmitOnly,
        AfterMagentaClearSubmit,
        Done
    }

    private static TraceStep step = TraceStep.Idle;
    private static bool readbackPending;
    private static readonly StringBuilder report = new StringBuilder();
    private static double nextCommandPollTime;

    static SceneGuardSceneViewPipelineTrace()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
            return;

        EditorApplication.update += OnEditorUpdate;
    }

    private static void OnEditorUpdate()
    {
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

        if (command == "trace-pipeline")
            BeginTrace("command");
    }

    public static bool IsTraceActive => step != TraceStep.Idle && step != TraceStep.Done;

    private static void BeginTrace(string source)
    {
        if (step != TraceStep.Idle && step != TraceStep.Done)
        {
            LogLine($"trace already running (step={step}); ignored request from {source}.");
            return;
        }

        if (SceneGuardGameVsSceneViewTrace.IsTraceActive)
        {
            LogLine($"compare trace is running; ignored request from {source}.");
            return;
        }

        report.Clear();
        report.AppendLine("========== SceneGuard SceneView Pipeline Trace ==========");
        report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Unity: {Application.unityVersion}");
        report.AppendLine($"Platform: {Application.platform}");
        report.AppendLine($"GraphicsDevice: {SystemInfo.graphicsDeviceType} / {SystemInfo.graphicsDeviceName}");
        report.AppendLine($"PlayMode: isPlaying={Application.isPlaying}");
        report.AppendLine($"Trigger: {source}");
        report.AppendLine();

        LogStaticState();
        LogRendererFeatures();

        step = TraceStep.AfterUrpEnd;
        readbackPending = false;

        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

        LogLine("trace started; waiting for SceneView renders (3 stages)...");
        SceneView.RepaintAll();
    }

    private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (step == TraceStep.Idle || step == TraceStep.Done)
            return;
        if (camera == null || camera.cameraType != CameraType.SceneView)
            return;

        if (!camera.enabled)
        {
            camera.enabled = true;
            LogLine("WARN: SceneView camera was disabled at beginCameraRendering; forced enabled.");
        }

        if (camera.targetTexture == null)
            LogLine("WARN: SceneView camera.targetTexture is NULL at beginCameraRendering.");
    }

    private static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (readbackPending || step == TraceStep.Idle || step == TraceStep.Done)
            return;
        if (camera == null || camera.cameraType != CameraType.SceneView)
            return;

        switch (step)
        {
            case TraceStep.AfterUrpEnd:
                LogLine("--- Stage 1: after URP endCameraRendering (no Submit, no patch) ---");
                RequestReadback(camera, "stage1-urp-end", () =>
                {
                    step = TraceStep.AfterSubmitOnly;
                    SceneView.RepaintAll();
                });
                break;

            case TraceStep.AfterSubmitOnly:
                LogLine("--- Stage 2: context.Submit() only ---");
                context.Submit();
                RequestReadback(camera, "stage2-after-submit", () =>
                {
                    step = TraceStep.AfterMagentaClearSubmit;
                    SceneView.RepaintAll();
                });
                break;

            case TraceStep.AfterMagentaClearSubmit:
                LogLine("--- Stage 3: magenta clear + Submit (RT writable control) ---");
                CommandBuffer cmd = new CommandBuffer { name = "SceneGuard Pipeline Trace Magenta" };
                cmd.ClearRenderTarget(false, true, Color.magenta);
                context.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
                context.Submit();
                RequestReadback(camera, "stage3-magenta-clear-submit", () =>
                {
                    step = TraceStep.Done;
                    WriteConclusion();
                    FinishTrace();
                });
                break;
        }
    }

    private static void LogStaticState()
    {
        report.AppendLine("=== SceneView / Camera ===");
        SceneView sv = SceneView.lastActiveSceneView;
        if (sv == null)
        {
            report.AppendLine("SceneView.lastActiveSceneView: NULL");
            return;
        }

        Camera cam = sv.camera;
        report.AppendLine($"sceneLighting={sv.sceneLighting} skyboxEnabled={sv.sceneViewState.skyboxEnabled}");
        report.AppendLine($"camera.enabled={cam.enabled} type={cam.cameraType} clearFlags={cam.clearFlags}");
        report.AppendLine($"camera.cullingMask=0x{cam.cullingMask:X8} hdr={cam.allowHDR} msaa={cam.allowMSAA}");
        RenderTexture rt = cam.targetTexture;
        report.AppendLine(rt == null
            ? "camera.targetTexture=NULL"
            : $"camera.targetTexture={rt.width}x{rt.height} fmt={rt.format} gfx={rt.graphicsFormat} depth={rt.depth}");
        report.AppendLine();

        report.AppendLine("=== Pipeline ===");
        var pipe = GraphicsSettings.defaultRenderPipeline;
        report.AppendLine(pipe != null ? $"defaultRenderPipeline={pipe.name} ({pipe.GetType().FullName})" : "defaultRenderPipeline=NULL");
        report.AppendLine($"QualitySettings.renderPipeline={(QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline.name : "NULL")}");
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
            return;
        }

        SerializedObject so = new SerializedObject(rd);
        SerializedProperty features = so.FindProperty("m_RendererFeatures");
        if (features == null || !features.isArray)
        {
            report.AppendLine("m_RendererFeatures missing");
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
        FlushReport();
    }

    private static void RequestReadback(Camera camera, string label, Action onComplete)
    {
        RenderTexture rt = camera != null ? camera.targetTexture : null;
        if (rt == null || rt.width <= 0 || rt.height <= 0)
        {
            LogLine($"readback {label}: SKIP (no targetTexture)");
            onComplete?.Invoke();
            return;
        }

        Vector2 center = new Vector2(rt.width * 0.5f, rt.height * 0.5f);
        readbackPending = true;
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
        {
            readbackPending = false;
            if (request.hasError)
            {
                LogLine($"readback {label}: GPU ERROR");
            }
            else
            {
                try
                {
                    var data = request.GetData<Color32>();
                    AnalyzePixels(label, data, rt.width, rt.height, center);
                }
                catch (Exception ex)
                {
                    LogLine($"readback {label}: parse failed {ex.Message}");
                }
            }

            onComplete?.Invoke();
        });
    }

    private static void AnalyzePixels(string label, Unity.Collections.NativeArray<Color32> data, int w, int h, Vector2 center)
    {
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

        Color32 centerColor = Sample(data, w, h, center);
        float nonBlackPct = total > 0 ? 100f * nonBlack / total : 0f;
        float brightPct = total > 0 ? 100f * bright / total : 0f;
        float avgLum = total > 0 ? sumLum / total : 0f;

        LogLine(
            $"readback {label}: rt={w}x{h} center={FormatColor(centerColor)} " +
            $"nonBlack={nonBlackPct:F2}% bright={brightPct:F2}% avgLum={avgLum:F5} maxLum={maxLum:F5}");
    }

    private static Color32 Sample(Unity.Collections.NativeArray<Color32> data, int w, int h, Vector2 pixel)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(pixel.x), 0, w - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(pixel.y), 0, h - 1);
        return data[Mathf.Clamp(y * w + x, 0, data.Length - 1)];
    }

    private static string FormatColor(Color32 c) => $"RGBA32({c.r},{c.g},{c.b},{c.a})";

    private static void WriteConclusion()
    {
        report.AppendLine("=== Conclusion Guide ===");
        report.AppendLine("stage1 black + stage3 magenta visible => URP draws nothing to SceneView RT; check GPUBatcher.ShowInSceneView / EcoEngine isSceneView path.");
        report.AppendLine("stage1 black + stage2 non-black => missing context.Submit in EcoEngine URP SceneView tail.");
        report.AppendLine("stage1 non-black => native URP output exists; problem is post-composite or editor display.");
        report.AppendLine("stage3 not magenta => SceneView RT not writable or wrong target.");
        report.AppendLine();
        FlushReport();
        LogLine("trace complete => " + Path.GetFullPath(OutputPath));
    }

    private static void FinishTrace()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
    }

    private static void LogLine(string line)
    {
        string full = "[SceneGuardTrace] " + line;
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
            Debug.LogWarning("[SceneGuardTrace] failed to write report: " + ex.Message);
        }
    }
}
