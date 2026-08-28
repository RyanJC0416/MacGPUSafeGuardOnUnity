using UnityEngine;

namespace Performance.MacGPU
{
    /// <summary>
    /// Mac Metal GPU 安全配置
    ///
    /// 使用方式:
    ///   1. 在 Project 窗口右键 → Create → Performance → Mac GPU Config 创建配置 Asset
    ///   2. 将该 Asset 挂载到 MacGPUSafeGuard 组件的 config 字段
    ///   3. 参数可通过 Unity Inspector 编辑，无需改代码
    /// </summary>
    [CreateAssetMenu(fileName = "MacGPUConfig", menuName = "Performance/Mac GPU Config")]
    public class MacGPUConfig : ScriptableObject
    {
        [Header("VSync 控制")]
        [Tooltip("Mac 平台是否启用 VSync。启用后帧率限制在显示器刷新率，防止 GPU 跑满导致超时崩溃。")]
        public bool enableVSync = true;

        [Header("渲染比例")]
        [Tooltip("渲染分辨率缩放 (0.1~1.0)。默认改为 0.90，优先保住稳定性的同时尽量恢复清晰度。")]
        [Range(0.5f, 1.0f)]
        public float renderScale = 0.9f;

        [Header("阴影优化")]
        [Tooltip("主光源阴影贴图分辨率。原值 4096 对 Metal 负担过大。")]
        public int mainLightShadowResolution = 2048;

        [Tooltip("阴影最大距离（米）。默认回调到 220，在稳定性与远处阴影完整度之间取平衡。")]
        [Range(30f, 300f)]
        public float shadowDistance = 220f;

        [Tooltip("级联阴影数量。默认回调到 3，尽量恢复层次感，同时避免回到最重的 4 级联。")]
        [Range(1, 4)]
        public int shadowCascadeCount = 3;

        [Tooltip("软阴影质量 (0=禁用, 1=PCF软, 2=高质量, 3=最高)。默认回调到 2。")]
        [Range(0, 3)]
        public int softShadowQuality = 2;

        [Tooltip("附加光阴影贴图分辨率。")]
        public int additionalLightsShadowResolution = 1024;

        [Header("Opaque Texture")]
        [Tooltip("不透明纹理下采样率 (1=无下采样, 2=半分辨率, 4=1/4分辨率)。\n" +
                 "默认恢复到 1，先尽量保住依赖 _CameraOpaqueTexture 的屏幕效果质量。")]
        public int opaqueDownsampling = 1;

        [Header("SRP Batcher")]
        [Tooltip("是否启用 SRP Batcher。启用前必须在 Editor 中测试紫色材质问题！\n" +
                 "自定义 Shader 可能不兼容 SRP Batcher 的常量缓冲区规则。")]
        public bool enableSRPBatcher = false; // 默认关闭，需手动确认后开启

        [Header("相机抗锯齿 (Camera Anti-Aliasing)")]
        [Tooltip("Mac 平台抗锯齿模式。0=None, 1=FXAA, 2=SMAA, 3=TAA。\n" +
                 "TAA 在 Metal 上开销极大（约 15-25% GPU），默认禁用。FXAA 是轻量替代方案。")]
        [Range(0, 3)]
        public int antiAliasingMode = 0;

        [Tooltip("TAA 质量 (0=Low, 1=Medium, 2=High)。仅 antiAliasingMode=3 时生效。\n" +
                 "Mac 推荐 Low，减少抖动采样和锐化开销。")]
        [Range(0, 2)]
        public int taaQuality = 0;

        [Header("MSAA / HDR")]
        [Tooltip("Mac 平台是否启用 MSAA。TAA + MSAA 同时开启在 Metal 上有驱动兼容性问题，\n" +
                 "且会大幅增加帧缓冲内存。默认关闭。")]
        public bool allowMSAA = false;

        [Tooltip("Mac 平台是否启用 HDR。游戏角色打光和半透特效按 HDR+tonemap 制作。\n" +
                 "LDR 下 Lit 角色会变剪影、加法特效会裁成白片。Metal 带宽更高，若 GPU 超时再改回 false。")]
        public bool allowHDR = true;

        [Tooltip("allowHDR=false 且环境光接近纯黑时，补一档 LDR 环境光。\n" +
                 "Mac 关掉了 RealTimeSkyGI / HDR 自动曝光后，主光 0.4 + 黑环境光会把场景压黑。")]
        public bool fillBlackAmbientWhenHdrDisabled = true;

        [Tooltip("allowHDR=false 时关掉 LensFlare（镜头光晕鬼影）。\n" +
                 "不关 Bloom、不压主光/环境光；LDR 下光晕会画成天上那几个死白圆斑。")]
        public bool muteLensFlareWhenHdrDisabled = true;

        [Tooltip("allowHDR=false 时，只把 TCRender/Base 加法场景片改成 SrcAlpha+One。\n" +
                 "HDR 开启时不要用。改混合未能修复发白，默认关闭。")]
        public bool clampLiuguangVfxWhenHdrDisabled = false;

        [Header("监控与保护")]
        [Tooltip("启用 GPU 帧时间监控。当连续 N 帧超过阈值时自动降低画质。")]
        public bool enableFrameTimeMonitor = true;

        [Tooltip("单帧耗时警戒线（毫秒）。超过此值计入危险帧计数。")]
        public float frameTimeWarningThresholdMs = 33.3f; // ~30fps

        [Tooltip("触发自动降级的连续危险帧数。")]
        [Range(3, 30)]
        public int consecutiveDangerFramesToTrigger = 10;

        [Tooltip("触发自动降级后的冷却时间（秒），避免频繁切换。")]
        public float autoReduceCooldownSeconds = 30f;

        [Header("Play 模式重型 RendererFeature 屏蔽 (实验性)")]
        [Tooltip("是否在 Play 运行时按下面名单关闭匹配的 RendererFeature。" +
                 "本次测试版本默认开启；验证后若需保留为 opt-in，可改回 false。")]
        public bool disableHeavyRendererFeaturesInPlay = true;

        [Tooltip("要屏蔽的 RendererFeature 名称子串（不区分大小写，部分匹配）。" +
                 "Unity 迭代后新增/重命名 feature 时在此维护。")]
        public string[] heavyRendererFeaturePatterns = new string[]
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

        [Header("BigWorld 相机视锥体分块剔除 (实验性)")]
        [Tooltip("是否在 Play 运行时关闭 EcoEngine.BigWorld.Scene.m_bCheckBlockByCamera。" +
                 "关闭后大世界分块加载只以玩家位置为中心，不再随相机转动而加载/卸载分块，" +
                 "用于验证镜头转动时物件闪烁是否由视锥体分块剔除引起。")]
        public bool disableBigWorldCameraBlockCulling = true;

        [Tooltip("强制修改 EntityManager.m_CameraExtendedRange 的额外范围。" +
                 "0 表示不修改；正值会扩大相机视锥体剔除的包围范围。")]
        public float bigWorldCameraExtendedRange = 0f;

        [Header("Play 模式 Game 窗口 VG / Scene 相机 (实验性)")]
        [Tooltip("Play 时关掉 SceneView 相机。Virtual Geometry 的可见性缓冲是全局的，" +
                 "Scene 相机和 Game 相机抢同一份 GPU cull 结果会导致 Game 窗口随镜头转动闪烁。" +
                 "Scene 窗口本轮先不管。")]
        public bool disableSceneViewCamerasInPlay = true;

        [Tooltip("把上一轮误关的 Virtual Geometry / Impostor 等重新打开。" +
                 "sm_stone_37c_m 这类物件走 VG，没有普通 MeshRenderer。")]
        public bool restoreVirtualGeometryInPlay = true;
    }
}
