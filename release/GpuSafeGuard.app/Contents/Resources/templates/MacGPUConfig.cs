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
