using UnityEngine;
using UnityEngine.Rendering;
using EcoEngine.Rendering.Universal;

namespace Performance.MacGPU
{
    /// <summary>
    /// Hypothesis: EcoEngine VGVisibilityPass uses CPU projection for CameraType.Game
    /// and GPU projection otherwise. Metal compute frustum/HiZ then pops VG meshes
    /// (e.g. sm_stone_37c_m) while rotating Game view.
    ///
    /// Fix: for Mac Metal Play Game cameras, force CameraData.cameraType off Game
    /// around VGVisibilityPass, then recull with GetGPUProjectionMatrix after a
    /// draw-args reset so kernel 3 cannot double-count.
    /// </summary>
    public sealed class MacVgGpuVpFixFeature : ScriptableRendererFeature
    {
        sealed class ForceGpuVpCameraTypePass : ScriptableRenderPass
        {
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CameraData cameraData = renderingData.cameraData;
                if (cameraData == null || cameraData.cameraType != CameraType.Game)
                    return;
                cameraData.cameraType = CameraType.Preview;
            }
        }

        sealed class RestoreGameCameraTypePass : ScriptableRenderPass
        {
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CameraData cameraData = renderingData.cameraData;
                if (cameraData == null)
                    return;
                if (cameraData.camera != null && cameraData.camera.cameraType == CameraType.Game)
                    cameraData.cameraType = CameraType.Game;
            }
        }

        sealed class RecullPass : ScriptableRenderPass
        {
            public VirtualGeometryRenderFeature.Settings Settings;

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (Settings == null || Settings.computePassesShader == null)
                    return;

                EcoEngine.Rendering.VirtualMeshManager vmm = EcoEngine.Rendering.VirtualMeshManager.current;
                if (vmm == null || vmm.vmmm == null)
                    return;

                EcoEngine.Rendering.VirtualMeshMatManager vmmm = vmm.vmmm;
                int dispatchCount = vmmm.GlobalPrefabInstanceClusterDispatchCount;
                if (dispatchCount <= 0)
                    return;

                CameraData cameraData = renderingData.cameraData;
                ComputeShader cs = Settings.computePassesShader;
                CommandBuffer cmd = new CommandBuffer { name = "Mac VG GPU VP Recull" };
                try
                {
                    int prefabCount = vmm.VirtualMeshPrefabList != null ? vmm.VirtualMeshPrefabList.Count : 0;
                    int resetKernel = 0;
                    int resetGroups = 1;
                    if (prefabCount > 64)
                    {
                        resetKernel = 2;
                        resetGroups = prefabCount / 128 + 1;
                    }
                    else if (prefabCount > 32)
                    {
                        resetKernel = 1;
                    }

                    cmd.SetComputeConstantBufferParam(cs, EcoEngine.Rendering.VirtualMeshShaderProperties.VisibilityPassConstants,
                        vmmm.GlobalVisibilityPassConstantBuffer, 0, 16);
                    cmd.SetComputeBufferParam(cs, resetKernel, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalDrawArgsBufferUAV,
                        vmmm.GlobalDrawArgsBuffer);
                    cmd.DispatchCompute(cs, resetKernel, resetGroups, 1, 1);

                    Matrix4x4 vp = cameraData.GetGPUProjectionMatrix() * cameraData.GetViewMatrix();
                    cmd.SetComputeMatrixParam(cs, EcoEngine.Rendering.VirtualMeshShaderProperties.Matrix_VP, vp);
                    cmd.SetComputeVectorParam(cs, EcoEngine.Rendering.VirtualMeshShaderProperties.ComputeVGData, new Vector4(
                        Settings.VMClusterLODProjectionErrorFactor,
                        Settings.VMDepthDrawProjectionBias,
                        Settings.VMDepthShadowProjectionBias,
                        0f));

                    if (Settings.depthPyramid != null)
                        cmd.SetGlobalTexture(EcoEngine.Rendering.VirtualMeshShaderProperties.DepthPyramid, Settings.depthPyramid);

                    cmd.SetComputeConstantBufferParam(cs, EcoEngine.Rendering.VirtualMeshShaderProperties.PrefabDispatchRanges,
                        vmmm.GlobalPrefabDispatchRangeBuffer, 0, 8192);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalPrefabInstanceClusterStaticBuffer,
                        vmmm.GlobalPrefabInstanceClusterStaticBuffer);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalPrefabInstanceClusterDynamicBuffer,
                        vmmm.GlobalPrefabInstanceClusterDynamicBuffer);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalInstanceDataBuffer,
                        vmmm.GlobalInstanceDataBuffer);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalGroupDataBuffer,
                        vmmm.GlobalGroupDataBuffer);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalDrawIndexMatrixBuffer,
                        vmmm.GlobalDrawIndexMatrixBuffer);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalDrawArgsBufferUAV,
                        vmmm.GlobalDrawArgsBuffer);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalVisibleInstanceDataBufferUAV,
                        vmmm.GlobalVisibleInstanceDataBuffer);
                    cmd.SetComputeBufferParam(cs, 3, EcoEngine.Rendering.VirtualMeshShaderProperties.GlobalVisibleInstanceShadowDataBufferUAV,
                        vmmm.GlobalVisibleInstanceShadowDataBuffer);

                    int remaining = dispatchCount;
                    int passes = Mathf.CeilToInt(remaining / 8388480f);
                    for (int i = 0; i < passes; i++)
                    {
                        cmd.SetComputeIntParam(cs, EcoEngine.Rendering.VirtualMeshShaderProperties.DispatchPassOffset, i * 8388480);
                        int groups = remaining > 8388480 ? 65535 : Mathf.CeilToInt(remaining / 128f);
                        cmd.DispatchCompute(cs, 3, groups, 1, 1);
                        remaining -= 8388480;
                    }
                }
                finally
                {
                    context.ExecuteCommandBuffer(cmd);
                    cmd.Release();
                }
            }
        }

        ForceGpuVpCameraTypePass _forceTypePass;
        RestoreGameCameraTypePass _restoreTypePass;
        RecullPass _recullPass;
        bool _logged;

        public override void Create()
        {
            name = "[Mac] VG GPU VP Fix";
            _forceTypePass = new ForceGpuVpCameraTypePass();
            _restoreTypePass = new RestoreGameCameraTypePass();
            _recullPass = new RecullPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                return;
            if (!Application.isPlaying)
                return;
            if (renderingData.cameraData.cameraType != CameraType.Game)
                return;

            if (_recullPass.Settings == null)
                _recullPass.Settings = FindVgSettings();
            if (_recullPass.Settings == null)
                return;

            int vgEvent = (int)(_recullPass.Settings.VGVisiblePassEvent + _recullPass.Settings.VGVisiblePassEventBias);
            _forceTypePass.renderPassEvent = (RenderPassEvent)(vgEvent - 1);
            _restoreTypePass.renderPassEvent = (RenderPassEvent)(vgEvent + 1);
            _recullPass.renderPassEvent = (RenderPassEvent)(vgEvent + 2);
            renderer.EnqueuePass(_forceTypePass);
            renderer.EnqueuePass(_restoreTypePass);
            renderer.EnqueuePass(_recullPass);

            if (!_logged)
            {
                _logged = true;
                Debug.Log("[MacGPUSafeGuard] [VG VP Fix] Enqueued GPU-projection recull after VGVisibilityPass.");
            }
        }

        static VirtualGeometryRenderFeature.Settings FindVgSettings()
        {
            Object[] features = Resources.FindObjectsOfTypeAll(typeof(VirtualGeometryRenderFeature));
            if (features == null)
                return null;
            for (int i = 0; i < features.Length; i++)
            {
                var vg = features[i] as VirtualGeometryRenderFeature;
                if (vg != null && vg.settings != null)
                    return vg.settings;
            }
            return null;
        }
    }
}
