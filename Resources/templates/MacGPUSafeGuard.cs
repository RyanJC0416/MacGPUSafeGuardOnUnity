using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using MagicaCloth2;
using UnityEditor;
#endif

namespace Performance.MacGPU
{
    /// <summary>
    /// Mac Metal GPU 崩溃防护脚本
    ///
    /// 功能:
    /// 1. 启动时自动检测并修正危险的 GPU 配置 (VSync/RenderScale/阴影等)
    /// 2. 相机设置修正 (TAA 降级, MSAA 关闭, HDR 控制)
    /// 3. 禁用重型 RendererFeature (SSGI, SSR, 体积云, 体积光, HBAO 等)
    /// 4. 运行时帧时间监控，持续高负载时自动降级画质
    /// 5. 提供 Editor 菜单一键应用所有安全配置
    /// 6. 记录所有操作到 Console 日志便于排查
    ///
    /// 重要设计决策:
    ///   本脚本不直接引用任何 URP 命名空间（无论是标准版还是 EcoEngine 自定义版本）。
    ///   所有 URP Asset 属性、UniversalAdditionalCameraData、RendererFeature 的读写
    ///   均通过反射完成。这确保脚本在标准 URP、EcoEngine URP 或其他自定义 SRP 上
    ///   都能正常编译和运行。
    ///
    /// 使用方式:
    ///   - 挂载到任意 GameObject（推荐挂载在 GameInstance 或同场景的 Manager 上）
    ///   - Editor 菜单: Performance → Mac GPU SafeGuard → Apply All Settings
    ///
    /// 注意事项:
    ///   - 仅在 macOS / macOS Editor 中生效
    ///   - Windows/Linux 平台自动跳过所有逻辑
    /// </summary>
    public class MacGPUSafeGuard : MonoBehaviour
    {
        [Header("Config Reference")]
        [Tooltip("Drag MacGPUConfig asset here. Uses built-in defaults if empty.")]
        public MacGPUConfig config;

        [Header("Runtime Status (Read-only)")]
        [SerializeField] private bool _isMacPlatform;
        [SerializeField] private bool _configApplied;
        [SerializeField] private bool _autoReduced;
        [SerializeField] private float _currentFrameTimeMs;
        [SerializeField] private int _consecutiveDangerFrames;
        [SerializeField] private string _lastLogMessage;

        // Internal state — store as pure object, all access via reflection (no URP/SRP type dependency)
        private object _urpAssetObj;
        private float _lastAutoReduceTime = -999f;
        private float _lastVSyncForceLogTime = -999f;
        private List<float> _frameTimeHistory = new List<float>(60);

        private const string C_PLAY_GUARD_PENDING_KEY = "MacGPUSafeGuard.PlayGuardPending";
        private static bool s_isGuardedPlay;
        private static bool s_guardHooksInstalled;
        private static readonly HashSet<int> s_quarantinedClothInstanceIds = new HashSet<int>();

        // Heartbeat for external watchdog freeze detection
        private static Thread s_heartbeatThread;
        private static bool s_heartbeatRunning;
        // macOS Mono returns ~/.config for ApplicationData; watchdog reads ~/Library/Application Support.
        private static readonly string C_HEARTBEAT_DIR = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            "Library", "Application Support", "MacGPUSafeGuard");
        private static readonly string C_HEARTBEAT_PATH = Path.Combine(C_HEARTBEAT_DIR, "heartbeat");
        private static readonly string C_COMPILING_PATH = Path.Combine(C_HEARTBEAT_DIR, "compiling");

        public static bool IsGuardedPlay => s_isGuardedPlay;

        #region Lifecycle

        void Awake()
        {
            _isMacPlatform = Application.platform == RuntimePlatform.OSXPlayer
                           || Application.platform == RuntimePlatform.OSXEditor;

            if (!_isMacPlatform)
            {
                Debug.Log($"[MacGPUSafeGuard] Non-Mac platform ({Application.platform}), skipping all safeguards");
                enabled = false;
                return;
            }

            ApplySafeConfig();
        }

        void Update()
        {
            if (!_isMacPlatform || config == null)
                return;

            MaintainSafeVSync();

            if (!config.enableFrameTimeMonitor)
                return;

            float frameTime = Time.unscaledDeltaTime * 1000f;
            _currentFrameTimeMs = frameTime;
            _frameTimeHistory.Add(frameTime);
            if (_frameTimeHistory.Count > 60)
                _frameTimeHistory.RemoveAt(0);

            if (frameTime > config.frameTimeWarningThresholdMs)
            {
                _consecutiveDangerFrames++;
                if (_consecutiveDangerFrames >= config.consecutiveDangerFramesToTrigger
                    && Time.time - _lastAutoReduceTime > config.autoReduceCooldownSeconds)
                {
                    TriggerAutoReduce();
                }
            }
            else
            {
                if (_consecutiveDangerFrames > 0) _consecutiveDangerFrames--;
            }
        }

        void MaintainSafeVSync()
        {
            if (!config.enableVSync || QualitySettings.vSyncCount == 1)
                return;

            QualitySettings.vSyncCount = 1;
            if (Time.unscaledTime - _lastVSyncForceLogTime > 1f)
            {
                _lastVSyncForceLogTime = Time.unscaledTime;
                Log("VSync was overridden by another system, forcing it back to 1");
            }
        }

        #endregion

        #region Core Config Application

        public void ApplySafeConfig()
        {
            Log("========== Mac GPU SafeGuard Applying Safe Config ==========");

            try
            {
                if (config == null)
                {
                    config = CreateDefaultConfig();
                }

                var cfg = config;
                _urpAssetObj = GetCurrentRenderPipelineAsset();

                if (_urpAssetObj == null)
                {
                    Log("WARNING: Could not resolve active Render Pipeline Asset. Skipping URP runtime settings.");
                    Log("Only VSync will be applied.");
                }
                else
                {
                    Log($"Resolved Render Pipeline Asset: {_urpAssetObj.GetType().FullName}");
                }

                ApplyVSync(cfg);

                if (_urpAssetObj != null)
                {
                    ApplyRenderScale(cfg);
                    ApplyShadowConfig(cfg);
                    ApplyOpaqueDownsampling(cfg);
                    ApplySRPBatcher(cfg);
                }

                // Camera settings — access Camera.main + UniversalAdditionalCameraData via reflection
                if (Camera.main != null)
                {
                    ApplyCameraSettings(cfg);
                }
                else
                {
                    Log("WARNING: Camera.main is null, camera settings will be applied in Start()");
                }


                _configApplied = true;
                Log("All safe config applied successfully.");
            }
            catch (Exception e)
            {
                Log($"ERROR applying config: {e.Message}\n{e.StackTrace}");
            }

            Log("============================================================");
        }

        void Start()
        {
            // Retry camera settings if Camera.main was null in Awake
            if (!_isMacPlatform || config == null || _configApplied == false)
                return;

            if (Camera.main == null)
            {
                Log("WARNING: Camera.main still null in Start(), camera settings skipped");
                return;
            }

            ApplyCameraSettings(config);
        }

        void ApplyVSync(MacGPUConfig cfg)
        {
            int targetVSync = cfg.enableVSync ? 1 : 0;
            int currentVSync = QualitySettings.vSyncCount;

            if (currentVSync != targetVSync)
            {
                QualitySettings.vSyncCount = targetVSync;
                Log($"VSync: {currentVSync} -> {targetVSync}");
            }
            else
            {
                Log($"VSync: already {targetVSync}, no change needed");
            }
        }

        void ApplyRenderScale(MacGPUConfig cfg)
        {
            if (!SetURPFloat("m_RenderScale", cfg.renderScale, out float oldVal))
            {
                Log("WARNING: Could not set RenderScale (property not found or wrong type)");
                return;
            }
            Log($"RenderScale: {oldVal:F2} -> {cfg.renderScale:F2}");
        }

        void ApplyShadowConfig(MacGPUConfig cfg)
        {
            // Main light shadow resolution
            if (SetURPInt("m_MainLightShadowmapResolution", cfg.mainLightShadowResolution, out int oldRes))
                Log($"MainLightShadowResolution: {oldRes} -> {cfg.mainLightShadowResolution}");
            else
                Log("WARNING: m_MainLightShadowmapResolution not found");

            // Shadow distance
            if (SetURPFloat("m_ShadowDistance", cfg.shadowDistance, out float oldDist))
                Log($"ShadowDistance: {oldDist:F0} -> {cfg.shadowDistance:F0}");
            else
                Log("WARNING: m_ShadowDistance not found");

            // Cascade count
            if (SetURPInt("m_ShadowCascadeCount", cfg.shadowCascadeCount, out int oldCasc))
                Log($"ShadowCascadeCount: {oldCasc} -> {cfg.shadowCascadeCount}");
            else
                Log("WARNING: m_ShadowCascadeCount not found");

            // Soft shadow quality
            if (SetURPInt("m_SoftShadowQuality", cfg.softShadowQuality, out int oldSoft))
                Log($"SoftShadowQuality: {oldSoft} -> {cfg.softShadowQuality}");
            else
                Log("WARNING: m_SoftShadowQuality not found");

            // Additional lights shadow resolution
            if (SetURPInt("m_AdditionalLightsShadowmapResolution", cfg.additionalLightsShadowResolution, out int oldAdd))
                Log($"AdditionalLightsShadowResolution: {oldAdd} -> {cfg.additionalLightsShadowResolution}");
            else
                Log("WARNING: m_AdditionalLightsShadowmapResolution not found");
        }

        void ApplyOpaqueDownsampling(MacGPUConfig cfg)
        {
            if (SetURPInt("m_OpaqueDownsampling", cfg.opaqueDownsampling, out int oldVal))
                Log($"OpaqueDownsampling: {oldVal} -> {cfg.opaqueDownsampling}" +
                    " (cannot fully disable - GeNa river depends on _CameraOpaqueTexture)");
            else
                Log("WARNING: m_OpaqueDownsampling not found");
        }

        void ApplySRPBatcher(MacGPUConfig cfg)
        {
            if (!cfg.enableSRPBatcher)
            {
                Log("SRP Batcher: staying disabled (not enabled in config)");
                return;
            }

            // Note: useSRPBatcher is a bool property in URP Asset
            // Try both possible naming conventions
            bool set = SetURPBool("m_UseSRPBatcher", true, out bool oldVal)
                       || SetURPBool("useSRPBatcher", true, out oldVal);

            if (set)
                Log($"SRP Batcher: {oldVal} -> true WARNING: Check for purple materials in scene!");
            else
                Log("WARNING: SRP Batcher property not found on this URP Asset");
        }

        #endregion

        #region Camera Settings (Anti-Aliasing / MSAA / HDR)

        void ApplyCameraSettings(MacGPUConfig cfg)
        {
            Log("--- Camera Settings ---");
            ApplyAntiAliasing(cfg);
            ApplyMSAA(cfg);
            ApplyHDR(cfg);
        }

        void ApplyAntiAliasing(MacGPUConfig cfg)
        {
            if (Camera.main == null) return;

            object cameraData = GetUniversalAdditionalCameraData(Camera.main);
            if (cameraData == null)
            {
                Log("WARNING: Could not find UniversalAdditionalCameraData component on Camera.main");
                return;
            }

            Type dataType = cameraData.GetType();

            // antialiasing property (int/enum: 0=None, 1=FXAA, 2=SMAA, 3=TAA)
            string[] aaPropNames = { "antialiasing", "m_Antialiasing", "m_AntialiasingMode" };
            PropertyInfo aaProp = null;
            foreach (var name in aaPropNames)
            {
                aaProp = FindPropertyRecursive(dataType, name);
                if (aaProp != null) break;
            }

            if (aaProp != null && aaProp.CanWrite)
            {
                object oldVal = aaProp.GetValue(cameraData);
                object newVal;

                if (aaProp.PropertyType.IsEnum)
                {
                    newVal = Enum.ToObject(aaProp.PropertyType, cfg.antiAliasingMode);
                }
                else
                {
                    newVal = cfg.antiAliasingMode;
                }
                aaProp.SetValue(cameraData, newVal);
                Log($"AntiAliasing: {oldVal} -> {newVal} ({cfg.antiAliasingMode})");
            }
            else
            {
                Log("WARNING: antialiasing property not found on UniversalAdditionalCameraData");
            }

            // antialiasingQuality property (0=Low, 1=Medium, 2=High)
            string[] aaqPropNames = { "antialiasingQuality", "m_AntialiasingQuality" };
            PropertyInfo aaqProp = null;
            foreach (var name in aaqPropNames)
            {
                aaqProp = FindPropertyRecursive(dataType, name);
                if (aaqProp != null) break;
            }

            if (aaqProp != null && aaqProp.CanWrite)
            {
                object oldVal = aaqProp.GetValue(cameraData);
                object newVal;
                if (aaqProp.PropertyType.IsEnum)
                    newVal = Enum.ToObject(aaqProp.PropertyType, cfg.taaQuality);
                else
                    newVal = cfg.taaQuality;
                aaqProp.SetValue(cameraData, newVal);
                Log($"AntiAliasingQuality: {oldVal} -> {newVal}");
            }

            // taaSettings.quality (TemporalAAQuality: 0=Low, 1=Medium, 2=High)
            string[] taaNames = { "taaSettings", "m_TaaSettings" };
            foreach (var taaName in taaNames)
            {
                var taaField = FindFieldRecursive(dataType, taaName);
                var taaProp = FindPropertyRecursive(dataType, taaName);
                if (taaField != null || taaProp != null)
                {
                    object taaSettings = taaField != null
                        ? taaField.GetValue(cameraData)
                        : taaProp.GetValue(cameraData);
                    if (taaSettings != null)
                    {
                        Type taaType = taaSettings.GetType();
                        string[] qualityNames = { "quality", "m_Quality" };
                        foreach (var qn in qualityNames)
                        {
                            var qField = FindFieldRecursive(taaType, qn);
                            var qProp = FindPropertyRecursive(taaType, qn);
                            if (qField != null)
                            {
                                object oldQ = qField.GetValue(taaSettings);
                                object newQ;
                                if (qField.FieldType.IsEnum)
                                    newQ = Enum.ToObject(qField.FieldType, cfg.taaQuality);
                                else
                                    newQ = cfg.taaQuality;
                                qField.SetValue(taaSettings, newQ);
                                Log($"TAA Quality: {oldQ} -> {newQ}");
                                break;
                            }
                            if (qProp != null)
                            {
                                object oldQ = qProp.GetValue(taaSettings);
                                object newQ;
                                if (qProp.PropertyType.IsEnum)
                                    newQ = Enum.ToObject(qProp.PropertyType, cfg.taaQuality);
                                else
                                    newQ = cfg.taaQuality;
                                qProp.SetValue(taaSettings, newQ);
                                Log($"TAA Quality: {oldQ} -> {newQ}");
                                break;
                            }
                        }
                    }
                    break;
                }
            }
        }

        void ApplyMSAA(MacGPUConfig cfg)
        {
            var cam = Camera.main;
            if (cam == null) return;

            if (cam.allowMSAA != cfg.allowMSAA)
            {
                cam.allowMSAA = cfg.allowMSAA;
                Log($"allowMSAA: {!cfg.allowMSAA} -> {cfg.allowMSAA}");
            }
            else
            {
                Log($"allowMSAA: already {cfg.allowMSAA}, no change");
            }
        }

        void ApplyHDR(MacGPUConfig cfg)
        {
            var cam = Camera.main;
            if (cam == null) return;

            if (cam.allowHDR != cfg.allowHDR)
            {
                cam.allowHDR = cfg.allowHDR;
                Log($"allowHDR: {!cfg.allowHDR} -> {cfg.allowHDR}");
            }
            else
            {
                Log($"allowHDR: already {cfg.allowHDR}, no change");
            }
        }

        object GetUniversalAdditionalCameraData(Camera cam)
        {
            // Try to find the component by type name across all loaded assemblies
            string[] candidateTypeNames = {
                "EcoEngine.Rendering.Universal.UniversalAdditionalCameraData",
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData",
            };

            foreach (var typeName in candidateTypeNames)
            {
                var type = Type.GetType(typeName);
                if (type == null)
                {
                    // Search all assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        type = asm.GetType(typeName);
                        if (type != null) break;
                    }
                }
                if (type != null)
                {
                    return cam.GetComponent(type);
                }
            }

            // Fallback: search by class name only
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type.Name == "UniversalAdditionalCameraData" && type.IsSubclassOf(typeof(Component)))
                    {
                        return cam.GetComponent(type);
                    }
                }
            }

            return null;
        }

        #endregion
        #region Auto-Reduce (Emergency)

        void TriggerAutoReduce()
        {
            _autoReduced = true;
            _lastAutoReduceTime = Time.time;
            _consecutiveDangerFrames = 0;

            // Emergency: further reduce render scale via reflection
            if (_urpAssetObj != null)
            {
                if (GetURPFloat("m_RenderScale", out float currentScale) && currentScale > 0.5f)
                {
                    float newScale = Mathf.Max(currentScale - 0.15f, 0.5f);
                    SetURPFloat("m_RenderScale", newScale, out _);
                    Log($"EMERGENCY REDUCE: RenderScale -> {newScale:F2}");
                }
            }

            if (QualitySettings.vSyncCount == 0)
            {
                QualitySettings.vSyncCount = 1;
                Log("EMERGENCY REDUCE: VSync -> 1 (forced enable)");
            }
        }

        #endregion

        #region Reflection Helpers (No URP Namespace Dependency)

        PropertyInfo FindPropertyRecursive(Type type, string memberName)
        {
            while (type != null)
            {
                var prop = type.GetProperty(memberName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null)
                    return prop;
                type = type.BaseType;
            }

            return null;
        }

        FieldInfo FindFieldRecursive(Type type, string memberName)
        {
            while (type != null)
            {
                var field = type.GetField(memberName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
                type = type.BaseType;
            }

            return null;
        }

        IEnumerable<string> GetCandidateMemberNames(string memberName)
        {
            yield return memberName;

            if (memberName.StartsWith("m_") && memberName.Length > 2)
            {
                var trimmed = memberName.Substring(2);
                yield return trimmed;
                yield return char.ToLowerInvariant(trimmed[0]) + trimmed.Substring(1);
            }
        }

        bool GetURPFloat(string propertyName, out float value)
        {
            value = 0f;
            if (_urpAssetObj == null) return false;

            var type = _urpAssetObj.GetType();
            foreach (var candidate in GetCandidateMemberNames(propertyName))
            {
                var propInfo = FindPropertyRecursive(type, candidate);
                if (propInfo != null && propInfo.PropertyType == typeof(float) && propInfo.CanRead)
                {
                    value = (float)propInfo.GetValue(_urpAssetObj);
                    return true;
                }

                var fieldInfo = FindFieldRecursive(type, candidate);
                if (fieldInfo != null && fieldInfo.FieldType == typeof(float))
                {
                    value = (float)fieldInfo.GetValue(_urpAssetObj);
                    return true;
                }
            }

            return false;
        }

        bool SetURPFloat(string propertyName, float newValue, out float oldValue)
        {
            oldValue = 0f;
            if (_urpAssetObj == null) return false;

            var type = _urpAssetObj.GetType();
            foreach (var candidate in GetCandidateMemberNames(propertyName))
            {
                var propInfo = FindPropertyRecursive(type, candidate);
                if (propInfo != null && propInfo.PropertyType == typeof(float) && propInfo.CanRead && propInfo.CanWrite)
                {
                    oldValue = (float)propInfo.GetValue(_urpAssetObj);
                    propInfo.SetValue(_urpAssetObj, newValue);
                    return true;
                }

                var fieldInfo = FindFieldRecursive(type, candidate);
                if (fieldInfo != null && fieldInfo.FieldType == typeof(float))
                {
                    oldValue = (float)fieldInfo.GetValue(_urpAssetObj);
                    fieldInfo.SetValue(_urpAssetObj, newValue);
                    return true;
                }
            }

            return false;
        }

        bool SetURPInt(string propertyName, int newValue, out int oldValue)
        {
            oldValue = 0;
            if (_urpAssetObj == null) return false;

            var type = _urpAssetObj.GetType();
            foreach (var candidate in GetCandidateMemberNames(propertyName))
            {
                var propInfo = FindPropertyRecursive(type, candidate);
                if (propInfo != null && propInfo.CanRead && propInfo.CanWrite)
                {
                    if (propInfo.PropertyType == typeof(int))
                    {
                        oldValue = (int)propInfo.GetValue(_urpAssetObj);
                        propInfo.SetValue(_urpAssetObj, newValue);
                        return true;
                    }

                    if (propInfo.PropertyType.IsEnum)
                    {
                        oldValue = Convert.ToInt32(propInfo.GetValue(_urpAssetObj));
                        propInfo.SetValue(_urpAssetObj, Enum.ToObject(propInfo.PropertyType, newValue));
                        return true;
                    }
                }

                var fieldInfo = FindFieldRecursive(type, candidate);
                if (fieldInfo != null)
                {
                    if (fieldInfo.FieldType == typeof(int))
                    {
                        oldValue = (int)fieldInfo.GetValue(_urpAssetObj);
                        fieldInfo.SetValue(_urpAssetObj, newValue);
                        return true;
                    }

                    if (fieldInfo.FieldType.IsEnum)
                    {
                        oldValue = Convert.ToInt32(fieldInfo.GetValue(_urpAssetObj));
                        fieldInfo.SetValue(_urpAssetObj, Enum.ToObject(fieldInfo.FieldType, newValue));
                        return true;
                    }
                }
            }

            return false;
        }

        bool SetURPBool(string propertyName, bool newValue, out bool oldValue)
        {
            oldValue = false;
            if (_urpAssetObj == null) return false;

            var type = _urpAssetObj.GetType();
            foreach (var candidate in GetCandidateMemberNames(propertyName))
            {
                var propInfo = FindPropertyRecursive(type, candidate);
                if (propInfo != null && propInfo.PropertyType == typeof(bool) && propInfo.CanRead && propInfo.CanWrite)
                {
                    oldValue = (bool)propInfo.GetValue(_urpAssetObj);
                    propInfo.SetValue(_urpAssetObj, newValue);
                    return true;
                }

                var fieldInfo = FindFieldRecursive(type, candidate);
                if (fieldInfo != null && fieldInfo.FieldType == typeof(bool))
                {
                    oldValue = (bool)fieldInfo.GetValue(_urpAssetObj);
                    fieldInfo.SetValue(_urpAssetObj, newValue);
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Utility Methods

        object GetCurrentRenderPipelineAsset()
        {
            if (QualitySettings.renderPipeline != null)
                return QualitySettings.renderPipeline;

            object graphicsPipeline = GetStaticPropertyValue("UnityEngine.Rendering.GraphicsSettings, UnityEngine.CoreModule", "currentRenderPipeline")
                                   ?? GetStaticPropertyValue("UnityEngine.Rendering.GraphicsSettings, UnityEngine.CoreModule", "defaultRenderPipeline")
                                   ?? GetStaticPropertyValue("UnityEngine.Rendering.GraphicsSettings, UnityEngine.CoreModule", "renderPipelineAsset");
            if (graphicsPipeline != null)
                return graphicsPipeline;

#if UNITY_EDITOR
            return LoadEditorGraphicsSettingsPipelineAsset();
#else
            return null;
#endif
        }

        object GetStaticPropertyValue(string assemblyQualifiedTypeName, string propertyName)
        {
            var type = Type.GetType(assemblyQualifiedTypeName);
            if (type == null)
                return null;

            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (prop == null || !prop.CanRead)
                return null;

            return prop.GetValue(null, null);
        }

#if UNITY_EDITOR
        static object LoadEditorGraphicsSettingsPipelineAsset()
        {
            var settingsObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("ProjectSettings/GraphicsSettings.asset");
            if (settingsObj == null)
                return null;

            var so = new SerializedObject(settingsObj);
            var prop = so.FindProperty("m_CustomRenderPipeline");
            return prop?.objectReferenceValue;
        }
#endif

        MacGPUConfig CreateDefaultConfig()
        {
            Log("WARNING: No config asset specified, using built-in defaults");
            return ScriptableObject.CreateInstance<MacGPUConfig>();
        }

        void Log(string msg)
        {
            _lastLogMessage = $"[{Time.time:F1}s] {msg}";
            Debug.Log($"[MacGPUSafeGuard] {msg}");
        }

        public float GetAverageFrameTime()
        {
            if (_frameTimeHistory.Count == 0) return 0;
            float sum = 0;
            for (int i = 0; i < _frameTimeHistory.Count; i++)
                sum += _frameTimeHistory[i];
            return sum / _frameTimeHistory.Count;
        }

        #endregion

        #region Static Methods & Editor Menu

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void RegisterEditorPlayGuardOnLoad()
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
                return;

            // Clean up stale playmode flags from a previous kill where
            // EnteredEditMode never fired to clean up.
            var guardDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "MacGPUSafeGuard");
            try { System.IO.File.Delete(System.IO.Path.Combine(guardDir, "in_playmode")); } catch { }
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(guardDir, "playmode_state"), "editmode"); } catch { }

            EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnEditorPlayModeStateChanged;
        }

        private static void OnEditorPlayModeStateChanged(PlayModeStateChange change)
        {
            var flagPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "MacGPUSafeGuard", "in_playmode");
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    SessionState.SetBool(C_PLAY_GUARD_PENDING_KEY, true);
                    s_quarantinedClothInstanceIds.Clear();
                    UnregisterGuardedPlayHooks();
                    try { System.IO.File.WriteAllText(flagPath, "1"); } catch { }
                    StartHeartbeat();
                    Debug.Log("[MacGPUSafeGuard] macOS 下普通 Play 默认走保护路径。");
                    break;

                case PlayModeStateChange.EnteredEditMode:
                case PlayModeStateChange.ExitingPlayMode:
                    SessionState.SetBool(C_PLAY_GUARD_PENDING_KEY, false);
                    s_isGuardedPlay = false;
                    s_quarantinedClothInstanceIds.Clear();
                    UnregisterGuardedPlayHooks();
                    try { System.IO.File.Delete(flagPath); } catch { }
                    StopHeartbeat();
                    break;
            }
        }

        private static void StartHeartbeat()
        {
            if (s_heartbeatThread != null)
                return;
            s_heartbeatRunning = true;
            s_heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true };
            s_heartbeatThread.Start();
            Debug.Log("[MacGPUSafeGuard] Watchdog heartbeat started.");
        }

        private static void StopHeartbeat()
        {
            s_heartbeatRunning = false;
            if (s_heartbeatThread != null)
            {
                try { s_heartbeatThread.Join(500); } catch { }
                s_heartbeatThread = null;
            }
            try
            {
                if (File.Exists(C_HEARTBEAT_PATH))
                    File.Delete(C_HEARTBEAT_PATH);
                if (File.Exists(C_COMPILING_PATH))
                    File.Delete(C_COMPILING_PATH);
            }
            catch { }
            Debug.Log("[MacGPUSafeGuard] Watchdog heartbeat stopped.");
        }

        private static void HeartbeatLoop()
        {
            try { Directory.CreateDirectory(C_HEARTBEAT_DIR); } catch { }
            while (s_heartbeatRunning)
            {
                try
                {
                    if (ShaderUtil.anythingCompiling)
                        File.WriteAllText(C_COMPILING_PATH, "1");
                    else if (File.Exists(C_COMPILING_PATH))
                        File.Delete(C_COMPILING_PATH);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    File.WriteAllText(C_HEARTBEAT_PATH, ts.ToString());
                }
                catch { }
                Thread.Sleep(3000);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ConsumeGuardedPlayMark()
        {
            if (!Application.isPlaying)
                return;

            s_isGuardedPlay = Application.platform == RuntimePlatform.OSXEditor
                && SessionState.GetBool(C_PLAY_GUARD_PENDING_KEY, false);

            SessionState.SetBool(C_PLAY_GUARD_PENDING_KEY, false);
            s_quarantinedClothInstanceIds.Clear();
            UnregisterGuardedPlayHooks();

            if (s_isGuardedPlay)
            {
                RegisterGuardedPlayHooks();
                Debug.Log("[MacGPUSafeGuard] 保护路径已激活，开始安装更前置的 MagicaCloth 隔离钩子。");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ApplyGuardedPlayIsolation()
        {
            if (!Application.isPlaying || !s_isGuardedPlay)
                return;

            string sceneName = SceneManager.GetActiveScene().name;
            QuarantineMagicaClothForGuardedPlay($"AfterSceneLoad:{sceneName}");
        }

        private static void RegisterGuardedPlayHooks()
        {
            if (s_guardHooksInstalled)
                return;

            SceneManager.sceneLoaded -= OnGuardedPlaySceneLoaded;
            SceneManager.sceneLoaded += OnGuardedPlaySceneLoaded;
            MagicaManager.afterUpdateDelegate -= OnGuardedPlayAfterUpdate;
            MagicaManager.afterUpdateDelegate += OnGuardedPlayAfterUpdate;
            s_guardHooksInstalled = true;
        }

        private static void UnregisterGuardedPlayHooks()
        {
            if (!s_guardHooksInstalled)
                return;

            SceneManager.sceneLoaded -= OnGuardedPlaySceneLoaded;
            MagicaManager.afterUpdateDelegate -= OnGuardedPlayAfterUpdate;
            s_guardHooksInstalled = false;
        }

        private static void OnGuardedPlaySceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!s_isGuardedPlay)
                return;

            QuarantineMagicaClothForGuardedPlay($"sceneLoaded:{scene.name}:{mode}");
        }

        private static void OnGuardedPlayAfterUpdate()
        {
            if (!s_isGuardedPlay)
                return;

            QuarantineMagicaClothForGuardedPlay("MagicaAfterUpdate");
        }

        private static void QuarantineMagicaClothForGuardedPlay(string source)
        {
            MagicaCloth[] cloths = UnityEngine.Object.FindObjectsOfType<MagicaCloth>(true);
            int quarantinedCount = 0;

            foreach (MagicaCloth cloth in cloths)
            {
                if (!TryQuarantineMagicaCloth(cloth))
                    continue;

                quarantinedCount++;
            }

            if (quarantinedCount > 0)
            {
                Debug.Log($"[MacGPUSafeGuard] 已前置隔离 MagicaCloth。source={source}, total={cloths.Length}, quarantined={quarantinedCount}");
            }
        }

        private static bool TryQuarantineMagicaCloth(MagicaCloth cloth)
        {
            if (cloth == null)
                return false;

            if (!cloth.gameObject.scene.IsValid())
                return false;

            int instanceId = cloth.GetInstanceID();
            if (s_quarantinedClothInstanceIds.Contains(instanceId))
                return false;

            ClothProcess process = cloth.Process;
            process.isRegisterDestroy = true;
            MagicaManager.SyncCloth?.RegisterClothProcessDispose(process);

            if (cloth.enabled)
                cloth.enabled = false;

            s_quarantinedClothInstanceIds.Add(instanceId);
            return true;
        }
#endif

        public static void ApplyStaticConfig()
        {
            var gameObject = new GameObject("[MacGPUSafeGuard]");
            gameObject.AddComponent<MacGPUSafeGuard>();
            DontDestroyOnLoad(gameObject);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitializeOnMac()
        {
            bool isMac = Application.platform == RuntimePlatform.OSXEditor
                      || Application.platform == RuntimePlatform.OSXPlayer;
            if (!isMac)
                return;

            ApplyStaticConfig();
            Debug.Log("[MacGPUSafeGuard] Auto-initialized on macOS.");
        }

#if UNITY_EDITOR

        [MenuItem("Performance/Mac GPU SafeGuard/Apply All Settings %&g", false, 100)]
        static void EditorApplyAllSettings()
        {
            EditorApplyRetinaSetting();
            EditorApplyVSyncSetting();
            EditorApplyURPAssetSettings();
            EditorApplyCameraSettings();
            EditorApplyRendererFeatureBlacklist();

            EditorUtility.DisplayDialog("Mac GPU SafeGuard",
                "All Mac Metal safety settings applied!\n\n" +
                "- Retina Support: OFF\n" +
                "- VSync: ON\n" +
                "- RenderScale: 0.90\n" +
                "- Shadows: Balanced\n" +
                "- Opaque Downsampling: 1\n" +
                "- Anti-Aliasing: OFF (TAA disabled)\n" +
                "- MSAA: OFF\n" +
                "- HDR: OFF\n" +
                "\nPlease re-enter Play Mode to test.",
                "OK");
        }

        [MenuItem("Performance/Mac GPU SafeGuard/Create Config Asset", false, 200)]
        static void EditorCreateConfigAsset()
        {
            var cfg = ScriptableObject.CreateInstance<MacGPUConfig>();
            string path = "Assets/scripts/Performance/MacGPUConfig.asset";
            AssetDatabase.CreateAsset(cfg, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[MacGPUSafeGuard] Config asset created: {path}");
            EditorGUIUtility.PingObject(cfg);
        }

        static void EditorApplyRetinaSetting()
        {
            var settingsObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("ProjectSettings/ProjectSettings.asset");
            if (settingsObj == null) return;

            var so = new SerializedObject(settingsObj);
            var prop = so.FindProperty("macRetinaSupport");
            if (prop != null)
            {
                if (prop.intValue != 0)
                {
                    prop.intValue = 0;
                    so.ApplyModifiedProperties();
                    Debug.Log("[MacGPUSafeGuard] macRetinaSupport -> 0 (Editor)");
                }
                else
                {
                    Debug.Log("[MacGPUSafeGuard] macRetinaSupport already 0, skipped");
                }
            }
            else
            {
                Debug.LogWarning("[MacGPUSafeGuard] macRetinaSupport property not found (Unity version may differ)");
            }
        }

        static void EditorApplyVSyncSetting()
        {
            var qualityObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("ProjectSettings/QualitySettings.asset");
            if (qualityObj == null) return;

            var so = new SerializedObject(qualityObj);
            var prop = so.FindProperty("vSyncCount");
            if (prop != null && prop.intValue != 1)
            {
                prop.intValue = 1;
                so.ApplyModifiedProperties();
                Debug.Log("[MacGPUSafeGuard] QualitySettings.vSyncCount -> 1 (Editor)");
            }
        }

        static void EditorApplyURPAssetSettings()
        {
            var urp = QualitySettings.renderPipeline ?? LoadEditorGraphicsSettingsPipelineAsset();
            if (urp == null)
            {
                Debug.LogError("[MacGPUSafeGuard] Cannot resolve current Render Pipeline Asset from QualitySettings or GraphicsSettings.asset!");
                return;
            }

            var urpObj = (UnityEngine.Object)urp;
            var so = new SerializedObject(urpObj);

            EditorSetProp(so, "m_RenderScale", 0.9f);
            EditorSetProp(so, "m_MainLightShadowmapResolution", 2048);
            EditorSetProp(so, "m_ShadowDistance", 220f);
            EditorSetProp(so, "m_ShadowCascadeCount", 3);
            EditorSetProp(so, "m_SoftShadowQuality", 2);
            EditorSetProp(so, "m_AdditionalLightsShadowmapResolution", 1024);
            EditorSetProp(so, "m_OpaqueDownsampling", 1);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(urpObj);
            AssetDatabase.SaveAssets();

            Debug.Log("[MacGPUSafeGuard] URP Asset parameters updated (Editor)");
        }

        static void EditorSetProp(SerializedObject so, string propName, object value)
        {
            var prop = so.FindProperty(propName);
            if (prop == null)
            {
                Debug.LogWarning($"[MacGPUSafeGuard] Property '{propName}' not found in URP Asset");
                return;
            }

            switch (value)
            {
                case int iv: prop.intValue = iv; break;
                case float fv: prop.floatValue = fv; break;
                case bool bv: prop.boolValue = bv; break;
                default: return;
            }

            Debug.Log($"  {propName}: {value}");
        }

        static void EditorApplyCameraSettings()
        {
            // Apply default Mac-safe camera settings via SerializedObject on the camera prefab/scene
            // Since Camera.main might not exist in Editor, skip runtime camera modifications here.
            // The safeguard script will apply them on Play.
            Debug.Log("[MacGPUSafeGuard] Camera settings (TAA/MSAA/HDR) will be applied at runtime on Play.");
        }

        static void EditorApplyRendererFeatureBlacklist()
        {
            var urp = QualitySettings.renderPipeline ?? LoadEditorGraphicsSettingsPipelineAsset();
            if (urp == null)
            {
                Debug.LogWarning("[MacGPUSafeGuard] Cannot resolve Render Pipeline Asset for RendererFeature blacklist.");
                return;
            }

            var urpObj = (UnityEngine.Object)urp;
            var urpSo = new SerializedObject(urpObj);
            var rendererDataListProp = urpSo.FindProperty("m_RendererDataList");

            if (rendererDataListProp == null || !rendererDataListProp.isArray)
            {
                Debug.LogWarning("[MacGPUSafeGuard] m_RendererDataList not found on URP asset.");
                return;
            }

            string[] blacklist = {
                "ScreenSpaceGlobalIllumination",
                "ScreenSpaceReflection",
                "VolumetricClouds",
                "Volumetric Lighting",
                "HorizonBasedAmbientOcclusion",
                "Fur",
                "Ocean",
                "FastFourierTransform",
                "SubsurfaceScattering",
                "角色高精度阴影",
                "CloudShadow",
                "ParticleCloud",
                "GlobalVolumeCloud",
                "NepheleSky",
            };

            int totalDisabled = 0;

            for (int i = 0; i < rendererDataListProp.arraySize; i++)
            {
                var rendererDataRef = rendererDataListProp.GetArrayElementAtIndex(i);
                if (rendererDataRef == null || rendererDataRef.objectReferenceValue == null)
                    continue;

                var rdSo = new SerializedObject(rendererDataRef.objectReferenceValue);
                var featuresProp = rdSo.FindProperty("m_RendererFeatures");
                if (featuresProp == null || !featuresProp.isArray)
                    continue;

                for (int j = 0; j < featuresProp.arraySize; j++)
                {
                    var featureElem = featuresProp.GetArrayElementAtIndex(j);
                    if (featureElem == null || featureElem.objectReferenceValue == null)
                        continue;

                    var featureSo = new SerializedObject(featureElem.objectReferenceValue);
                    var nameProp = featureSo.FindProperty("m_Name");
                    string featureName = nameProp?.stringValue ?? "";

                    foreach (string pattern in blacklist)
                    {
                        if (!string.IsNullOrEmpty(featureName)
                            && featureName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var activeProp = featureSo.FindProperty("m_Active");
                            if (activeProp != null && activeProp.boolValue)
                            {
                                activeProp.boolValue = false;
                                featureSo.ApplyModifiedProperties();
                                EditorUtility.SetDirty(featureElem.objectReferenceValue);
                                Debug.Log($"[MacGPUSafeGuard] DISABLED RendererFeature: {featureName} (pattern: '{pattern}')");
                                totalDisabled++;
                            }
                            break;
                        }
                    }
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[MacGPUSafeGuard] RendererFeature blacklist applied: {totalDisabled} feature(s) disabled.");
        }

#endif

        #endregion
    }
}
