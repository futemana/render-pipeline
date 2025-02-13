using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class DrawRenderingLayersFeature : ScriptableRendererFeature
{
    private class DrawRenderingLayersPass : ScriptableRenderPass
    {
        private ProfilingSampler m_ProfilingSampler;
        private RTHandle m_TestRenderingLayersTextureHandle;
        private PassData m_PassData;
        public DrawRenderingLayersPass()
        {
            m_ProfilingSampler = new ProfilingSampler("Draw Rendering Layers");
            this.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            m_PassData = new PassData();
        }

        public void Setup(RTHandle renderingLayerTestTextureHandle)
        {
            m_TestRenderingLayersTextureHandle = renderingLayerTestTextureHandle;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_PassData.viewportScale = m_TestRenderingLayersTextureHandle.useScaling ? new Vector2(m_TestRenderingLayersTextureHandle.rtHandleProperties.rtHandleScale.x, m_TestRenderingLayersTextureHandle.rtHandleProperties.rtHandleScale.y) : Vector2.one;

            ExecutePass(CommandBufferHelpers.GetRasterCommandBuffer(renderingData.commandBuffer), m_PassData);
        }

        private void ExecutePass(RasterCommandBuffer cmd, PassData data)
        {
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                Blitter.BlitTexture(cmd, m_TestRenderingLayersTextureHandle, data.viewportScale, 0, true);
            }
        }

        private class PassData
        {
            internal DrawRenderingLayersPass pass;
            internal TextureHandle color;
            internal Vector2 viewportScale;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, FrameResources frameResources, ref RenderingData renderingData)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw Rendering Layers", out var passData, m_ProfilingSampler))
            {
                UniversalRenderer renderer = (UniversalRenderer) renderingData.cameraData.renderer;

                passData.color = renderer.activeColorTexture;
                builder.UseTextureFragment(passData.color, 0, IBaseRenderGraphBuilder.AccessFlags.Write);
                builder.UseTexture(renderingLayerTexture);
                passData.viewportScale = m_TestRenderingLayersTextureHandle.useScaling ? new Vector2(m_TestRenderingLayersTextureHandle.rtHandleProperties.rtHandleScale.x, m_TestRenderingLayersTextureHandle.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                builder.AllowPassCulling(false);

                passData.pass = this;

                builder.SetRenderFunc((PassData data, RasterGraphContext rgContext) =>
                {
                    data.pass.ExecutePass(rgContext.cmd, data);
                });
            }
        }
    }

    private class DrawRenderingLayersPrePass : ScriptableRenderPass
    {
        private static class ShaderPropertyId
        {
            public static readonly int scaleBias = Shader.PropertyToID("_ScaleBias");
        }

        private Material m_Material;
        private ProfilingSampler m_ProfilingSampler;
        private RTHandle m_ColoredRenderingLayersTextureHandle;
        private Vector4[] m_RenderingLayerColors = new Vector4[32];

        public DrawRenderingLayersPrePass(RenderPassEvent renderPassEvent)
        {
            m_ProfilingSampler = new ProfilingSampler("Rendering Layers PrePass");
            this.renderPassEvent = renderPassEvent;
        }

        public void Setup(RTHandle renderingLayerTestTextureHandle, Material material)
        {
            m_ColoredRenderingLayersTextureHandle = renderingLayerTestTextureHandle;

            m_Material = material;

            for (int i = 0; i < 32; i++)
                m_RenderingLayerColors[i] = Color.HSVToRGB(i / 32f, 1, 1);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ConfigureTarget(m_ColoredRenderingLayersTextureHandle);
            ConfigureClear(ClearFlag.ColorStencil, Color.black);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ExecutePass(ref renderingData);
        }

        internal void ExecutePass(ref RenderingData renderingData)
        {
            var cmd = CommandBufferHelpers.GetRasterCommandBuffer(renderingData.commandBuffer);
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                Render(cmd, renderingData.cameraData);
            }
        }

        private void Render(RasterCommandBuffer cmd, in CameraData cameraData)
        {
            cmd.SetGlobalVectorArray("_RenderingLayersColors", m_RenderingLayerColors);
            cmd.SetGlobalVector(ShaderPropertyId.scaleBias, new Vector4(1, 1, 0, 0));
            cmd.DrawProcedural(Matrix4x4.identity, m_Material, 0, MeshTopology.Triangles, 3, 1);
        }

        private class PassData
        {
            internal DrawRenderingLayersPrePass pass;
            internal RenderingData renderingData;
            internal TextureHandle color;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, FrameResources frameResources, ref RenderingData renderingData)
        {
            UniversalRenderer renderer = (UniversalRenderer)renderingData.cameraData.renderer;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw Rendering PrePass", out var passData, m_ProfilingSampler))
            {
                renderingLayerTexture = renderGraph.ImportTexture(m_ColoredRenderingLayersTextureHandle);
                builder.UseTextureFragment(renderingLayerTexture, 0, IBaseRenderGraphBuilder.AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);

                passData.pass = this;
                passData.renderingData = renderingData;
                passData.color = renderingLayerTexture;

                builder.SetRenderFunc((PassData data, RasterGraphContext rgContext) =>
                {
                    data.pass.ExecutePass(ref data.renderingData);
                });
            }
        }
    }

    private const string k_ShaderName = "Hidden/Universal Render Pipeline/DrawRenderingLayers";

    [SerializeField]
    private Material m_Material;

    [SerializeField]
    private RenderPassEvent m_Event = RenderPassEvent.AfterRenderingPrePasses;

    [SerializeField]
    internal RenderingLayerUtils.MaskSize m_MaskSize = RenderingLayerUtils.MaskSize.Bits8;

    private DrawRenderingLayersPrePass m_DrawRenderingLayerPass;
    private DrawRenderingLayersPass m_RequestRenderingLayerPass;

    private RTHandle m_ColoredRenderingLayersTextureHandle;

    internal override bool RequireRenderingLayers(bool isDeferred, bool needsGBufferAccurateNormals, out RenderingLayerUtils.Event atEvent, out RenderingLayerUtils.MaskSize maskSize)
    {
        if (m_Event < RenderPassEvent.AfterRenderingGbuffer)
            atEvent = RenderingLayerUtils.Event.DepthNormalPrePass;
        else
            atEvent = RenderingLayerUtils.Event.Opaque;
        maskSize = m_MaskSize;
        return true;
    }

    /// <inheritdoc/>
    public override void Create()
    {
        m_DrawRenderingLayerPass = new DrawRenderingLayersPrePass(m_Event);
        m_RequestRenderingLayerPass = new DrawRenderingLayersPass();
    }

    protected override void Dispose(bool disposing)
    {
        m_ColoredRenderingLayersTextureHandle?.Release();
    }

    internal static TextureHandle renderingLayerTexture;

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.msaaSamples = 1;
        desc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB;
        desc.depthBufferBits = 0;
        RenderingUtils.ReAllocateIfNeeded(ref m_ColoredRenderingLayersTextureHandle, desc, name: "_ColoredRenderingLayersTexture");

        m_DrawRenderingLayerPass.Setup(m_ColoredRenderingLayersTextureHandle, m_Material);
        renderer.EnqueuePass(m_DrawRenderingLayerPass);
        m_RequestRenderingLayerPass.Setup(m_ColoredRenderingLayersTextureHandle);
        renderer.EnqueuePass(m_RequestRenderingLayerPass);
    }
}
