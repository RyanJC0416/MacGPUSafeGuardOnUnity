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
        private float _lastCameraForceLogTime = -999f;
        private readonly HashSet<int> _aaAppliedCameraIds = new HashSet<int>();
        private bool _rendererFeaturesDisabledInPlay;
        private bool _cullingSystemsDisabledInPlay;
        private bool _virtualGeometryRestoredInPlay;
        private bool _vgGpuVpFixInjected;
        private uint _frozenInstancingBoundsCode;
        private bool _instancingBoundsCodeFrozen;
        private bool _bigWorldCameraCullingDisabled;
        private object _bigWorldSceneObj;
        private FieldInfo _bigWorldCheckBlockField;
        private FieldInfo _bigWorldEntityManagerField;
        private FieldInfo _bigWorldCameraExtendedRangeField;
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
            MaintainGameCameraSafeguards();
            PatchBigWorldCameraCulling(config);
            DisableSceneViewCamerasInPlay(config);
            RestoreVirtualGeometryInPlay(config);
            InjectVgGpuVpFix();

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

        static bool IsGameCamera(Camera cam)
        {
            if (cam == null)
                return false;
            CameraType type = cam.cameraType;
            return type == CameraType.Game || type == CameraType.VR;
        }

        void MaintainGameCameraSafeguards()
        {
            if (config == null)
                return;

            Camera[] cameras = Camera.allCameras;
            int forced = 0;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                if (!IsGameCamera(cam))
                    continue;

                if (cam.allowHDR != config.allowHDR)
                {
                    cam.allowHDR = config.allowHDR;
                    forced++;
                }

                if (cam.allowMSAA != config.allowMSAA)
                {
                    cam.allowMSAA = config.allowMSAA;
                    forced++;
                }

                int id = cam.GetInstanceID();
                if (_aaAppliedCameraIds.Contains(id))
                    continue;

                try
                {
                    ApplyAntiAliasing(config, cam);
                }
                catch (Exception ex)
                {
                    Log($"AntiAliasing skipped on {cam.name}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _aaAppliedCameraIds.Add(id);
                }
            }

            if (forced > 0 && Time.unscaledTime - _lastCameraForceLogTime > 1f)
            {
                _lastCameraForceLogTime = Time.unscaledTime;
                Log($"Forced HDR/MSAA on Game cameras (allowHDR={config.allowHDR}, allowMSAA={config.allowMSAA}, touched={forced})");
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

                // Game cameras often spawn after Awake/Start (hot-update / BigWorld).
                // Apply to current Game cameras now; Update() retries as new cameras appear.
                ApplyCameraSettings(cfg);

                // Play-mode heavy RendererFeature blacklist (forced in this test build).
                // Temporarily ignoring config.disableHeavyRendererFeaturesInPlay so old config assets also run.
                if (_urpAssetObj != null && !_rendererFeaturesDisabledInPlay)
                {
                    DisableHeavyRendererFeaturesInPlay(cfg);
                }

                // Culling / occlusion test: disable them to see if rotation popping stops.
                if (_urpAssetObj != null && !_cullingSystemsDisabledInPlay)
                {
                    DisableCullingAndOcclusionInPlay();
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
            if (!_isMacPlatform || config == null || _configApplied == false)
                return;

            ApplyCameraSettings(config);

            if (_urpAssetObj != null && !_rendererFeaturesDisabledInPlay)
                DisableHeavyRendererFeaturesInPlay(config);

            if (_urpAssetObj != null && !_cullingSystemsDisabledInPlay)
                DisableCullingAndOcclusionInPlay();

            PatchBigWorldCameraCulling(config);
            DisableSceneViewCamerasInPlay(config);
            RestoreVirtualGeometryInPlay(config);
            InjectVgGpuVpFix();
        }

        void LateUpdate()
        {
            if (!_isMacPlatform || config == null)
                return;
            if (!Application.isPlaying)
                return;

            FreezeGpuInstancingFrustumOnRotate();
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
            Camera[] cameras = Camera.allCameras;
            int gameCount = 0;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (IsGameCamera(cameras[i]))
                    gameCount++;
            }

            if (gameCount == 0)
            {
                Log("--- Camera Settings --- skipped (no Game cameras yet; Update will retry)");
                return;
            }

            Log($"--- Camera Settings --- applying to {gameCount} Game camera(s)");
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                if (!IsGameCamera(cam))
                    continue;

                try
                {
                    ApplyAntiAliasing(cfg, cam);
                    ApplyMSAA(cfg, cam);
                    ApplyHDR(cfg, cam);
                }
                catch (Exception ex)
                {
                    Log($"Camera settings skipped on {cam.name}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _aaAppliedCameraIds.Add(cam.GetInstanceID());
                }
            }
        }

        void ApplyAntiAliasing(MacGPUConfig cfg, Camera cam)
        {
            if (cam == null) return;

            object cameraData = GetUniversalAdditionalCameraData(cam);
            if (cameraData == null)
            {
                Log($"WARNING: Could not find UniversalAdditionalCameraData on {cam.name}");
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

            if (aaProp != null && aaProp.CanWrite && !IsByRefProperty(aaProp))
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
                Log($"AntiAliasing [{cam.name}]: {oldVal} -> {newVal} ({cfg.antiAliasingMode})");
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

            if (aaqProp != null && aaqProp.CanWrite && !IsByRefProperty(aaqProp))
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

            // taaSettings is often a ref-returning property on URP camera data.
            // PropertyInfo.GetValue throws NotSupportedException (ByRef) on Mono.
            foreach (var taaName in new[] { "m_TaaSettings", "taaSettings" })
            {
                FieldInfo taaField = FindFieldRecursive(dataType, taaName);
                if (taaField == null)
                    continue;

                object taaSettings = taaField.GetValue(cameraData);
                if (taaSettings == null)
                    continue;

                Type taaType = taaSettings.GetType();
                FieldInfo qField = FindFieldRecursive(taaType, "quality")
                                   ?? FindFieldRecursive(taaType, "m_Quality");
                if (qField == null)
                    break;

                object oldQ = qField.GetValue(taaSettings);
                object newQ = qField.FieldType.IsEnum
                    ? Enum.ToObject(qField.FieldType, cfg.taaQuality)
                    : (object)cfg.taaQuality;
                qField.SetValue(taaSettings, newQ);
                Log($"TAA Quality [{cam.name}]: {oldQ} -> {newQ}");
                break;
            }
        }

        void ApplyMSAA(MacGPUConfig cfg, Camera cam)
        {
            if (cam == null) return;

            if (cam.allowMSAA != cfg.allowMSAA)
            {
                cam.allowMSAA = cfg.allowMSAA;
                Log($"allowMSAA [{cam.name}]: {!cfg.allowMSAA} -> {cfg.allowMSAA}");
            }
        }

        void ApplyHDR(MacGPUConfig cfg, Camera cam)
        {
            if (cam == null) return;

            if (cam.allowHDR != cfg.allowHDR)
            {
                cam.allowHDR = cfg.allowHDR;
                Log($"allowHDR [{cam.name}]: {!cfg.allowHDR} -> {cfg.allowHDR}");
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

        #region RendererFeature Blacklist (Play Mode)

        /// <summary>
        /// Play-mode opt-in: disable heavy RendererFeatures by name substring.
        /// Only runs on Mac when config.disableHeavyRendererFeaturesInPlay is true.
        /// Changes are in-memory only; restart to revert.
        /// </summary>
        void DisableHeavyRendererFeaturesInPlay(MacGPUConfig cfg)
        {
            if (_rendererFeaturesDisabledInPlay)
                return;

            if (!cfg.disableHeavyRendererFeaturesInPlay)
                Log("[Play Blacklist] Config flag is disabled; forcing test run.");

            var patterns = cfg.heavyRendererFeaturePatterns;
            if (patterns == null || patterns.Length == 0)
            {
                Log("[Play Blacklist] No patterns configured; using built-in fallback list.");
                patterns = GetDefaultHeavyRendererFeaturePatterns();
            }

            try
            {
                int disabledCount = 0;
                int inspectedCount = 0;

                object[] rendererDataList = GetRendererDataList();
                if (rendererDataList == null || rendererDataList.Length == 0)
                {
                    Log("WARNING: Could not resolve renderer data list for Play-mode feature blacklist.");
                    return;
                }

                foreach (var rendererData in rendererDataList)
                {
                    if (rendererData == null)
                        continue;

                    object[] features = GetRendererFeatures(rendererData);
                    if (features == null)
                        continue;

                    foreach (var feature in features)
                    {
                        if (feature == null)
                            continue;

                        string featureName = GetFeatureName(feature);
                        inspectedCount++;
                        if (string.IsNullOrEmpty(featureName))
                            continue;

                        foreach (string pattern in patterns)
                        {
                            if (string.IsNullOrEmpty(pattern))
                                continue;

                            if (featureName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (SetFeatureActive(feature, false))
                                {
                                    Log($"[Play Blacklist] DISABLED: {featureName} (matched '{pattern}')");
                                    disabledCount++;
                                }
                                break;
                            }
                        }
                    }
                }

                _rendererFeaturesDisabledInPlay = true;
                Log($"[Play Blacklist] Inspected {inspectedCount} feature(s), disabled {disabledCount}. Restart to revert.");
            }
            catch (Exception ex)
            {
                Log($"ERROR disabling heavy RendererFeatures in Play: {ex.Message}\n{ex.StackTrace}");
            }
        }

        static string[] GetDefaultHeavyRendererFeaturePatterns()
        {
            return new string[]
            {
                "[TA]Volumetric Lighting",
                "[TA]ScreenSpaceReflection",
                "[TA]ScreenSpaceGlobalIllumination",
                "GTAO",
                "[TA]Cloud Shadow",
                "[Engine]Ocean",
                "EcoEngine.Rendering.CodeBridge.FurRendererFeature",
                "HeightMapFogFeature",
                "[TA]SeparableSSS",
                "EcoEngine.Rendering.CodeBridge.HighQualityDepthOfFieldRendererFeature",
                "EcoEngine.Rendering.CodeBridge.ContactShadowsRenderFeature",
                "[TA]RealTimeSkyGI",
                "EcoEngine.Rendering.CodeBridge.NepheleSkyRendererFeature",
                "[TA]DebugScreenHSV",
                "[Engine]Screenshot Effect",
                "EcoEngine.Rendering.CodeBridge.CloudShadowRendererFeature",
                "EcoEngine.Rendering.CodeBridge.ParticleCloudRendererFeature",
                "GlobalVolumeCloud",
                "VolumetricClouds",
                "HorizonBasedAmbientOcclusion",
                "SubsurfaceScattering",
                "角色高精度阴影",
                "FastFourierTransform",
            };
        }

        object[] GetRendererDataList()
        {
            if (_urpAssetObj == null)
                return null;

            var urpType = _urpAssetObj.GetType();
            foreach (string name in new[] { "rendererDataList", "m_RendererDataList" })
            {
                var prop = FindPropertyRecursive(urpType, name);
                if (prop != null && prop.CanRead)
                {
                    var value = prop.GetValue(_urpAssetObj);
                    return ConvertToObjectArray(value);
                }

                var field = FindFieldRecursive(urpType, name);
                if (field != null)
                {
                    var value = field.GetValue(_urpAssetObj);
                    return ConvertToObjectArray(value);
                }
            }

            return null;
        }

        object[] GetRendererFeatures(object rendererData)
        {
            if (rendererData == null)
                return null;

            var type = rendererData.GetType();
            foreach (string name in new[] { "rendererFeatures", "m_RendererFeatures" })
            {
                var prop = FindPropertyRecursive(type, name);
                if (prop != null && prop.CanRead)
                {
                    var value = prop.GetValue(rendererData);
                    return ConvertToObjectArray(value);
                }

                var field = FindFieldRecursive(type, name);
                if (field != null)
                {
                    var value = field.GetValue(rendererData);
                    return ConvertToObjectArray(value);
                }
            }

            return null;
        }

        static object[] ConvertToObjectArray(object value)
        {
            if (value == null)
                return null;

            if (value is object[] arr)
                return arr;

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                var list = new List<object>();
                foreach (var item in enumerable)
                    list.Add(item);
                return list.ToArray();
            }

            return new[] { value };
        }

        static string GetFeatureName(object feature)
        {
            if (feature == null)
                return null;

            var type = feature.GetType();

            foreach (string name in new[] { "name", "m_Name" })
            {
                var prop = FindPropertyRecursiveStatic(type, name);
                if (prop != null && prop.CanRead)
                    return prop.GetValue(feature)?.ToString();

                var field = FindFieldRecursiveStatic(type, name);
                if (field != null)
                    return field.GetValue(feature)?.ToString();
            }

            return type.Name;
        }

        static bool SetFeatureActive(object feature, bool active)
        {
            if (feature == null)
                return false;

            var type = feature.GetType();
            foreach (string name in new[] { "isActive", "active", "m_Active" })
            {
                var prop = FindPropertyRecursiveStatic(type, name);
                if (prop != null && prop.CanRead && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    bool old = (bool)prop.GetValue(feature);
                    if (old != active)
                    {
                        prop.SetValue(feature, active);
                        return true;
                    }
                    return false;
                }

                var field = FindFieldRecursiveStatic(type, name);
                if (field != null && field.FieldType == typeof(bool))
                {
                    bool old = (bool)field.GetValue(feature);
                    if (old != active)
                    {
                        field.SetValue(feature, active);
                        return true;
                    }
                    return false;
                }
            }

            return false;
        }

        static PropertyInfo FindPropertyRecursiveStatic(Type type, string memberName)
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

        static FieldInfo FindFieldRecursiveStatic(Type type, string memberName)
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

        #endregion

        #region Culling / Occlusion Test (Play Mode)

        /// <summary>
        /// Play-mode test: disable Unity Occlusion Culling and culling-related RendererFeatures
        /// to see if rotation popping is caused by a culling system mismatch on Mac Metal.
        /// In-memory only; restart to revert.
        /// </summary>
        void DisableCullingAndOcclusionInPlay()
        {
            if (_cullingSystemsDisabledInPlay)
                return;

            try
            {
                // Disable global occlusion culling via reflection (avoids compile-time dependency on some Unity versions/assemblies)
                bool occlusionWasEnabled = GetGlobalOcclusionCullingEnabled();
                SetGlobalOcclusionCullingEnabled(false);
                Log($"[Culling Test] OcclusionCulling.enabled: {occlusionWasEnabled} -> false");

                // Also disable per-camera occlusion culling
                Camera[] cameras = Camera.allCameras;
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera cam = cameras[i];
                    if (cam == null || !IsGameCamera(cam))
                        continue;
                    if (cam.useOcclusionCulling)
                    {
                        cam.useOcclusionCulling = false;
                        Log($"[Culling Test] Camera {cam.name} useOcclusionCulling -> false");
                    }
                }

                // Only disable GPU HiZ occlusion. Virtual Geometry / Impostor must stay on:
                // sm_stone_37c_m is VG (no MeshRenderer), turning VG off made Game view pop.
                string[] cullingPatterns = new string[]
                {
                    "[Engine]HizCulling",
                };

                int disabledCount = 0;
                int inspectedCount = 0;

                object[] rendererDataList = GetRendererDataList();
                if (rendererDataList != null)
                {
                    foreach (var rendererData in rendererDataList)
                    {
                        if (rendererData == null)
                            continue;

                        object[] features = GetRendererFeatures(rendererData);
                        if (features == null)
                            continue;

                        foreach (var feature in features)
                        {
                            if (feature == null)
                                continue;

                            string featureName = GetFeatureName(feature);
                            inspectedCount++;
                            if (string.IsNullOrEmpty(featureName))
                                continue;

                            foreach (string pattern in cullingPatterns)
                            {
                                if (string.IsNullOrEmpty(pattern))
                                    continue;

                                if (featureName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (SetFeatureActive(feature, false))
                                    {
                                        Log($"[Culling Test] DISABLED: {featureName} (matched '{pattern}')");
                                        disabledCount++;
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

                _cullingSystemsDisabledInPlay = true;
                Log($"[Culling Test] Inspected {inspectedCount} feature(s), disabled {disabledCount}. Restart to revert.");
            }
            catch (Exception ex)
            {
                Log($"ERROR disabling culling/occlusion systems: {ex.Message}\n{ex.StackTrace}");
            }
        }

        static bool GetGlobalOcclusionCullingEnabled()
        {
            try
            {
                Type t = FindOcclusionCullingType();
                if (t == null)
                    return false;

                PropertyInfo p = t.GetProperty("enabled", BindingFlags.Public | BindingFlags.Static);
                if (p != null && p.CanRead)
                    return (bool)p.GetValue(null);

                FieldInfo f = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (f != null)
                    return (bool)f.GetValue(null);
            }
            catch { }
            return false;
        }

        static void SetGlobalOcclusionCullingEnabled(bool value)
        {
            try
            {
                Type t = FindOcclusionCullingType();
                if (t == null)
                    return;

                PropertyInfo p = t.GetProperty("enabled", BindingFlags.Public | BindingFlags.Static);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(null, value);
                    return;
                }

                FieldInfo f = t.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (f != null)
                    f.SetValue(null, value);
            }
            catch { }
        }

        static Type FindOcclusionCullingType()
        {
            Type t = Type.GetType("UnityEngine.OcclusionCulling, UnityEngine.CoreModule");
            if (t != null)
                return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType("UnityEngine.OcclusionCulling");
                if (t != null)
                    return t;
            }

            return null;
        }

        #endregion

        #region BigWorld Camera Block Culling Test (Play Mode)

        /// <summary>
        /// Play-mode test: disable EcoEngine.BigWorld.Scene.m_bCheckBlockByCamera.
        /// The BigWorld streaming system uses the camera frustum to load/unload scene blocks,
        /// which appears to cause objects to pop in/out when rotating the camera after the
        /// Unity/URP version iteration. Setting this flag to false makes block loading only
        /// depend on player position, eliminating rotation-dependent popping.
        /// In-memory only; restart to revert.
        /// </summary>
        void PatchBigWorldCameraCulling(MacGPUConfig cfg)
        {
            if (cfg == null || !cfg.disableBigWorldCameraBlockCulling)
                return;

            try
            {
                // The EcoEngine.Runtime assembly is loaded by the custom URP package.
                if (_bigWorldSceneObj == null)
                {
                    Assembly ecoEngineAssembly = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "EcoEngine.Runtime")
                        {
                            ecoEngineAssembly = asm;
                            break;
                        }
                    }

                    if (ecoEngineAssembly == null)
                    {
                        // Not loaded yet; retry next frame from Update().
                        return;
                    }

                    Type sceneManagerType = ecoEngineAssembly.GetType("EcoEngine.BigWorld.SceneManager");
                    if (sceneManagerType == null)
                    {
                        Log("[BigWorld Culling] EcoEngine.BigWorld.SceneManager type not found.");
                        return;
                    }

                    MethodInfo getActiveScene = sceneManagerType.GetMethod("GetActiveScene",
                        BindingFlags.Public | BindingFlags.Static);
                    if (getActiveScene == null)
                    {
                        Log("[BigWorld Culling] SceneManager.GetActiveScene() method not found.");
                        return;
                    }

                    _bigWorldSceneObj = getActiveScene.Invoke(null, null);
                    if (_bigWorldSceneObj == null)
                    {
                        // Active scene may not be created yet; retry next frame.
                        return;
                    }

                    Type sceneType = ecoEngineAssembly.GetType("EcoEngine.BigWorld.Scene");
                    _bigWorldCheckBlockField = sceneType?.GetField("m_bCheckBlockByCamera",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    _bigWorldEntityManagerField = sceneType?.GetField("m_EntityManager",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    Type entityManagerType = ecoEngineAssembly.GetType("EcoEngine.BigWorld.EntityManager");
                    if (entityManagerType != null)
                    {
                        _bigWorldCameraExtendedRangeField = entityManagerType.GetField("m_CameraExtendedRange",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }

                if (_bigWorldSceneObj == null)
                    return;

                if (_bigWorldCheckBlockField != null)
                {
                    bool oldValue = (bool)_bigWorldCheckBlockField.GetValue(_bigWorldSceneObj);
                    if (oldValue)
                    {
                        _bigWorldCheckBlockField.SetValue(_bigWorldSceneObj, false);
                        Log("[BigWorld Culling] Set Scene.m_bCheckBlockByCamera: True -> False");
                    }
                    else if (!_bigWorldCameraCullingDisabled)
                    {
                        Log("[BigWorld Culling] Scene.m_bCheckBlockByCamera already False");
                    }
                }

                if (_bigWorldCameraExtendedRangeField != null && cfg.bigWorldCameraExtendedRange > 0f)
                {
                    object entityManager = _bigWorldEntityManagerField?.GetValue(_bigWorldSceneObj);
                    if (entityManager != null)
                    {
                        float oldRange = (float)_bigWorldCameraExtendedRangeField.GetValue(entityManager);
                        if (Mathf.Abs(oldRange - cfg.bigWorldCameraExtendedRange) > 0.001f)
                        {
                            _bigWorldCameraExtendedRangeField.SetValue(entityManager, cfg.bigWorldCameraExtendedRange);
                            Log($"[BigWorld Culling] EntityManager.m_CameraExtendedRange: {oldRange} -> {cfg.bigWorldCameraExtendedRange}");
                        }
                    }
                }

                _bigWorldCameraCullingDisabled = true;
            }
            catch (Exception ex)
            {
                Log($"ERROR patching BigWorld camera culling: {ex.Message}\n{ex.StackTrace}");
            }
        }

        #endregion

        #region Game View VG / Scene Camera Isolation (Play Mode)

        /// <summary>
        /// sm_stone_37c_m is Virtual Geometry, not a MeshRenderer. The previous culling
        /// test turned VG off. Restore it so Game view can draw those instances.
        /// In-memory only; restart to revert.
        /// </summary>
        void RestoreVirtualGeometryInPlay(MacGPUConfig cfg)
        {
            if (cfg == null || !cfg.restoreVirtualGeometryInPlay)
                return;
            if (_virtualGeometryRestoredInPlay || _urpAssetObj == null)
                return;

            string[] restorePatterns = new string[]
            {
                "[Engine] Virtual Geometry",
                "[Engine]Impostor",
                "[Engine]TerrainVT",
                "[Game]场景分层渲染",
                "[Engine]ShadowCache",
            };

            try
            {
                int restored = 0;
                object[] rendererDataList = GetRendererDataList();
                if (rendererDataList != null)
                {
                    foreach (var rendererData in rendererDataList)
                    {
                        if (rendererData == null)
                            continue;

                        object[] features = GetRendererFeatures(rendererData);
                        if (features == null)
                            continue;

                        foreach (var feature in features)
                        {
                            if (feature == null)
                                continue;

                            string featureName = GetFeatureName(feature);
                            if (string.IsNullOrEmpty(featureName))
                                continue;

                            foreach (string pattern in restorePatterns)
                            {
                                if (featureName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                if (SetFeatureActive(feature, true))
                                {
                                    Log($"[VG Restore] ENABLED: {featureName}");
                                    restored++;
                                }
                                break;
                            }
                        }
                    }
                }

                Type vgType = Type.GetType("EcoEngine.Rendering.Universal.VirtualGeometryRenderFeature, EcoEngine.Runtime");
                if (vgType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "EcoEngine.Runtime")
                            continue;
                        vgType = asm.GetType("EcoEngine.Rendering.Universal.VirtualGeometryRenderFeature");
                        break;
                    }
                }

                if (vgType != null)
                {
                    FieldInfo configOff = vgType.GetField("ConfigOff", BindingFlags.Public | BindingFlags.Static);
                    if (configOff != null && configOff.FieldType == typeof(bool) && (bool)configOff.GetValue(null))
                    {
                        configOff.SetValue(null, false);
                        Log("[VG Restore] VirtualGeometryRenderFeature.ConfigOff -> false");
                    }

                    FieldInfo enabledField = vgType.GetField("m_Enabled", BindingFlags.NonPublic | BindingFlags.Static);
                    if (enabledField != null && enabledField.FieldType == typeof(bool))
                    {
                        bool old = (bool)enabledField.GetValue(null);
                        if (!old)
                        {
                            enabledField.SetValue(null, true);
                            Log("[VG Restore] VirtualGeometryRenderFeature.m_Enabled: False -> True");
                        }
                    }
                }

                _virtualGeometryRestoredInPlay = true;
                Log($"[VG Restore] Restored {restored} RendererFeature(s). Keep HizCulling off.");
            }
            catch (Exception ex)
            {
                Log($"ERROR restoring Virtual Geometry: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// VG visibility is stored in global GPU buffers. SceneView cameras also run
        /// VGVisibilityPass and overwrite Game camera results, which looks like
        /// objects popping while rotating the Game view. Disable Scene cameras in Play.
        /// Scene window is already unusable this round; Game view is the target.
        /// </summary>
        void DisableSceneViewCamerasInPlay(MacGPUConfig cfg)
        {
            if (!Application.isPlaying)
                return;

#if UNITY_EDITOR
            try
            {
                var sceneViews = UnityEditor.SceneView.sceneViews;
                if (sceneViews == null)
                    return;

                for (int i = 0; i < sceneViews.Count; i++)
                {
                    var sv = sceneViews[i] as UnityEditor.SceneView;
                    if (sv == null)
                        continue;

                    Camera cam = sv.camera;
                    if (cam != null && cam.enabled)
                    {
                        cam.enabled = false;
                        Log($"[Game View] Disabled SceneView camera '{cam.name}' in Play (VG global cull buffer).");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR disabling SceneView cameras: {ex.Message}");
            }
#endif
        }

        void InjectVgGpuVpFix()
        {
            if (_vgGpuVpFixInjected)
                return;
            if (!Application.isPlaying)
                return;

            try
            {
                Camera[] cameras = Camera.allCameras;
                bool injectedAny = false;
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera cam = cameras[i];
                    if (cam == null || !IsGameCamera(cam))
                        continue;

                    object additional = FindUniversalAdditionalCameraData(cam);
                    if (additional == null)
                        continue;

                    PropertyInfo rendererProp = additional.GetType().GetProperty("scriptableRenderer",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (rendererProp == null)
                        continue;

                    object renderer = rendererProp.GetValue(additional);
                    if (renderer == null)
                        continue;

                    FieldInfo listField = FindFieldRecursive(renderer.GetType(), "m_RendererFeatures");
                    if (listField == null)
                        continue;

                    var list = listField.GetValue(renderer) as System.Collections.IList;
                    if (list == null)
                        continue;

                    bool already = false;
                    for (int f = 0; f < list.Count; f++)
                    {
                        if (list[f] != null && list[f].GetType() == typeof(MacVgGpuVpFixFeature))
                        {
                            already = true;
                            break;
                        }
                    }

                    if (!already)
                    {
                        var feature = ScriptableObject.CreateInstance<MacVgGpuVpFixFeature>();
                        feature.Create();
                        list.Add(feature);
                        Log("[VG VP Fix] Injected MacVgGpuVpFixFeature into live renderer.");
                    }
                    injectedAny = true;
                }

                if (injectedAny)
                    _vgGpuVpFixInjected = true;
            }
            catch (Exception ex)
            {
                Log($"ERROR injecting VG GPU VP fix: {ex.Message}\n{ex.StackTrace}");
            }
        }

        object FindUniversalAdditionalCameraData(Camera cam)
        {
            if (cam == null)
                return null;
            Component[] comps = cam.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] == null)
                    continue;
                string tn = comps[i].GetType().Name;
                if (tn.IndexOf("UniversalAdditionalCameraData", StringComparison.Ordinal) >= 0)
                    return comps[i];
            }
            return null;
        }

        void FreezeGpuInstancingFrustumOnRotate()
        {
            try
            {
                Type mgrType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    mgrType = asm.GetType("EcoEngine.BigWorld.GeometryInstancingManager")
                              ?? asm.GetType("EcoEngine.Rendering.GeometryInstancingManager");
                    if (mgrType != null)
                        break;
                }
                if (mgrType == null)
                    return;

                PropertyInfo instProp = mgrType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object mgr = instProp != null ? instProp.GetValue(null) : null;
                if (mgr == null)
                    return;

                Camera gameCam = null;
                Camera[] cameras = Camera.allCameras;
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] != null && IsGameCamera(cameras[i]))
                    {
                        gameCam = cameras[i];
                        break;
                    }
                }

                // Pin last-seen transform so GeometryInstancingManager.Update does not
                // bump BoundsCheckCode (and recull) when the Game camera rotates.
                if (gameCam != null)
                {
                    FieldInfo posField = mgrType.GetField("m_position", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo rotField = mgrType.GetField("m_rotate", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (posField != null)
                        posField.SetValue(mgr, gameCam.transform.position);
                    if (rotField != null)
                        rotField.SetValue(mgr, gameCam.transform.eulerAngles);
                }

                FieldInfo codeField = mgrType.GetField("BoundsCheckCode", BindingFlags.Public | BindingFlags.Instance);
                if (codeField == null || codeField.FieldType != typeof(uint))
                    return;

                uint code = (uint)codeField.GetValue(mgr);
                if (!_instancingBoundsCodeFrozen)
                {
                    if (code == 0)
                        return;
                    _frozenInstancingBoundsCode = code;
                    _instancingBoundsCodeFrozen = true;
                    Log($"[Instancing Freeze] Freeze BoundsCheckCode at {code} so camera rotation does not recull GPU instances.");
                    return;
                }

                if (code != _frozenInstancingBoundsCode)
                    codeField.SetValue(mgr, _frozenInstancingBoundsCode);
            }
            catch (Exception ex)
            {
                Log($"ERROR freezing instancing frustum: {ex.Message}");
            }
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

        static bool IsByRefProperty(PropertyInfo prop)
        {
            if (prop == null)
                return true;
            if (prop.PropertyType.IsByRef)
                return true;
            MethodInfo getter = prop.GetGetMethod(true);
            return getter != null && getter.ReturnType.IsByRef;
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

            // Disable heavy RendererFeatures at Editor startup so SceneView
            // and GameView don't overwhelm the GPU with SSGI/SSR/VolumetricClouds/etc.
            // In-memory only (no SaveAssets) — changes are reversed on Editor restart.
            AutoDisableHeavyRendererFeatures();

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

        // Editor startup + Play hooks live in RegisterEditorPlayGuardOnLoad (no menus).
        // PlayMode URP/camera safety uses runtime clones in ApplySafeConfig — do not SaveAssets on urp.asset.

        // Called automatically on Editor startup. Uses direct asset path loading
        // (bypasses URP asset GUID references) and applies in-memory only — no
        // SaveAssets, so changes are reversed on Editor restart.
        static void AutoDisableHeavyRendererFeatures()
        {
            try
            {
                string[] blacklist = {
                    "ScreenSpaceGlobalIllumination", "ScreenSpaceReflection",
                    "VolumetricClouds", "Volumetric Lighting",
                    "HorizonBasedAmbientOcclusion", "Fur", "Ocean",
                    "FastFourierTransform", "SubsurfaceScattering",
                    "角色高精度阴影", "CloudShadow", "ParticleCloud",
                    "GlobalVolumeCloud", "NepheleSky",
                };

                string[] rendererDataPaths = {
                    "Assets/Settings/urp_renderer.asset",
                    "Assets/Settings/urp_role_renderer.asset",
                    "Assets/Settings/urp_ui_renderer.asset",
                    "Assets/Settings/urp_renderer_for_ui_scene.asset",
                };

                int disabledCount = 0;
                foreach (string path in rendererDataPaths)
                {
                    var rdAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (rdAsset == null) continue;

                    var rdSo = new SerializedObject(rdAsset);
                    var featuresProp = rdSo.FindProperty("m_RendererFeatures");
                    if (featuresProp == null || !featuresProp.isArray) continue;

                    for (int j = 0; j < featuresProp.arraySize; j++)
                    {
                        var fe = featuresProp.GetArrayElementAtIndex(j);
                        if (fe == null || fe.objectReferenceValue == null) continue;

                        var fs = new SerializedObject(fe.objectReferenceValue);
                        string fn = fs.FindProperty("m_Name")?.stringValue ?? "";
                        if (string.IsNullOrEmpty(fn)) continue;

                        foreach (string pattern in blacklist)
                        {
                            if (fn.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var ap = fs.FindProperty("m_Active");
                                if (ap != null && ap.boolValue)
                                {
                                    ap.boolValue = false;
                                    fs.ApplyModifiedProperties();
                                    disabledCount++;
                                }
                                break;
                            }
                        }
                    }
                }

                Debug.Log($"[MacGPUSafeGuard] Auto-disabled {disabledCount} heavy RendererFeature(s) for macOS.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MacGPUSafeGuard] AutoDisableHeavyRendererFeatures failed: {ex.Message}");
            }
        }

#endif

        #endregion
    }
}
