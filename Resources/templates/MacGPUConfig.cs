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

        [Tooltip("Mac 平台是否启用 HDR。HDR 渲染使颜色缓冲变为 RGBA16F，带宽翻倍。\n" +
                 "Mac Metal 上根据实际项目需求谨慎开启。")]
        public bool allowHDR = false;

        [Header("重型渲染特效屏蔽 (Heavy Renderer Feature Blacklist)")]
        [Tooltip("Mac 平台自动禁用的 RendererFeature（名称部分匹配，不区分大小写）。\n" +
                 "默认屏蔽项目中最重的 Metal GPU 负担项：\n" +
                 "  SSGI(屏幕空间全局光照) ≈20-30% GPU\n" +
                 "  SSR(屏幕空间反射) ≈10-15% GPU\n" +
                 "  体积云系统 ≈15-25% GPU\n" +
                 "  体积光照 ≈10-15% GPU\n" +
                 "  HBAO(水平基准环境遮蔽) ≈5-10% GPU\n" +
                 "  海洋+FFT ≈10-15% GPU\n" +
                 "  毛发渲染 ≈5-10% GPU\n" +
                 "  次表面散射(SSS) ≈3-5% GPU\n" +
                 "  高精度阴影 ≈2-5% GPU")]
        public string[] disabledRendererFeatures = new string[] {
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
    }
}
