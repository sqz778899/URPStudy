using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Rendering modes for Universal renderer.
    /// </summary>
    public enum RenderingMode
    {
        /// <summary>Render all objects and lighting in one pass, with a hard limit on the number of lights that can be applied on an object.</summary>
        Forward = 0,
        /// <summary>Render all objects and lighting in one pass using a clustered data structure to access lighting data.</summary>
        [InspectorName("Forward+")]
        ForwardPlus = 2,
        /// <summary>Render all objects first in a g-buffer pass, then apply all lighting in a separate pass using deferred shading.</summary>
        Deferred = 1
    };

    /// <summary>
    /// When the Universal Renderer should use Depth Priming in Forward mode.
    /// </summary>
    public enum DepthPrimingMode
    {
        /// <summary>Depth Priming will never be used.</summary>
        Disabled,
        /// <summary>Depth Priming will only be used if there is a depth prepass needed by any of the render passes.</summary>
        Auto,
        /// <summary>A depth prepass will be explicitly requested so Depth Priming can be used.</summary>
        Forced,
    }

    /// <summary>
    /// Default renderer for Universal RP.
    /// This renderer is supported on all Universal RP supported platforms.
    /// It uses a classic forward rendering strategy with per-object light culling.
    /// </summary>
    public sealed partial class UniversalRenderer : ScriptableRenderer
    {
        #if UNITY_SWITCH || UNITY_ANDROID
        const GraphicsFormat k_DepthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt;
        const int k_DepthBufferBits = 24;
        #else
        const GraphicsFormat k_DepthStencilFormat = GraphicsFormat.D32_SFloat_S8_UInt;
        const int k_DepthBufferBits = 32;
        #endif

        const int k_FinalBlitPassQueueOffset = 1;
        const int k_AfterFinalBlitPassQueueOffset = k_FinalBlitPassQueueOffset + 1;

        static readonly List<ShaderTagId> k_DepthNormalsOnly = new List<ShaderTagId> { new ShaderTagId("DepthNormalsOnly") };

        private static class Profiling
        {
            private const string k_Name = nameof(UniversalRenderer);
            public static readonly ProfilingSampler createCameraRenderTarget = new ProfilingSampler($"{k_Name}.{nameof(CreateCameraRenderTarget)}");
        }

        /// <inheritdoc/>
        public override int SupportedCameraStackingTypes()
        {
            switch (m_RenderingMode)
            {
                case RenderingMode.Forward:
                case RenderingMode.ForwardPlus:
                    return 1 << (int)CameraRenderType.Base | 1 << (int)CameraRenderType.Overlay;
                case RenderingMode.Deferred:
                    return 1 << (int)CameraRenderType.Base;
                default:
                    return 0;
            }
        }

        // Rendering mode setup from UI. The final rendering mode used can be different. See renderingModeActual.
        internal RenderingMode renderingModeRequested => m_RenderingMode;

        // Actual rendering mode, which may be different (ex: wireframe rendering, hardware not capable of deferred rendering).
        internal RenderingMode renderingModeActual => renderingModeRequested == RenderingMode.Deferred && (GL.wireframe || (DebugHandler != null && DebugHandler.IsActiveModeUnsupportedForDeferred) || m_DeferredLights == null || !m_DeferredLights.IsRuntimeSupportedThisFrame() || m_DeferredLights.IsOverlay)
        ? RenderingMode.Forward
        : this.renderingModeRequested;

        bool m_Clustering;

        internal bool accurateGbufferNormals => m_DeferredLights != null ? m_DeferredLights.AccurateGbufferNormals : false;

#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
        internal bool needTransparencyPass { get { return !UniversalRenderPipeline.asset.useAdaptivePerformance || !AdaptivePerformance.AdaptivePerformanceRenderSettings.SkipTransparentObjects;; } }
#endif
        /// <summary>Property to control the depth priming behavior of the forward rendering path.</summary>
        public DepthPrimingMode depthPrimingMode { get { return m_DepthPrimingMode; } set { m_DepthPrimingMode = value; } }
        DepthOnlyPass m_DepthPrepass;
        DepthNormalOnlyPass m_DepthNormalPrepass;
        CopyDepthPass m_PrimedDepthCopyPass;
        MotionVectorRenderPass m_MotionVectorPass;
        MainLightShadowCasterPass m_MainLightShadowCasterPass;
        AdditionalLightsShadowCasterPass m_AdditionalLightsShadowCasterPass;
        GBufferPass m_GBufferPass;
        CopyDepthPass m_GBufferCopyDepthPass;
        DeferredPass m_DeferredPass;
        DrawObjectsPass m_RenderOpaqueForwardOnlyPass;
        DrawObjectsPass m_RenderOpaqueForwardPass;
        DrawObjectsWithRenderingLayersPass m_RenderOpaqueForwardWithRenderingLayersPass;
        DrawSkyboxPass m_DrawSkyboxPass;
        CopyDepthPass m_CopyDepthPass;
        CopyColorPass m_CopyColorPass;
        TransparentSettingsPass m_TransparentSettingsPass;
        DrawObjectsPass m_RenderTransparentForwardPass;
        InvokeOnRenderObjectCallbackPass m_OnRenderObjectCallbackPass;
        FinalBlitPass m_FinalBlitPass;
        CapturePass m_CapturePass;
#if ENABLE_VR && ENABLE_XR_MODULE
        XROcclusionMeshPass m_XROcclusionMeshPass;
        CopyDepthPass m_XRCopyDepthPass;
#endif
#if UNITY_EDITOR
        CopyDepthPass m_FinalDepthCopyPass;
#endif
        DrawScreenSpaceUIPass m_DrawOffscreenUIPass;
        DrawScreenSpaceUIPass m_DrawOverlayUIPass;

        internal RenderTargetBufferSystem m_ColorBufferSystem;

        internal RTHandle m_ActiveCameraColorAttachment;
        RTHandle m_ColorFrontBuffer;
        internal RTHandle m_ActiveCameraDepthAttachment;
        internal RTHandle m_CameraDepthAttachment;
        RTHandle m_XRTargetHandleAlias;
        internal RTHandle m_DepthTexture;
        RTHandle m_NormalsTexture;
        RTHandle m_DecalLayersTexture;
        RTHandle m_OpaqueColor;
        RTHandle m_MotionVectorColor;
        RTHandle m_MotionVectorDepth;

        ForwardLights m_ForwardLights;
        DeferredLights m_DeferredLights;
        RenderingMode m_RenderingMode;
        DepthPrimingMode m_DepthPrimingMode;
        CopyDepthMode m_CopyDepthMode;
        bool m_DepthPrimingRecommended;
        StencilState m_DefaultStencilState;
        LightCookieManager m_LightCookieManager;
        IntermediateTextureMode m_IntermediateTextureMode;

        // Materials used in URP Scriptable Render Passes
        Material m_BlitMaterial = null;
        Material m_BlitHDRMaterial = null;
        Material m_CopyDepthMaterial = null;
        Material m_SamplingMaterial = null;
        Material m_StencilDeferredMaterial = null;
        Material m_CameraMotionVecMaterial = null;
        Material m_ObjectMotionVecMaterial = null;

        PostProcessPasses m_PostProcessPasses;
        internal ColorGradingLutPass colorGradingLutPass { get => m_PostProcessPasses.colorGradingLutPass; }
        internal PostProcessPass postProcessPass { get => m_PostProcessPasses.postProcessPass; }
        internal PostProcessPass finalPostProcessPass { get => m_PostProcessPasses.finalPostProcessPass; }
        internal RTHandle colorGradingLut { get => m_PostProcessPasses.colorGradingLut; }
        internal DeferredLights deferredLights { get => m_DeferredLights; }

        /// <summary>
        /// Constructor for the Universal Renderer.
        /// </summary>
        /// <param name="data">The settings to create the renderer with.</param>
        public UniversalRenderer(UniversalRendererData data) : base(data)
        {
            // Query and cache runtime platform info first before setting up URP.
            PlatformAutoDetect.Initialize();

#if ENABLE_VR && ENABLE_XR_MODULE
            Experimental.Rendering.XRSystem.Initialize(XRPassUniversal.Create, data.xrSystemData.shaders.xrOcclusionMeshPS, data.xrSystemData.shaders.xrMirrorViewPS);
#endif
            m_BlitMaterial = CoreUtils.CreateEngineMaterial(data.shaders.coreBlitPS);
            m_BlitHDRMaterial = CoreUtils.CreateEngineMaterial(data.shaders.blitHDROverlay);
            m_CopyDepthMaterial = CoreUtils.CreateEngineMaterial(data.shaders.copyDepthPS);
            m_SamplingMaterial = CoreUtils.CreateEngineMaterial(data.shaders.samplingPS);
            m_StencilDeferredMaterial = CoreUtils.CreateEngineMaterial(data.shaders.stencilDeferredPS);
            m_CameraMotionVecMaterial = CoreUtils.CreateEngineMaterial(data.shaders.cameraMotionVector);
            m_ObjectMotionVecMaterial = CoreUtils.CreateEngineMaterial(data.shaders.objectMotionVector);

            StencilStateData stencilData = data.defaultStencilState;
            m_DefaultStencilState = StencilState.defaultValue;
            m_DefaultStencilState.enabled = stencilData.overrideStencilState;
            m_DefaultStencilState.SetCompareFunction(stencilData.stencilCompareFunction);
            m_DefaultStencilState.SetPassOperation(stencilData.passOperation);
            m_DefaultStencilState.SetFailOperation(stencilData.failOperation);
            m_DefaultStencilState.SetZFailOperation(stencilData.zFailOperation);

            m_IntermediateTextureMode = data.intermediateTextureMode;

            if (UniversalRenderPipeline.asset?.supportsLightCookies ?? false)
            {
                var settings = LightCookieManager.Settings.Create();
                var asset = UniversalRenderPipeline.asset;
                if (asset)
                {
                    settings.atlas.format = asset.additionalLightsCookieFormat;
                    settings.atlas.resolution = asset.additionalLightsCookieResolution;
                }

                m_LightCookieManager = new LightCookieManager(ref settings);
            }

            this.stripShadowsOffVariants = true;
            this.stripAdditionalLightOffVariants = true;
#if ENABLE_VR && ENABLE_VR_MODULE
#if PLATFORM_WINRT || PLATFORM_ANDROID
            // AdditionalLightOff variant is available on HL&Quest platform due to performance consideration.
            this.stripAdditionalLightOffVariants = !PlatformAutoDetect.isXRMobile;
#endif
#endif

            ForwardLights.InitParams forwardInitParams;
            forwardInitParams.lightCookieManager = m_LightCookieManager;
            forwardInitParams.forwardPlus = false;
            m_Clustering = false;
            m_ForwardLights = new ForwardLights(forwardInitParams);
            //m_DeferredLights.LightCulling = data.lightCulling;
            this.m_RenderingMode = data.renderingMode;
            this.m_DepthPrimingMode = data.depthPrimingMode;
            this.m_CopyDepthMode = data.copyDepthMode;
            useRenderPassEnabled = data.useNativeRenderPass && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;

#if UNITY_ANDROID || UNITY_IOS || UNITY_TVOS
            this.m_DepthPrimingRecommended = false;
#else
            this.m_DepthPrimingRecommended = true;
#endif

            // Note: Since all custom render passes inject first and we have stable sort,
            // we inject the builtin passes in the before events.
            m_MainLightShadowCasterPass = new MainLightShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);
            m_AdditionalLightsShadowCasterPass = new AdditionalLightsShadowCasterPass(RenderPassEvent.BeforeRenderingShadows);
            
            m_DepthPrepass = new DepthOnlyPass(RenderPassEvent.BeforeRenderingPrePasses, RenderQueueRange.opaque, data.opaqueLayerMask);
            m_DepthNormalPrepass = new DepthNormalOnlyPass(RenderPassEvent.BeforeRenderingPrePasses, RenderQueueRange.opaque, data.opaqueLayerMask);
            m_MotionVectorPass = new MotionVectorRenderPass(m_CameraMotionVecMaterial, m_ObjectMotionVecMaterial);

            m_PrimedDepthCopyPass = new CopyDepthPass(RenderPassEvent.AfterRenderingPrePasses, m_CopyDepthMaterial, true);

            // Always create this pass even in deferred because we use it for wireframe rendering in the Editor or offscreen depth texture rendering.
            m_RenderOpaqueForwardPass = new DrawObjectsPass(URPProfileId.DrawOpaqueObjects, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            m_RenderOpaqueForwardWithRenderingLayersPass = new DrawObjectsWithRenderingLayersPass(URPProfileId.DrawOpaqueObjects, true, RenderPassEvent.BeforeRenderingOpaques, RenderQueueRange.opaque, data.opaqueLayerMask, m_DefaultStencilState, stencilData.stencilReference);

            bool copyDepthAfterTransparents = m_CopyDepthMode == CopyDepthMode.AfterTransparents;

            m_CopyDepthPass = new CopyDepthPass(
                copyDepthAfterTransparents ? RenderPassEvent.AfterRenderingTransparents : RenderPassEvent.AfterRenderingSkybox,
                m_CopyDepthMaterial,
                shouldClear: true,
                copyResolvedDepth: RenderingUtils.MultisampleDepthResolveSupported() && copyDepthAfterTransparents);

            m_DrawSkyboxPass = new DrawSkyboxPass(RenderPassEvent.BeforeRenderingSkybox);
            m_CopyColorPass = new CopyColorPass(RenderPassEvent.AfterRenderingSkybox, m_SamplingMaterial, m_BlitMaterial);
#if ADAPTIVE_PERFORMANCE_2_1_0_OR_NEWER
            if (needTransparencyPass)
#endif
            {
                m_TransparentSettingsPass = new TransparentSettingsPass(RenderPassEvent.BeforeRenderingTransparents, data.shadowTransparentReceive);
                m_RenderTransparentForwardPass = new DrawObjectsPass(URPProfileId.DrawTransparentObjects, false, RenderPassEvent.BeforeRenderingTransparents, RenderQueueRange.transparent, data.transparentLayerMask, m_DefaultStencilState, stencilData.stencilReference);
            }
            m_OnRenderObjectCallbackPass = new InvokeOnRenderObjectCallbackPass(RenderPassEvent.BeforeRenderingPostProcessing);

            m_DrawOffscreenUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.BeforeRenderingPostProcessing, true);
            m_DrawOverlayUIPass = new DrawScreenSpaceUIPass(RenderPassEvent.AfterRendering + k_AfterFinalBlitPassQueueOffset, false); // after m_FinalBlitPass

            {
                var postProcessParams = PostProcessParams.Create();
                postProcessParams.blitMaterial = m_BlitMaterial;
                postProcessParams.requestHDRFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                var asset = UniversalRenderPipeline.asset;
                if (asset)
                    postProcessParams.requestHDRFormat = UniversalRenderPipeline.MakeRenderTextureGraphicsFormat(asset.supportsHDR, asset.hdrColorBufferPrecision, false);

                m_PostProcessPasses = new PostProcessPasses(data.postProcessData, ref postProcessParams);
            }

            m_CapturePass = new CapturePass(RenderPassEvent.AfterRendering);
            m_FinalBlitPass = new FinalBlitPass(RenderPassEvent.AfterRendering + k_FinalBlitPassQueueOffset, m_BlitMaterial, m_BlitHDRMaterial);

#if UNITY_EDITOR
            m_FinalDepthCopyPass = new CopyDepthPass(RenderPassEvent.AfterRendering + 9, m_CopyDepthMaterial);
#endif

            // RenderTexture format depends on camera and pipeline (HDR, non HDR, etc)
            // Samples (MSAA) depend on camera and pipeline
            m_ColorBufferSystem = new RenderTargetBufferSystem("_CameraColorAttachment");

            supportedRenderingFeatures = new RenderingFeatures();

            LensFlareCommonSRP.mergeNeeded = 0;
            LensFlareCommonSRP.maxLensFlareWithOcclusionTemporalSample = 1;
            LensFlareCommonSRP.Initialize();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            m_ForwardLights.Cleanup();
            m_GBufferPass?.Dispose();
            m_PostProcessPasses.Dispose();
            m_FinalBlitPass?.Dispose();
            m_DrawOffscreenUIPass?.Dispose();
            m_DrawOverlayUIPass?.Dispose();

            m_XRTargetHandleAlias?.Release();

            ReleaseRenderTargets();

            base.Dispose(disposing);
            CoreUtils.Destroy(m_BlitMaterial);
            CoreUtils.Destroy(m_BlitHDRMaterial);
            CoreUtils.Destroy(m_CopyDepthMaterial);
            CoreUtils.Destroy(m_SamplingMaterial);
            CoreUtils.Destroy(m_StencilDeferredMaterial);
            CoreUtils.Destroy(m_CameraMotionVecMaterial);
            CoreUtils.Destroy(m_ObjectMotionVecMaterial);

            CleanupRenderGraphResources();

            LensFlareCommonSRP.Dispose();
        }

        internal override void ReleaseRenderTargets()
        {
            m_ColorBufferSystem.Dispose();
            if (m_DeferredLights != null && !m_DeferredLights.UseRenderPass)
                m_GBufferPass?.Dispose();

            m_PostProcessPasses.ReleaseRenderTargets();
            m_MainLightShadowCasterPass?.Dispose();
            m_AdditionalLightsShadowCasterPass?.Dispose();

            m_CameraDepthAttachment?.Release();
            m_DepthTexture?.Release();
            m_NormalsTexture?.Release();
            m_DecalLayersTexture?.Release();
            m_OpaqueColor?.Release();
            m_MotionVectorColor?.Release();
            m_MotionVectorDepth?.Release();
            hasReleasedRTs = true;
        }

        private void SetupFinalPassDebug(ref CameraData cameraData)
        {
            if ((DebugHandler != null) && DebugHandler.IsActiveForCamera(ref cameraData))
            {
                if (DebugHandler.TryGetFullscreenDebugMode(out DebugFullScreenMode fullScreenDebugMode, out int textureHeightPercent) &&
                    (fullScreenDebugMode != DebugFullScreenMode.ReflectionProbeAtlas || m_Clustering))
                {
                    Camera camera = cameraData.camera;
                    float screenWidth = camera.pixelWidth;
                    float screenHeight = camera.pixelHeight;

                    var relativeSize = Mathf.Clamp01(textureHeightPercent / 100f);
                    var height = relativeSize * screenHeight;
                    var width = relativeSize * screenWidth;

                    if (fullScreenDebugMode == DebugFullScreenMode.ReflectionProbeAtlas)
                    {
                        // Ensure that atlas is not stretched, but doesn't take up more than the percentage in any dimension.
                        var texture = m_ForwardLights.reflectionProbeManager.atlasRT;
                        var targetWidth = height * texture.width / texture.height;
                        if (targetWidth > width)
                        {
                            height = width * texture.height / texture.width;
                        }
                        else
                        {
                            width = targetWidth;
                        }
                    }

                    float normalizedSizeX = width / screenWidth;
                    float normalizedSizeY = height / screenHeight;

                    Rect normalizedRect = new Rect(1 - normalizedSizeX, 1 - normalizedSizeY, normalizedSizeX, normalizedSizeY);

                    switch (fullScreenDebugMode)
                    {
                        case DebugFullScreenMode.Depth:
                        {
                            DebugHandler.SetDebugRenderTarget(m_DepthTexture.nameID, normalizedRect, true);
                            break;
                        }
                        case DebugFullScreenMode.AdditionalLightsShadowMap:
                        {
                            DebugHandler.SetDebugRenderTarget(m_AdditionalLightsShadowCasterPass.m_AdditionalLightsShadowmapHandle, normalizedRect, false);
                            break;
                        }
                        case DebugFullScreenMode.MainLightShadowMap:
                        {
                            DebugHandler.SetDebugRenderTarget(m_MainLightShadowCasterPass.m_MainLightShadowmapTexture, normalizedRect, false);
                            break;
                        }
                        case DebugFullScreenMode.ReflectionProbeAtlas:
                        {
                            DebugHandler.SetDebugRenderTarget(m_ForwardLights.reflectionProbeManager.atlasRT, normalizedRect, false);
                            break;
                        }
                        default:
                        {
                            break;
                        }
                    }
                }
                else
                {
                    DebugHandler.ResetDebugRenderTarget();
                }
            }
        }

        bool IsDepthPrimingEnabled(ref CameraData cameraData)
        {
            // depth priming requires an extra depth copy, disable it on platforms not supporting it (like GLES when MSAA is on)
            if (!CanCopyDepth(ref cameraData))
                return false;

            bool depthPrimingRequested = (m_DepthPrimingRecommended && m_DepthPrimingMode == DepthPrimingMode.Auto) || m_DepthPrimingMode == DepthPrimingMode.Forced;
            bool isForwardRenderingMode = m_RenderingMode == RenderingMode.Forward || m_RenderingMode == RenderingMode.ForwardPlus;
            bool isFirstCameraToWriteDepth = cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth;
            // Enabled Depth priming when baking Reflection Probes causes artefacts (UUM-12397)
            bool isNotReflectionCamera = cameraData.cameraType != CameraType.Reflection;

            return  depthPrimingRequested && isForwardRenderingMode && isFirstCameraToWriteDepth && isNotReflectionCamera;
        }

        bool IsGLESDevice()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
        }

        bool IsGLDevice()
        {
            return IsGLESDevice() || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;
        }

        public void SetupInSceneView(ref RenderingData renderingData)
        {
            useRenderPassEnabled = false;
            colorGradingLutPass.ConfigureDescriptor(in renderingData.postProcessingData, out var desc, out var filterMode);
            RenderingUtils.ReAllocateIfNeeded(ref m_PostProcessPasses.m_ColorGradingLut, desc, filterMode, TextureWrapMode.Clamp, anisoLevel: 0, name: "_InternalGradingLut");
            colorGradingLutPass.Setup(colorGradingLut);
            EnqueuePass(colorGradingLutPass);
            m_FinalDepthCopyPass.Setup(m_DepthTexture, k_CameraTarget);
            m_FinalDepthCopyPass.CopyToDepth = true;
            m_FinalDepthCopyPass.MssaSamples = 0;
            EnqueuePass(m_FinalDepthCopyPass);
        }
        /// <inheritdoc />
        public override void Setup(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ref CameraData cameraData = ref renderingData.cameraData;
            Camera camera = cameraData.camera;
            RenderTextureDescriptor cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
            CommandBuffer cmd = renderingData.commandBuffer;
            
            // TODO: We could cache and generate the LUT before rendering the stack
            bool isSceneViewOrPreviewCamera = cameraData.isSceneViewCamera || cameraData.cameraType == CameraType.Preview;
            if (isSceneViewOrPreviewCamera)
                SetupInSceneView(ref renderingData);
            
            m_MainLightShadowCasterPass.Setup(ref renderingData);
            m_TransparentSettingsPass.Setup(ref renderingData);
            m_CopyDepthPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

            //Set rt descriptors so preview camera's have access should it be needed
            RenderTextureDescriptor colorDescriptor = cameraTargetDescriptor;
            colorDescriptor.useMipMap = false;
            colorDescriptor.autoGenerateMips = false;
            colorDescriptor.depthBufferBits = (int)DepthBits.None;
            m_ColorBufferSystem.SetCameraSettings(colorDescriptor, FilterMode.Bilinear);

            // Configure all settings require to start a new camera stack (base camera only)
            if (cameraData.renderType == CameraRenderType.Base)
            {
                // Scene filtering redraws the objects on top of the resulting frame. It has to draw directly to the sceneview buffer.
                bool sceneViewFilterEnabled = camera.sceneViewFilterMode == Camera.SceneViewFilterMode.ShowFiltered;
                bool intermediateRenderTexture =!sceneViewFilterEnabled;
                RenderTargetIdentifier targetId = BuiltinRenderTextureType.CameraTarget;
                
                // Doesn't create texture for Overlay cameras as they are already overlaying on top of created textures.
                if (intermediateRenderTexture)
                    CreateCameraRenderTarget(context, ref cameraTargetDescriptor, false, cmd, ref cameraData);

                m_ActiveCameraColorAttachment = m_ColorBufferSystem.PeekBackBuffer();
                m_ActiveCameraDepthAttachment = m_CameraDepthAttachment;
            }
            
            // Assign camera targets (color and depth)
            ConfigureCameraTarget(m_ActiveCameraColorAttachment, m_ActiveCameraDepthAttachment);
           
            
            RenderTextureDescriptor depthDescriptor = cameraTargetDescriptor;
            depthDescriptor.graphicsFormat = GraphicsFormat.None;
            depthDescriptor.depthStencilFormat = k_DepthStencilFormat;
            depthDescriptor.depthBufferBits = k_DepthBufferBits;
            depthDescriptor.msaaSamples = 1;// Depth-Only pass don't use MSAA
            RenderingUtils.ReAllocateIfNeeded(ref m_DepthTexture, depthDescriptor, FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_CameraDepthTexture");
            cmd.SetGlobalTexture(m_DepthTexture.name, m_DepthTexture.nameID);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            // Optimized store actions are very important on tile based GPUs and have a great impact on performance.
            // if MSAA is enabled and any of the following passes need a copy of the color or depth target, make sure the MSAA'd surface is stored
            // if following passes won't use it then just resolve (the Resolve action will still store the resolved surface, but discard the MSAA'd surface, which is very expensive to store).
            DrawObjectsPass renderOpaqueForwardPass = m_RenderOpaqueForwardPass;
            renderOpaqueForwardPass.ConfigureColorStoreAction(RenderBufferStoreAction.Store);
            renderOpaqueForwardPass.ConfigureDepthStoreAction(RenderBufferStoreAction.Store);
            
            
            EnqueuePass(m_MainLightShadowCasterPass);
            EnqueuePass(renderOpaqueForwardPass);
            EnqueuePass(m_DrawSkyboxPass);
            m_CopyDepthPass.Setup(m_ActiveCameraDepthAttachment, m_DepthTexture);
            EnqueuePass(m_CopyDepthPass);
            EnqueuePass(m_RenderTransparentForwardPass);
            EnqueuePass(m_OnRenderObjectCallbackPass);
            SetupFinalPassDebug(ref cameraData);
            m_FinalBlitPass.Setup(cameraTargetDescriptor, m_ActiveCameraColorAttachment);
            EnqueuePass(m_FinalBlitPass);
        }

        /// <inheritdoc />
        public override void SetupLights(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_ForwardLights.Setup(context, ref renderingData);

            if (this.renderingModeActual == RenderingMode.Deferred)
                m_DeferredLights.SetupLights(context, ref renderingData);
        }

        /// <inheritdoc />
        public override void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters,
            ref CameraData cameraData)
        {
            // We disable shadow casters if both shadow casting modes are turned off
            // or the shadow distance has been turned down to zero
            bool isShadowCastingDisabled = !UniversalRenderPipeline.asset.supportsMainLightShadows && !UniversalRenderPipeline.asset.supportsAdditionalLightShadows;
            bool isShadowDistanceZero = Mathf.Approximately(cameraData.maxShadowDistance, 0.0f);
            if (isShadowCastingDisabled || isShadowDistanceZero)
            {
                cullingParameters.cullingOptions &= ~CullingOptions.ShadowCasters;
            }

            cullingParameters.maximumVisibleLights = UniversalRenderPipeline.maxVisibleAdditionalLights + 1;
            cullingParameters.shadowDistance = cameraData.maxShadowDistance;

            cullingParameters.conservativeEnclosingSphere = UniversalRenderPipeline.asset.conservativeEnclosingSphere;

            cullingParameters.numIterationsEnclosingSphere = UniversalRenderPipeline.asset.numIterationsEnclosingSphere;
        }

        /// <inheritdoc />
        public override void FinishRendering(CommandBuffer cmd)
        {
            m_ColorBufferSystem.Clear();
            m_ActiveCameraColorAttachment = null;
            m_ActiveCameraDepthAttachment = null;
        }

        void EnqueueDeferred(ref RenderingData renderingData, bool hasDepthPrepass, bool hasNormalPrepass, bool hasRenderingLayerPrepass, bool applyMainShadow, bool applyAdditionalShadow)
        {
            m_DeferredLights.Setup(
                ref renderingData,
                applyAdditionalShadow ? m_AdditionalLightsShadowCasterPass : null,
                hasDepthPrepass,
                hasNormalPrepass,
                hasRenderingLayerPrepass,
                m_DepthTexture,
                m_ActiveCameraDepthAttachment,
                m_ActiveCameraColorAttachment
            );
            // Need to call Configure for both of these passes to setup input attachments as first frame otherwise will raise errors
            if (useRenderPassEnabled && m_DeferredLights.UseRenderPass)
            {
                m_GBufferPass.Configure(null, renderingData.cameraData.cameraTargetDescriptor);
                m_DeferredPass.Configure(null, renderingData.cameraData.cameraTargetDescriptor);
            }

            EnqueuePass(m_GBufferPass);

            //Must copy depth for deferred shading: TODO wait for API fix to bind depth texture as read-only resource.
            if (!useRenderPassEnabled || !m_DeferredLights.UseRenderPass)
            {
                m_GBufferCopyDepthPass.Setup(m_CameraDepthAttachment, m_DepthTexture);
                EnqueuePass(m_GBufferCopyDepthPass);
            }

            EnqueuePass(m_DeferredPass);

            EnqueuePass(m_RenderOpaqueForwardOnlyPass);
        }

        private struct RenderPassInputSummary
        {
            internal bool requiresDepthTexture;
            internal bool requiresDepthPrepass;
            internal bool requiresNormalsTexture;
            internal bool requiresColorTexture;
            internal bool requiresColorTextureCreated;
            internal bool requiresMotionVectors;
            internal RenderPassEvent requiresDepthNormalAtEvent;
            internal RenderPassEvent requiresDepthTextureEarliestEvent;
        }

        private RenderPassInputSummary GetRenderPassInputs(ref RenderingData renderingData)
        {
            RenderPassEvent beforeMainRenderingEvent = RenderPassEvent.BeforeRenderingOpaques;

            RenderPassInputSummary inputSummary = new RenderPassInputSummary();
            inputSummary.requiresDepthNormalAtEvent = RenderPassEvent.BeforeRenderingOpaques;
            inputSummary.requiresDepthTextureEarliestEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            for (int i = 0; i < activeRenderPassQueue.Count; ++i)
            {
                ScriptableRenderPass pass = activeRenderPassQueue[i];
                bool needsDepth = (pass.input & ScriptableRenderPassInput.Depth) != ScriptableRenderPassInput.None;
                bool needsNormals = (pass.input & ScriptableRenderPassInput.Normal) != ScriptableRenderPassInput.None;
                bool needsColor = (pass.input & ScriptableRenderPassInput.Color) != ScriptableRenderPassInput.None;
                bool needsMotion = (pass.input & ScriptableRenderPassInput.Motion) != ScriptableRenderPassInput.None;
                bool eventBeforeMainRendering = pass.renderPassEvent <= beforeMainRenderingEvent;

                // TODO: Need a better way to handle this, probably worth to recheck after render graph
                // DBuffer requires color texture created as it does not handle y flip correctly
                if (pass is DBufferRenderPass dBufferRenderPass)
                {
                    inputSummary.requiresColorTextureCreated = true;
                }

                inputSummary.requiresDepthTexture |= needsDepth;
                inputSummary.requiresDepthPrepass |= needsNormals || needsDepth && eventBeforeMainRendering;
                inputSummary.requiresNormalsTexture |= needsNormals;
                inputSummary.requiresColorTexture |= needsColor;
                inputSummary.requiresMotionVectors |= needsMotion;
                if (needsDepth)
                    inputSummary.requiresDepthTextureEarliestEvent = (RenderPassEvent)Mathf.Min((int)pass.renderPassEvent, (int)inputSummary.requiresDepthTextureEarliestEvent);
                if (needsNormals || needsDepth)
                    inputSummary.requiresDepthNormalAtEvent = (RenderPassEvent)Mathf.Min((int)pass.renderPassEvent, (int)inputSummary.requiresDepthNormalAtEvent);
            }

            // NOTE: TAA and motion vector dependencies added here to share between Execute and Render (Graph) paths.
            // TAA in postprocess requires motion to function.
            if (renderingData.cameraData.IsTemporalAAEnabled())
                inputSummary.requiresMotionVectors = true;

            // Motion vectors imply depth
            if (inputSummary.requiresMotionVectors)
                inputSummary.requiresDepthTexture = true;

            return inputSummary;
        }

        void CreateCameraRenderTarget(ScriptableRenderContext context, ref RenderTextureDescriptor descriptor, bool primedDepth, CommandBuffer cmd, ref CameraData cameraData)
        {
            using (new ProfilingScope(null, Profiling.createCameraRenderTarget))
            {
                if (m_ColorBufferSystem.PeekBackBuffer() == null || m_ColorBufferSystem.PeekBackBuffer().nameID != BuiltinRenderTextureType.CameraTarget)
                {
                    m_ActiveCameraColorAttachment = m_ColorBufferSystem.GetBackBuffer(cmd);
                    ConfigureCameraColorTarget(m_ActiveCameraColorAttachment);
                    cmd.SetGlobalTexture("_CameraColorTexture", m_ActiveCameraColorAttachment.nameID);
                    //Set _AfterPostProcessTexture, users might still rely on this although it is now always the cameratarget due to swapbuffer
                    cmd.SetGlobalTexture("_AfterPostProcessTexture", m_ActiveCameraColorAttachment.nameID);
                }

                if (m_CameraDepthAttachment == null || m_CameraDepthAttachment.nameID != BuiltinRenderTextureType.CameraTarget)
                {
                    var depthDescriptor = descriptor;
                    depthDescriptor.useMipMap = false;
                    depthDescriptor.autoGenerateMips = false;
                    depthDescriptor.bindMS = false;

                    bool hasMSAA = depthDescriptor.msaaSamples > 1 && (SystemInfo.supportsMultisampledTextures != 0);

                    // if MSAA is enabled and we are not resolving depth, which we only do if the CopyDepthPass is AfterTransparents,
                    // then we want to bind the multisampled surface.
                    if (hasMSAA)
                    {
                        // if depth priming is enabled the copy depth primed pass is meant to do the MSAA resolve, so we want to bind the MS surface
                        if (IsDepthPrimingEnabled(ref cameraData))
                            depthDescriptor.bindMS = true;
                        else
                            depthDescriptor.bindMS = !(RenderingUtils.MultisampleDepthResolveSupported() && m_CopyDepthMode == CopyDepthMode.AfterTransparents);
                    }

                    // binding MS surfaces is not supported by the GLES backend, and it won't be fixed after investigating
                    // the high performance impact of potential fixes, which would make it more expensive than depth prepass (fogbugz 1339401 for more info)
                    if (IsGLESDevice())
                        depthDescriptor.bindMS = false;

                    depthDescriptor.graphicsFormat = GraphicsFormat.None;
                    depthDescriptor.depthStencilFormat = k_DepthStencilFormat;
                    RenderingUtils.ReAllocateIfNeeded(ref m_CameraDepthAttachment, depthDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_CameraDepthAttachment");
                    cmd.SetGlobalTexture(m_CameraDepthAttachment.name, m_CameraDepthAttachment.nameID);
                }
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }

        bool PlatformRequiresExplicitMsaaResolve()
        {
#if UNITY_EDITOR
            // In the editor play-mode we use a Game View Render Texture, with
            // samples count forced to 1 so we always need to do an explicit MSAA resolve.
            return true;
#else
            // On Metal/iOS the MSAA resolve is done implicitly as part of the renderpass, so we do not need an extra intermediate pass for the explicit autoresolve.
            // Note: On Vulkan Standalone, despite SystemInfo.supportsMultisampleAutoResolve being true, the backbuffer has only 1 sample, so we still require
            // the explicit resolve on non-mobile platforms with supportsMultisampleAutoResolve.
            return !(SystemInfo.supportsMultisampleAutoResolve && Application.isMobilePlatform)
                && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal;
#endif
        }

        /// <summary>
        /// Checks if the pipeline needs to create a intermediate render texture.
        /// </summary>
        /// <param name="cameraData">CameraData contains all relevant render target information for the camera.</param>
        /// <seealso cref="CameraData"/>
        /// <returns>Return true if pipeline needs to render to a intermediate render texture.</returns>
        bool RequiresIntermediateColorTexture(ref CameraData cameraData)
        {
            // When rendering a camera stack we always create an intermediate render texture to composite camera results.
            // We create it upon rendering the Base camera.
            if (cameraData.renderType == CameraRenderType.Base && !cameraData.resolveFinalTarget)
                return true;

            // Always force rendering into intermediate color texture if deferred rendering mode is selected.
            // Reason: without intermediate color texture, the target camera texture is y-flipped.
            // However, the target camera texture is bound during gbuffer pass and deferred pass.
            // Gbuffer pass will not be y-flipped because it is MRT (see ScriptableRenderContext implementation),
            // while deferred pass will be y-flipped, which breaks rendering.
            // This incurs an extra blit into at the end of rendering.
            if (this.renderingModeActual == RenderingMode.Deferred)
                return true;

            bool isSceneViewCamera = cameraData.isSceneViewCamera;
            var cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
            int msaaSamples = cameraTargetDescriptor.msaaSamples;
            bool isScaledRender = cameraData.imageScalingMode != ImageScalingMode.None;
            bool isCompatibleBackbufferTextureDimension = cameraTargetDescriptor.dimension == TextureDimension.Tex2D;
            bool requiresExplicitMsaaResolve = msaaSamples > 1 && PlatformRequiresExplicitMsaaResolve();
            bool isOffscreenRender = cameraData.targetTexture != null && !isSceneViewCamera;
            bool isCapturing = cameraData.captureActions != null;

            bool postProcessEnabled = cameraData.postProcessEnabled && m_PostProcessPasses.isCreated;
            bool requiresBlitForOffscreenCamera = postProcessEnabled || cameraData.requiresOpaqueTexture || requiresExplicitMsaaResolve || !cameraData.isDefaultViewport;
            if (isOffscreenRender)
                return requiresBlitForOffscreenCamera;

            return requiresBlitForOffscreenCamera || isSceneViewCamera || isScaledRender || cameraData.isHdrEnabled ||
                !isCompatibleBackbufferTextureDimension || isCapturing || cameraData.requireSrgbConversion;
        }

        bool CanCopyDepth(ref CameraData cameraData)
        {
            bool msaaEnabledForCamera = cameraData.cameraTargetDescriptor.msaaSamples > 1;
            bool supportsTextureCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None;
            bool supportsDepthTarget = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.Depth);
            bool supportsDepthCopy = !msaaEnabledForCamera && (supportsDepthTarget || supportsTextureCopy);

            bool msaaDepthResolve = msaaEnabledForCamera && SystemInfo.supportsMultisampledTextures != 0;

            // copying MSAA depth on GLES3 is giving invalid results. This won't be fixed by the driver team because it would introduce performance issues (more info in the Fogbugz issue 1339401 comments)
            if (IsGLESDevice() && msaaDepthResolve)
                return false;

            return supportsDepthCopy || msaaDepthResolve;
        }

        internal override void SwapColorBuffer(CommandBuffer cmd)
        {
            m_ColorBufferSystem.Swap();

            //Check if we are using the depth that is attached to color buffer
            if (m_ActiveCameraDepthAttachment.nameID != BuiltinRenderTextureType.CameraTarget)
                ConfigureCameraTarget(m_ColorBufferSystem.GetBackBuffer(cmd), m_ColorBufferSystem.GetBufferA());
            else
                ConfigureCameraColorTarget(m_ColorBufferSystem.GetBackBuffer(cmd));

            m_ActiveCameraColorAttachment = m_ColorBufferSystem.GetBackBuffer(cmd);
            cmd.SetGlobalTexture("_CameraColorTexture", m_ActiveCameraColorAttachment.nameID);
            //Set _AfterPostProcessTexture, users might still rely on this although it is now always the cameratarget due to swapbuffer
            cmd.SetGlobalTexture("_AfterPostProcessTexture", m_ActiveCameraColorAttachment.nameID);
        }

        internal override RTHandle GetCameraColorFrontBuffer(CommandBuffer cmd)
        {
            return m_ColorBufferSystem.GetFrontBuffer(cmd);
        }

        internal override RTHandle GetCameraColorBackBuffer(CommandBuffer cmd)
        {
            return m_ColorBufferSystem.GetBackBuffer(cmd);
        }

        internal override void EnableSwapBufferMSAA(bool enable)
        {
            m_ColorBufferSystem.EnableMSAA(enable);
        }
    }
}
