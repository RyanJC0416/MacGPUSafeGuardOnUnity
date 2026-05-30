# SceneGuard Phase 1 — Unity Diagnostic Script Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a temporary Unity Editor-only diagnostic script (`SceneGuardDiagnostics.cs`) that systematically diagnoses why SceneView is black on macOS, and attempts targeted repairs based on findings.

**Architecture:** A single static C# class with `[MenuItem]` entries. It inspects SceneView, RenderPipeline, RendererFeatures, lighting, and Editor.log errors via reflection and SerializedObject (no URP namespace dependencies). Repair logic is conditional — only applies fixes for diagnosed problems.

**Tech Stack:** Unity 2022.3, C#, UnityEditor API, SerializedObject reflection

---

## File Structure

| File | Location | Action | Notes |
|------|----------|--------|-------|
| `SceneGuardDiagnostics.cs` | `Assets/Editor/SceneGuardDiagnostics.cs` (Unity project) | **Create** | Editor-only, not in any asmdef |
| DevLog | `SceneGuard/devlogs/YYYY-MM-DD-<topic>.md` (Git repo) | **Create per task** | Commit to Git after each session |

**Important:** The Unity project uses **Perforce**, not Git. Before creating/modifying files in the Unity project, P4 checkout/add is required. Use changelist **`191167`** (`[Mac 适配]`).

---

## Task 1: P4 Checkout and Create File Skeleton

**Files:**
- Create: `Assets/Editor/SceneGuardDiagnostics.cs` (in Unity project at `/Users/ryan/Perforce/WorkSpace_Ryan_Mac/client/unity/`)

**Prerequisite:** The `Assets/Editor/` directory may not exist. Create it if needed.

- [ ] **Step 1: P4 add the new file**

```bash
# Run in terminal
p4 add -c 191167 "/Users/ryan/Perforce/WorkSpace_Ryan_Mac/client/unity/Assets/Editor/SceneGuardDiagnostics.cs"
```

Expected: `//WorkSpace_Ryan_Mac/client/unity/Assets/Editor/SceneGuardDiagnostics.cs#1 - opened for add`

- [ ] **Step 2: Write the file skeleton**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

public static class SceneGuardDiagnostics
{
    private static readonly StringBuilder _report = new StringBuilder();
    private static string _lastReport = "";

    [MenuItem("Performance/SceneGuard Diagnostics/Run Full Diagnosis")]
    private static void RunFullDiagnosis()
    {
        _report.Clear();
        Log("========== SceneGuard Diagnostics ==========");
        Log($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"Unity: {Application.unityVersion}");
        Log($"Platform: {Application.platform}");
        Log("");

        DiagnoseSceneView();
        DiagnoseRenderPipeline();
        DiagnoseRendererFeatures();
        DiagnoseLighting();
        ScanEditorLogForErrors();

        Log("");
        var assessment = GenerateAssessment();
        Log($"=== Assessment: {assessment} ===");

        _lastReport = _report.ToString();
        Debug.Log(_lastReport);
    }

    [MenuItem("Performance/SceneGuard Diagnostics/Attempt Repair")]
    private static void AttemptRepair()
    {
        _report.Clear();
        Log("========== SceneGuard Repair ==========");

        AttemptFixShowInSceneView();
        AttemptResetSceneViewCamera();
        AttemptForceRepaint();

        Log("=== Repair attempt complete ===");
        Log("Please observe SceneView and report whether it recovered.");
        Debug.Log(_report.ToString());
    }

    [MenuItem("Performance/SceneGuard Diagnostics/Show Last Report")]
    private static void ShowLastReport()
    {
        if (string.IsNullOrEmpty(_lastReport))
        {
            Debug.Log("[SceneGuard] No report available. Run Full Diagnosis first.");
            return;
        }
        Debug.Log(_lastReport);
    }

    private static void Log(string msg)
    {
        _report.AppendLine($"[SceneGuardDiagnostics] {msg}");
    }

    // === PLACEHOLDERS: will be filled in subsequent tasks ===
    private static void DiagnoseSceneView() { Log("SceneView: NOT YET IMPLEMENTED"); }
    private static void DiagnoseRenderPipeline() { Log("RenderPipeline: NOT YET IMPLEMENTED"); }
    private static void DiagnoseRendererFeatures() { Log("RendererFeatures: NOT YET IMPLEMENTED"); }
    private static void DiagnoseLighting() { Log("Lighting: NOT YET IMPLEMENTED"); }
    private static void ScanEditorLogForErrors() { Log("Editor.log: NOT YET IMPLEMENTED"); }
    private static string GenerateAssessment() { return "UNKNOWN"; }
    private static void AttemptFixShowInSceneView() { Log("Fix ShowInSceneView: NOT YET IMPLEMENTED"); }
    private static void AttemptResetSceneViewCamera() { Log("Reset camera: NOT YET IMPLEMENTED"); }
    private static void AttemptForceRepaint() { Log("Force repaint: NOT YET IMPLEMENTED"); }
}
```

- [ ] **Step 3: Save and ask user to compile in Unity**

Save the file. Prompt the user: "请切换到 Unity Editor，确认 Console 中无编译错误。如果有 CS 错误，请把错误信息贴给我。"

Expected: Unity compiles without errors. Three new menu items appear under `Performance/SceneGuard Diagnostics/`.

- [ ] **Step 4: Run smoke test**

In Unity Editor, click `Performance → SceneGuard Diagnostics → Run Full Diagnosis`.

Expected Console output:
```
[SceneGuardDiagnostics] ========== SceneGuard Diagnostics ==========
[SceneGuardDiagnostics] Time: ...
...
[SceneGuardDiagnostics] SceneView: NOT YET IMPLEMENTED
...
[SceneGuardDiagnostics] === Assessment: UNKNOWN ===
```

- [ ] **Step 5: Commit devlog**

Create `SceneGuard/devlogs/2026-05-30-phase1-kickoff.md` in the Git repo:

```markdown
# SceneGuard Phase 1 Kickoff

## Action
- Created `Assets/Editor/SceneGuardDiagnostics.cs` skeleton
- P4 add to CL 191167
- Menu items: Run Full Diagnosis / Attempt Repair / Show Last Report

## Test Result
- Unity compiles without errors
- Menu items visible

## Next
- Implement SceneView diagnosis (Task 2)
```

```bash
cd /Users/ryan/WorkSpace/MyProject/MacGPUSafeGuardOnUnity
git add SceneGuard/devlogs/2026-05-30-phase1-kickoff.md
git commit -m "docs: add SceneGuard Phase 1 kickoff devlog"
```

---

## Task 2: Implement SceneView Self-Diagnosis

**Files:**
- Modify: `Assets/Editor/SceneGuardDiagnostics.cs` — replace `DiagnoseSceneView()` placeholder

- [ ] **Step 1: Replace DiagnoseSceneView placeholder**

Replace the placeholder `DiagnoseSceneView()` method with:

```csharp
    private static void DiagnoseSceneView()
    {
        Log("=== Phase 1: SceneView Status ===");

        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null)
        {
            Log("SceneView.lastActiveSceneView: NULL");
            Log("  This means no SceneView window is active or available.");
            return;
        }

        Log("SceneView.lastActiveSceneView: VALID");
        Log($"  position: {sceneView.position}");
        Log($"  cameraMode: {sceneView.cameraMode}");
        Log($"  in2DMode: {sceneView.in2DMode}");
        Log($"  isRotationLocked: {sceneView.isRotationLocked}");

        var cam = sceneView.camera;
        if (cam == null)
        {
            Log("SceneView camera: NULL — CRITICAL");
            return;
        }

        Log($"SceneView camera: {(cam.enabled ? "ENABLED" : "DISABLED")}");
        Log($"  clearFlags: {cam.clearFlags}");
        Log($"  backgroundColor: {cam.backgroundColor}");
        Log($"  cullingMask: {cam.cullingMask} (layers: {LayerMaskToString(cam.cullingMask)})");
        Log($"  orthographic: {cam.orthographic}");
        Log($"  nearClipPlane: {cam.nearClipPlane}");
        Log($"  farClipPlane: {cam.farClipPlane}");
        Log($"  fieldOfView: {cam.fieldOfView}");
        Log($"  useOcclusionCulling: {cam.useOcclusionCulling}");
        Log($"  allowHDR: {cam.allowHDR}");
        Log($"  allowMSAA: {cam.allowMSAA}");

        // Check if camera is looking at nothing
        var forward = cam.transform.forward;
        Log($"  camera forward: {forward}");

        // Check SceneView overlays / grid
        var showGrid = sceneView.sceneViewState.showGrid;
        var showSkybox = sceneView.sceneViewState.skyboxEnabled;
        Log($"  showGrid: {showGrid}");
        Log($"  skyboxEnabled: {showSkybox}");
    }

    private static string LayerMaskToString(int mask)
    {
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                string name = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(name) ? $"Layer{i}" : name);
            }
        }
        return names.Count > 0 ? string.Join(", ", names) : "(none)";
    }
```

- [ ] **Step 2: Compile and test**

Save file. Ask user to compile in Unity.

Run: `Performance → SceneGuard Diagnostics → Run Full Diagnosis`

Expected: Console shows SceneView camera status, clear flags, culling mask, etc.

- [ ] **Step 3: Record devlog**

Append to `SceneGuard/devlogs/2026-05-30-phase1-kickoff.md`:

```markdown
## Task 2: SceneView Self-Diagnosis

### Action
- Implemented DiagnoseSceneView() with camera state inspection

### Test Result
- [ ] Menu works, outputs camera info
- [ ] Camera enabled/disabled status visible
- [ ] Culling mask shows active layers
```

Ask user to fill in `[ ]` after testing.

---

## Task 3: Implement Render Pipeline Diagnosis

**Files:**
- Modify: `Assets/Editor/SceneGuardDiagnostics.cs` — replace `DiagnoseRenderPipeline()` placeholder

- [ ] **Step 1: Replace DiagnoseRenderPipeline placeholder**

Replace the placeholder with:

```csharp
    private static void DiagnoseRenderPipeline()
    {
        Log("");
        Log("=== Phase 2: Render Pipeline Status ===");

        var rpAsset = GraphicsSettings.currentRenderPipelineAsset;
        if (rpAsset == null)
        {
            Log("GraphicsSettings.currentRenderPipelineAsset: NULL — CRITICAL");
            Log("  No render pipeline is active. SceneView will not render correctly.");
            return;
        }

        Log($"RenderPipelineAsset: {rpAsset.GetType().FullName}");
        Log($"  name: {rpAsset.name}");
        Log($"  instanceID: {rpAsset.GetInstanceID()}");

        var so = new SerializedObject(rpAsset);
        string[] props = {
            "m_RenderScale",
            "m_MainLightShadowmapResolution",
            "m_ShadowDistance",
            "m_ShadowCascadeCount",
            "m_SoftShadowQuality",
            "m_AdditionalLightsShadowmapResolution",
            "m_OpaqueDownsampling",
            "m_UseSRPBatcher",
        };

        foreach (var propName in props)
        {
            var prop = so.FindProperty(propName);
            if (prop != null)
            {
                Log($"  {propName}: {SerializedPropToString(prop)}");
            }
            else
            {
                Log($"  {propName}: NOT FOUND");
            }
        }

        // Check RendererDataList
        var rendererDataList = so.FindProperty("m_RendererDataList");
        if (rendererDataList != null && rendererDataList.isArray)
        {
            Log($"  RendererDataList count: {rendererDataList.arraySize}");
            for (int i = 0; i < rendererDataList.arraySize; i++)
            {
                var elem = rendererDataList.GetArrayElementAtIndex(i);
                if (elem?.objectReferenceValue != null)
                {
                    Log($"    [{i}]: {elem.objectReferenceValue.name} ({elem.objectReferenceValue.GetType().Name})");
                }
                else
                {
                    Log($"    [{i}]: NULL");
                }
            }
        }
        else
        {
            Log("  m_RendererDataList: NOT FOUND or not array");
        }
    }

    private static string SerializedPropToString(SerializedProperty prop)
    {
        switch (prop.propertyType)
        {
            case SerializedPropertyType.Integer: return prop.intValue.ToString();
            case SerializedPropertyType.Boolean: return prop.boolValue.ToString();
            case SerializedPropertyType.Float: return prop.floatValue.ToString("F2");
            case SerializedPropertyType.Enum: return $"{prop.enumValueIndex} ({prop.enumDisplayNames[prop.enumValueIndex]})");
            default: return $"({prop.propertyType})";
        }
    }
```

- [ ] **Step 2: Compile and test**

Save file. Ask user to compile in Unity.

Run: `Performance → SceneGuard Diagnostics → Run Full Diagnosis`

Expected: Console shows URP asset type, render scale, shadow settings, renderer data list.

- [ ] **Step 3: Record devlog**

Append test results to devlog.

---

## Task 4: Implement RendererFeature Diagnosis

**Files:**
- Modify: `Assets/Editor/SceneGuardDiagnostics.cs` — replace `DiagnoseRendererFeatures()` placeholder

- [ ] **Step 1: Replace DiagnoseRendererFeatures placeholder**

Replace the placeholder with:

```csharp
    private static void DiagnoseRendererFeatures()
    {
        Log("");
        Log("=== Phase 3: RendererFeature Status ===");

        var rpAsset = GraphicsSettings.currentRenderPipelineAsset;
        if (rpAsset == null)
        {
            Log("  Skipped: no RenderPipelineAsset");
            return;
        }

        var so = new SerializedObject(rpAsset);
        var rendererDataList = so.FindProperty("m_RendererDataList");
        if (rendererDataList == null || !rendererDataList.isArray)
        {
            Log("  m_RendererDataList not found");
            return;
        }

        for (int i = 0; i < rendererDataList.arraySize; i++)
        {
            var rdRef = rendererDataList.GetArrayElementAtIndex(i);
            if (rdRef == null || rdRef.objectReferenceValue == null)
            {
                Log($"  RendererData[{i}]: NULL reference");
                continue;
            }

            var rdSo = new SerializedObject(rdRef.objectReferenceValue);
            var featuresProp = rdSo.FindProperty("m_RendererFeatures");
            if (featuresProp == null || !featuresProp.isArray)
            {
                Log($"  RendererData[{i}]: {rdRef.objectReferenceValue.name} — m_RendererFeatures not found");
                continue;
            }

            Log($"  RendererData[{i}]: {rdRef.objectReferenceValue.name}, features={featuresProp.arraySize}");

            for (int j = 0; j < featuresProp.arraySize; j++)
            {
                var fe = featuresProp.GetArrayElementAtIndex(j);
                if (fe == null || fe.objectReferenceValue == null)
                {
                    Log($"    Feature[{j}]: NULL reference");
                    continue;
                }

                var fs = new SerializedObject(fe.objectReferenceValue);
                var nameProp = fs.FindProperty("m_Name");
                var activeProp = fs.FindProperty("m_Active");
                var showInSceneViewProp = fs.FindProperty("m_ShowInSceneView");

                string name = nameProp?.stringValue ?? "unnamed";
                bool active = activeProp?.boolValue ?? false;
                bool? showInSceneView = showInSceneViewProp != null ? (bool?)showInSceneViewProp.boolValue : null;

                string status = active ? "ACTIVE" : "INACTIVE";
                string showStr = showInSceneView.HasValue ? showInSceneView.Value.ToString() : "N/A";

                if (showInSceneView == false)
                {
                    Log($"    Feature[{j}]: {name} ⚠️ active={status}, ShowInSceneView={showStr} — MAY HIDE SCENEVIEW CONTENT");
                }
                else
                {
                    Log($"    Feature[{j}]: {name}, active={status}, ShowInSceneView={showStr}");
                }
            }
        }
    }
```

- [ ] **Step 2: Compile and test**

Save file. Ask user to compile in Unity.

Run: `Performance → SceneGuard Diagnostics → Run Full Diagnosis`

Expected: Console lists all RendererFeatures with active status and ShowInSceneView. Features with `ShowInSceneView=false` are flagged with ⚠️.

- [ ] **Step 3: Record devlog**

Append test results to devlog.

---

## Task 5: Implement Editor.log Error Scanning

**Files:**
- Modify: `Assets/Editor/SceneGuardDiagnostics.cs` — replace `ScanEditorLogForErrors()` placeholder

- [ ] **Step 1: Replace ScanEditorLogForErrors placeholder**

Replace the placeholder with:

```csharp
    private static void ScanEditorLogForErrors()
    {
        Log("");
        Log("=== Phase 4: Editor.log Error Scan ===");

        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            "Library", "Logs", "Unity", "Editor.log"
        );

        if (!File.Exists(logPath))
        {
            Log($"Editor.log: NOT FOUND at {logPath}");
            return;
        }

        try
        {
            // Read last ~200KB to avoid loading GB-sized logs
            var fileInfo = new FileInfo(logPath);
            long bytesToRead = Math.Min(200 * 1024, fileInfo.Length);
            long startPosition = fileInfo.Length - bytesToRead;

            var lines = new List<string>();
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(startPosition, SeekOrigin.Begin);
                using (var reader = new StreamReader(fs))
                {
                    reader.ReadLine(); // discard first partial line
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }
            }

            var keywords = new[] {
                "Error creating pipeline state",
                "mismatching vertex shader output",
                "fallback shader",
                "not found",
                "Metal: Error",
                "ShadowCache out of range",
                "Skipping draw calls to avoid crashing",
                "ComputeBuffer none provided",
                "TCTerrainLit",
                "Fragment input(s)",
            };

            int errorCount = 0;
            const int maxLinesToCheck = 200;
            int startIdx = Math.Max(0, lines.Count - maxLinesToCheck);

            for (int i = lines.Count - 1; i >= startIdx; i--)
            {
                var line = lines[i];
                bool isRelevant = line.Contains("error:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Error", StringComparison.Ordinal)
                    || line.Contains("WARNING", StringComparison.Ordinal)
                    || keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));

                if (isRelevant)
                {
                    Log($"  [LOG] {line.Trim()}");
                    errorCount++;
                    if (errorCount >= 20)
                    {
                        Log("  ... (truncated after 20 relevant lines)");
                        break;
                    }
                }
            }

            if (errorCount == 0)
            {
                Log("Editor.log: No relevant errors/warnings found in last 200 lines");
            }
            else
            {
                Log($"Editor.log: {errorCount} relevant lines found (scanned last {lines.Count - startIdx} lines)");
            }
        }
        catch (Exception ex)
        {
            Log($"Editor.log scan FAILED: {ex.Message}");
        }
    }
```

- [ ] **Step 2: Compile and test**

Save file. Ask user to compile in Unity.

Run: `Performance → SceneGuard Diagnostics → Run Full Diagnosis`

Expected: Console shows recent relevant errors from Editor.log, including Metal errors, fallback shader issues, etc.

- [ ] **Step 3: Record devlog**

Append test results to devlog.

---

## Task 6: Implement Lighting Diagnosis

**Files:**
- Modify: `Assets/Editor/SceneGuardDiagnostics.cs` — replace `DiagnoseLighting()` placeholder

- [ ] **Step 1: Replace DiagnoseLighting placeholder**

Replace the placeholder with:

```csharp
    private static void DiagnoseLighting()
    {
        Log("");
        Log("=== Phase 5: Lighting & Environment ===");

        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        Log($"Scene lights total: {lights.Length}");

        var activeLights = lights.Where(l => l.enabled).ToArray();
        Log($"  Active lights: {activeLights.Length}");

        var directional = activeLights.Where(l => l.type == LightType.Directional).ToArray();
        if (directional.Length == 0)
        {
            Log("  ⚠️ No active Directional Light found — SceneView will be dark");
        }
        else
        {
            foreach (var dl in directional.Take(3))
            {
                Log($"  Directional: {dl.name}, intensity={dl.intensity:F2}, color={dl.color}");
            }
        }

        var point = activeLights.Where(l => l.type == LightType.Point).ToArray();
        var spot = activeLights.Where(l => l.type == LightType.Spot).ToArray();
        Log($"  Point lights: {point.Length}, Spot lights: {spot.Length}");

        Log($"Ambient mode: {RenderSettings.ambientMode}");
        Log($"Ambient intensity: {RenderSettings.ambientIntensity:F2}");
        Log($"Ambient color: {RenderSettings.ambientLight}");
        Log($"Ambient sky color: {RenderSettings.ambientSkyColor}");

        Log($"Default reflection mode: {RenderSettings.defaultReflectionMode}");
        Log($"Reflection intensity: {RenderSettings.reflectionIntensity:F2}");

        // Check if any post-processing or skybox might be interfering
        var skybox = RenderSettings.skybox;
        Log($"Skybox material: {(skybox != null ? skybox.name : "NULL")}");

        var sun = RenderSettings.sun;
        Log($"Sun source: {(sun != null ? sun.name : "NULL")}");
    }
```

- [ ] **Step 2: Compile and test**

Save file. Ask user to compile in Unity.

Run: `Performance → SceneGuard Diagnostics → Run Full Diagnosis`

Expected: Console shows active lights, ambient settings, skybox status.

- [ ] **Step 3: Record devlog**

Append test results to devlog.

---

## Task 7: Implement Assessment Logic

**Files:**
- Modify: `Assets/Editor/SceneGuardDiagnostics.cs` — replace `GenerateAssessment()` placeholder

- [ ] **Step 1: Add state tracking fields and replace assessment method**

Add these fields at the top of the class (after `_lastReport`):

```csharp
    private static bool _sceneViewNull = false;
    private static bool _sceneViewCameraNull = false;
    private static bool _sceneViewCameraDisabled = false;
    private static bool _rpAssetNull = false;
    private static int _editorLogErrorCount = 0;
    private static List<string> _showInSceneViewOffFeatures = new List<string>();
    private static bool _noDirectionalLight = false;
```

Replace `GenerateAssessment()` with:

```csharp
    private static string GenerateAssessment()
    {
        var issues = new List<string>();

        if (_sceneViewNull) issues.Add("SceneView is null");
        if (_sceneViewCameraNull) issues.Add("SceneView camera is null");
        if (_sceneViewCameraDisabled) issues.Add("SceneView camera is disabled");
        if (_rpAssetNull) issues.Add("RenderPipelineAsset is null");
        if (_editorLogErrorCount > 0) issues.Add($"Editor.log has {_editorLogErrorCount} relevant errors");
        if (_showInSceneViewOffFeatures.Count > 0) issues.Add($"{_showInSceneViewOffFeatures.Count} features have ShowInSceneView=false");
        if (_noDirectionalLight) issues.Add("No active directional light");

        if (issues.Count == 0)
        {
            Log("Suspected root cause: none detected — SceneView should be rendering");
            return "HEALTHY";
        }

        foreach (var issue in issues)
        {
            Log($"Suspected issue: {issue}");
        }

        bool critical = _sceneViewNull || _sceneViewCameraNull || _rpAssetNull;
        return critical ? "BROKEN" : "DEGRADED";
    }
```

- [ ] **Step 2: Update diagnostic methods to set state flags**

In `DiagnoseSceneView()`, add state tracking after the null checks:

```csharp
        _sceneViewNull = (sceneView == null);
        _sceneViewCameraNull = (cam == null);
        _sceneViewCameraDisabled = (cam != null && !cam.enabled);
```

In `DiagnoseRenderPipeline()`, add:

```csharp
        _rpAssetNull = (rpAsset == null);
```

In `ScanEditorLogForErrors()`, add before the method returns:

```csharp
        _editorLogErrorCount = errorCount;
```

In `DiagnoseRendererFeatures()`, inside the inner loop where ShowInSceneView is checked, add:

```csharp
                if (showInSceneView == false)
                {
                    _showInSceneViewOffFeatures.Add(name);
                    Log($"    Feature[{j}]: {name} ⚠️ ...");
                }
```

And reset the list at the start:

```csharp
        _showInSceneViewOffFeatures.Clear();
```

In `DiagnoseLighting()`, add:

```csharp
        _noDirectionalLight = (directional.Length == 0);
```

- [ ] **Step 3: Compile and test**

Save file. Ask user to compile in Unity.

Run: `Performance → SceneGuard Diagnostics → Run Full Diagnosis`

Expected: Final assessment line shows `BROKEN`, `DEGRADED`, or `HEALTHY` with suspected issues listed.

- [ ] **Step 4: Record devlog**

Append test results to devlog.

---

## Task 8: Implement Repair Logic

**Files:**
- Modify: `Assets/Editor/SceneGuardDiagnostics.cs` — replace all three repair placeholders

- [ ] **Step 1: Replace repair placeholders**

Replace the three repair placeholder methods with:

```csharp
    private static void AttemptFixShowInSceneView()
    {
        if (_showInSceneViewOffFeatures.Count == 0)
        {
            Log("Fix ShowInSceneView: no features need fixing");
            return;
        }

        Log($"Fix ShowInSceneView: {_showInSceneViewOffFeatures.Count} feature(s) to fix");

        var rpAsset = GraphicsSettings.currentRenderPipelineAsset;
        if (rpAsset == null) return;

        var so = new SerializedObject(rpAsset);
        var rendererDataList = so.FindProperty("m_RendererDataList");
        if (rendererDataList == null || !rendererDataList.isArray) return;

        int fixedCount = 0;
        for (int i = 0; i < rendererDataList.arraySize; i++)
        {
            var rdRef = rendererDataList.GetArrayElementAtIndex(i);
            if (rdRef == null || rdRef.objectReferenceValue == null) continue;

            var rdSo = new SerializedObject(rdRef.objectReferenceValue);
            var featuresProp = rdSo.FindProperty("m_RendererFeatures");
            if (featuresProp == null || !featuresProp.isArray) continue;

            bool rdModified = false;
            for (int j = 0; j < featuresProp.arraySize; j++)
            {
                var fe = featuresProp.GetArrayElementAtIndex(j);
                if (fe == null || fe.objectReferenceValue == null) continue;

                var fs = new SerializedObject(fe.objectReferenceValue);
                var nameProp = fs.FindProperty("m_Name");
                var showProp = fs.FindProperty("m_ShowInSceneView");

                string name = nameProp?.stringValue ?? "";
                if (showProp != null && _showInSceneViewOffFeatures.Contains(name))
                {
                    showProp.boolValue = true;
                    fs.ApplyModifiedProperties();
                    rdModified = true;
                    fixedCount++;
                    Log($"  Fixed: {name} → ShowInSceneView=true");
                }
            }

            if (rdModified)
            {
                rdSo.ApplyModifiedProperties();
            }
        }

        if (fixedCount > 0)
        {
            so.ApplyModifiedProperties();
            Log($"Fix ShowInSceneView: {fixedCount} feature(s) fixed. You may need to wait for Unity to refresh.");
        }
    }

    private static void AttemptResetSceneViewCamera()
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null)
        {
            Log("Reset camera: SceneView is null, cannot reset");
            return;
        }

        var cam = sceneView.camera;
        if (cam == null)
        {
            Log("Reset camera: SceneView camera is null — cannot reset via script");
            Log("  SUGGESTION: Try opening a new SceneView tab (Window → General → Scene)");
            return;
        }

        Log("Reset camera: attempting to restore safe defaults");

        cam.enabled = true;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.backgroundColor = new Color(0.192f, 0.302f, 0.475f); // default Unity blue
        cam.cullingMask = ~0; // all layers
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 1000f;

        Log("  camera.enabled = true");
        Log("  camera.clearFlags = Skybox");
        Log("  camera.cullingMask = Everything");
        Log("  camera.near/far = 0.01 / 1000");

        // Try to reset SceneView camera mode
        try
        {
            sceneView.sceneViewState.showGrid = true;
            sceneView.sceneViewState.skyboxEnabled = true;
            sceneView.sceneViewState.flaresEnabled = true;
            sceneView.sceneViewState.imageEffectsEnabled = true;
            Log("  SceneViewState: grid/skybox/flares/imageEffects enabled");
        }
        catch (Exception ex)
        {
            Log($"  SceneViewState reset failed: {ex.Message}");
        }
    }

    private static void AttemptForceRepaint()
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView != null)
        {
            sceneView.Repaint();
            Log("Force repaint: SceneView.Repaint() called");
        }

        // Also try to repaint all SceneViews
        var allSceneViews = SceneView.sceneViews;
        if (allSceneViews != null)
        {
            foreach (SceneView sv in allSceneViews)
            {
                if (sv != null)
                {
                    sv.Repaint();
                }
            }
            Log($"Force repaint: repainted {allSceneViews.Length} SceneView(s)");
        }

        // Force pipeline rebuild
        try
        {
            var rpAsset = GraphicsSettings.currentRenderPipelineAsset;
            if (rpAsset != null)
            {
                var so = new SerializedObject(rpAsset);
                so.ApplyModifiedProperties();
                Log("Force repaint: touched RenderPipelineAsset to trigger rebuild");
            }
        }
        catch (Exception ex)
        {
            Log($"Force pipeline rebuild failed: {ex.Message}");
        }
    }
```

- [ ] **Step 2: Compile and test**

Save file. Ask user to compile in Unity.

Run: `Performance → SceneGuard Diagnostics → Attempt Repair`

Expected: Console shows repair actions taken. User observes SceneView — may or may not recover yet.

- [ ] **Step 3: Record devlog with full results**

Create a new devlog `SceneGuard/devlogs/2026-05-30-repair-attempt-v1.md`:

```markdown
# SceneGuard Repair Attempt v1

## Diagnosis Summary
- SceneView status: [user fills]
- RenderPipelineAsset: [user fills]
- RendererFeatures with ShowInSceneView=false: [user fills]
- Editor.log relevant errors: [user fills]
- Assessment: [HEALTHY/DEGRADED/BROKEN]

## Repair Attempt
- Fixed ShowInSceneView: [count]
- Reset camera defaults: [yes/no]
- Force repaint: [yes/no]

## Result
- SceneView recovered: [YES / NO / PARTIAL]
- If NO: still seeing [black screen / only gizmos / other]

## Next Steps
- [ ] Based on diagnosis output, determine what else to investigate
```

```bash
cd /Users/ryan/WorkSpace/MyProject/MacGPUSafeGuardOnUnity
git add SceneGuard/devlogs/2026-05-30-repair-attempt-v1.md
git commit -m "docs: add SceneGuard repair attempt v1 devlog"
```

---

## Task 9: Iterate Based on Results

**Goal:** If SceneView did NOT recover after Task 8, analyze the diagnosis output and iterate.

- [ ] **Step 1: Analyze diagnosis output**

Review the Full Diagnosis output with the user. Key questions:

1. Is `SceneView.lastActiveSceneView` null? → Open new SceneView tab (`Window → General → Scene`)
2. Is `RenderPipelineAsset` null? → Check `Project Settings → Graphics → Scriptable Render Pipeline`
3. Are there `Error creating pipeline state` errors in Editor.log? → Likely Metal/Shader incompatibility
4. Are there `fallback shader ... not found` errors? → Missing shader references
5. Is `ShowInSceneView=false` on critical features? → Already fixed by repair logic
6. Is SceneView camera `cullingMask = 0` or disabled? → Already fixed by repair logic

- [ ] **Step 2: Add targeted diagnostics based on findings**

If the root cause is still unclear, add more specific diagnostics:

```csharp
    // Add to DiagnoseSceneView() if needed:
    // Check if the camera transform is at origin looking at nothing
    Log($"  camera position: {cam.transform.position}");
    Log($"  camera rotation: {cam.transform.rotation.eulerAngles}");

    // Check if SceneView is using a valid render texture
    if (cam.targetTexture != null)
    {
        Log($"  camera.targetTexture: {cam.targetTexture.width}x{cam.targetTexture.height}");
    }
    else
    {
        Log("  camera.targetTexture: null (rendering to screen/backbuffer)");
    }
```

Or if Metal errors are found:

```csharp
    // Add to ScanEditorLogForErrors() — already scanning for Metal errors
    // Consider reading more of the log (500KB instead of 200KB)
```

- [ ] **Step 3: Try additional repairs**

If standard repairs don't work, try:

```csharp
    [MenuItem("Performance/SceneGuard Diagnostics/Force Rebuild Pipeline")]
    private static void ForceRebuildPipeline()
    {
        Log("Force rebuilding render pipeline...");
        var rpAsset = GraphicsSettings.currentRenderPipelineAsset;
        if (rpAsset != null)
        {
            GraphicsSettings.defaultRenderPipeline = null;
            GraphicsSettings.defaultRenderPipeline = rpAsset;
            Log("Pipeline asset re-assigned");
        }
        EditorApplication.ExecuteMenuItem("Window/General/Scene");
        Log("Opened fresh SceneView");
    }
```

- [ ] **Step 4: Record iteration in devlog**

Each iteration gets its own devlog entry until SceneView recovers.

---

## Spec Coverage Check

| Spec Requirement | Implementing Task |
|------------------|-------------------|
| Editor-only script at `Assets/Editor/` | Task 1 |
| Menu item: Run Full Diagnosis | Task 1 |
| Menu item: Attempt Repair | Task 1 |
| Diagnose SceneView (camera, state) | Task 2 |
| Diagnose RenderPipeline (URP asset) | Task 3 |
| Diagnose RendererFeatures (ShowInSceneView) | Task 4 |
| Scan Editor.log for Metal/Shader errors | Task 5 |
| Diagnose Lighting & Environment | Task 6 |
| Assessment: HEALTHY/DEGRADED/BROKEN | Task 7 |
| Repair: Fix ShowInSceneView | Task 8 |
| Repair: Reset camera defaults | Task 8 |
| Repair: Force repaint | Task 8 |
| DevLog for each session | Every task |
| Iteration until root cause found | Task 9 |

**No gaps found.**

---

## Placeholder Scan

- No `TBD`, `TODO`, or `implement later` strings in code.
- No vague instructions like "add appropriate error handling" — all error handling is explicit.
- No `Similar to Task N` references — each task is self-contained.
- All file paths are exact.
