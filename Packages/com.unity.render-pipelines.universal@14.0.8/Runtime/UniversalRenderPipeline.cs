using System;
using Unity.Collections;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Rendering.Universal;
#endif
using UnityEngine.Scripting.APIUpdating;
using Lightmapping = UnityEngine.Experimental.GlobalIllumination.Lightmapping;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Profiling;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// The main class for the Universal Render Pipeline (URP).
    /// </summary>
    public sealed partial class UniversalRenderPipeline : RenderPipeline
    {
        /// <summary>
        /// The shader tag used in the Universal Render Pipeline (URP)
        /// </summary>
        public const string k_ShaderTagName = "UniversalPipeline";

        private static class Profiling
        {
            private static Dictionary<int, ProfilingSampler> s_HashSamplerCache = new Dictionary<int, ProfilingSampler>();
            public static readonly ProfilingSampler unknownSampler = new ProfilingSampler("Unknown");

            // Specialization for camera loop to avoid allocations.
            public static ProfilingSampler TryGetOrAddCameraSampler(Camera camera)
            {
#if UNIVERSAL_PROFILING_NO_ALLOC
                return unknownSampler;
#else
                ProfilingSampler ps = null;
                int cameraId = camera.GetHashCode();
                bool exists = s_HashSamplerCache.TryGetValue(cameraId, out ps);
                if (!exists)
                {
                    // NOTE: camera.name allocates!
                    ps = new ProfilingSampler($"{nameof(UniversalRenderPipeline)}.{nameof(RenderSingleCameraInternal)}: {camera.name}");
                    s_HashSamplerCache.Add(cameraId, ps);
                }
                return ps;
#endif
            }

            public static class Pipeline
            {
                // TODO: Would be better to add Profiling name hooks into RenderPipeline.cs, requires changes outside of Universal.
#if UNITY_2021_1_OR_NEWER
                public static readonly ProfilingSampler beginContextRendering  = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(BeginContextRendering)}");
                public static readonly ProfilingSampler endContextRendering    = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(EndContextRendering)}");
#else
                public static readonly ProfilingSampler beginFrameRendering = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(BeginFrameRendering)}");
                public static readonly ProfilingSampler endFrameRendering = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(EndFrameRendering)}");
#endif
                public static readonly ProfilingSampler beginCameraRendering = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(BeginCameraRendering)}");
                public static readonly ProfilingSampler endCameraRendering = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(EndCameraRendering)}");

                const string k_Name = nameof(UniversalRenderPipeline);
                public static readonly ProfilingSampler initializeCameraData = new ProfilingSampler($"{k_Name}.{nameof(InitializeCameraData)}");
                public static readonly ProfilingSampler initializeStackedCameraData = new ProfilingSampler($"{k_Name}.{nameof(InitializeStackedCameraData)}");
                public static readonly ProfilingSampler initializeAdditionalCameraData = new ProfilingSampler($"{k_Name}.{nameof(InitializeAdditionalCameraData)}");
                public static readonly ProfilingSampler initializeRenderingData = new ProfilingSampler($"{k_Name}.{nameof(InitializeRenderingData)}");
                public static readonly ProfilingSampler initializeShadowData = new ProfilingSampler($"{k_Name}.{nameof(InitializeShadowData)}");
                public static readonly ProfilingSampler initializeLightData = new ProfilingSampler($"{k_Name}.{nameof(InitializeLightData)}");
                public static readonly ProfilingSampler getPerObjectLightFlags = new ProfilingSampler($"{k_Name}.{nameof(GetPerObjectLightFlags)}");
                public static readonly ProfilingSampler getMainLightIndex = new ProfilingSampler($"{k_Name}.{nameof(GetMainLightIndex)}");
                public static readonly ProfilingSampler setupPerFrameShaderConstants = new ProfilingSampler($"{k_Name}.{nameof(SetupPerFrameShaderConstants)}");
                public static readonly ProfilingSampler setupPerCameraShaderConstants = new ProfilingSampler($"{k_Name}.{nameof(SetupPerCameraShaderConstants)}");

                public static class Renderer
                {
                    const string k_Name = nameof(ScriptableRenderer);
                    public static readonly ProfilingSampler setupCullingParameters = new ProfilingSampler($"{k_Name}.{nameof(ScriptableRenderer.SetupCullingParameters)}");
                    public static readonly ProfilingSampler setup = new ProfilingSampler($"{k_Name}.{nameof(ScriptableRenderer.Setup)}");
                };

                public static class Context
                {
                    const string k_Name = nameof(ScriptableRenderContext);
                    public static readonly ProfilingSampler submit = new ProfilingSampler($"{k_Name}.{nameof(ScriptableRenderContext.Submit)}");
                };
            };
        }

        /// <summary>
        /// The maximum amount of bias allowed for shadows.
        /// </summary>
        public static float maxShadowBias
        {
            get => 10.0f;
        }

        /// <summary>
        /// The minimum value allowed for render scale.
        /// </summary>
        public static float minRenderScale
        {
            get => 0.1f;
        }

        /// <summary>
        /// The maximum value allowed for render scale.
        /// </summary>
        public static float maxRenderScale
        {
            get => 2.0f;
        }

        /// <summary>
        /// The max number of iterations allowed calculating enclosing sphere.
        /// </summary>
        public static int maxNumIterationsEnclosingSphere
        {
            get => 1000;
        }

        /// <summary>
        /// The max number of lights that can be shaded per object (in the for loop in the shader).
        /// </summary>
        public static int maxPerObjectLights
        {
            // No support to bitfield mask and int[] in gles2. Can't index fast more than 4 lights.
            // Check Lighting.hlsl for more details.
            get => (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2) ? 4 : 8;
        }

        // These limits have to match same limits in Input.hlsl
        internal const int k_MaxVisibleAdditionalLightsMobileShaderLevelLessThan45 = 16;
        internal const int k_MaxVisibleAdditionalLightsMobile = 32;
        internal const int k_MaxVisibleAdditionalLightsNonMobile = 256;

        /// <summary>
        /// The max number of additional lights that can can affect each GameObject.
        /// </summary>
        public static int maxVisibleAdditionalLights
        {
            get
            {
                // Must match: Input.hlsl, MAX_VISIBLE_LIGHTS
                bool isMobile = GraphicsSettings.HasShaderDefine(BuiltinShaderDefine.SHADER_API_MOBILE);
                if (isMobile && (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 || (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && Graphics.minOpenGLESVersion <= OpenGLESVersion.OpenGLES30)))
                    return k_MaxVisibleAdditionalLightsMobileShaderLevelLessThan45;

                // GLES can be selected as platform on Windows (not a mobile platform) but uniform buffer size so we must use a low light count.
                return (isMobile || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3)
                    ? k_MaxVisibleAdditionalLightsMobile : k_MaxVisibleAdditionalLightsNonMobile;
            }
        }

        // Match with values in Input.hlsl
        internal static int lightsPerTile => ((maxVisibleAdditionalLights + 31) / 32) * 32;
        internal static int maxZBinWords => 1024 * 4;
        internal static int maxTileWords => (maxVisibleAdditionalLights <= 32 ? 1024 : 4096) * 4;
        internal static int maxVisibleReflectionProbes => Math.Min(maxVisibleAdditionalLights, 64);

        internal const int k_DefaultRenderingLayerMask = 0x00000001;
        private readonly DebugDisplaySettingsUI m_DebugDisplaySettingsUI = new DebugDisplaySettingsUI();

        private UniversalRenderPipelineGlobalSettings m_GlobalSettings;

        /// <summary>
        /// The default Render Pipeline Global Settings.
        /// </summary>
        public override RenderPipelineGlobalSettings defaultSettings => m_GlobalSettings;

        // flag to keep track of depth buffer requirements by any of the cameras in the stack
        internal static bool cameraStackRequiresDepthForPostprocessing = false;

        internal static RenderGraph s_RenderGraph;
        internal static RTHandleResourcePool s_RTHandlePool;

        private static bool useRenderGraph;

        // Reference to the asset associated with the pipeline.
        // When a pipeline asset is switched in `GraphicsSettings`, the `UniversalRenderPipelineCore.asset` member
        // becomes unreliable for the purpose of pipeline and renderer clean-up in the `Dispose` call from
        // `RenderPipelineManager.CleanupRenderPipeline`.
        // This field provides the correct reference for the purpose of cleaning up the renderers on this pipeline
        // asset.
        private readonly UniversalRenderPipelineAsset pipelineAsset;

        /// <inheritdoc/>
        public override string ToString() => pipelineAsset?.ToString();

        /// <summary>
        /// Creates a new <c>UniversalRenderPipeline</c> instance.
        /// </summary>
        /// <param name="asset">The <c>UniversalRenderPipelineAsset</c> asset to initialize the pipeline.</param>
        /// <seealso cref="RenderPassEvent"/>
        public UniversalRenderPipeline(UniversalRenderPipelineAsset asset)
        {
            pipelineAsset = asset;
#if UNITY_EDITOR
            m_GlobalSettings = UniversalRenderPipelineGlobalSettings.Ensure();
#else
            m_GlobalSettings = UniversalRenderPipelineGlobalSettings.instance;
#endif
            SetSupportedRenderingFeatures(pipelineAsset);

            // Initial state of the RTHandle system.
            // We initialize to screen width/height to avoid multiple realloc that can lead to inflated memory usage (as releasing of memory is delayed).
            // Note: Use legacy DR control. Can be removed once URP integrates with core package DynamicResolutionHandler
            RTHandles.Initialize(Screen.width, Screen.height, useLegacyDynamicResControl: true);

            GraphicsSettings.useScriptableRenderPipelineBatching = asset.useSRPBatcher;

            // In QualitySettings.antiAliasing disabled state uses value 0, where in URP 1
            int qualitySettingsMsaaSampleCount = QualitySettings.antiAliasing > 0 ? QualitySettings.antiAliasing : 1;
            bool msaaSampleCountNeedsUpdate = qualitySettingsMsaaSampleCount != asset.msaaSampleCount;

            // Let engine know we have MSAA on for cases where we support MSAA backbuffer
            if (msaaSampleCountNeedsUpdate)
            {
                QualitySettings.antiAliasing = asset.msaaSampleCount;
            }


            // Configure initial XR settings
            MSAASamples msaaSamples = (MSAASamples)Mathf.Clamp(Mathf.NextPowerOfTwo(QualitySettings.antiAliasing), (int)MSAASamples.None, (int)MSAASamples.MSAA8x);
            XRSystem.SetDisplayMSAASamples(msaaSamples);
            XRSystem.SetRenderScale(asset.renderScale);

            Shader.globalRenderPipeline = k_ShaderTagName;

            Lightmapping.SetDelegate(lightsDelegate);

            CameraCaptureBridge.enabled = true;

            RenderingUtils.ClearSystemInfoCache();

            DecalProjector.defaultMaterial = asset.decalMaterial;

            s_RenderGraph = new RenderGraph("URPRenderGraph");
            useRenderGraph = false;

            s_RTHandlePool = new RTHandleResourcePool();

            DebugManager.instance.RefreshEditor();
            m_DebugDisplaySettingsUI.RegisterDebug(UniversalRenderPipelineDebugDisplaySettings.Instance);

            QualitySettings.enableLODCrossFade = asset.enableLODCrossFade;
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            m_DebugDisplaySettingsUI.UnregisterDebug();

            Blitter.Cleanup();

            base.Dispose(disposing);

            pipelineAsset.DestroyRenderers();

            Shader.globalRenderPipeline = string.Empty;

            SupportedRenderingFeatures.active = new SupportedRenderingFeatures();
            ShaderData.instance.Dispose();
            XRSystem.Dispose();

            s_RenderGraph.Cleanup();
            s_RenderGraph = null;

            s_RTHandlePool.Cleanup();
            s_RTHandlePool = null;
#if UNITY_EDITOR
            SceneViewDrawMode.ResetDrawMode();
#endif
            Lightmapping.ResetDelegate();
            CameraCaptureBridge.enabled = false;

            DisposeAdditionalCameraData();
        }

        // If the URP gets destroyed, we must clean up all the added URP specific camera data and
        // non-GC resources to avoid leaking them.
        private void DisposeAdditionalCameraData()
        {
            foreach (var c in Camera.allCameras)
            {
                if (c.TryGetComponent<UniversalAdditionalCameraData>(out var acd))
                {
                    acd.taaPersistentData?.DeallocateTargets();
                };
            }
        }


        /// <inheritdoc/>
        protected override void Render(ScriptableRenderContext renderContext, Camera[] cameras)
        {
            Render(renderContext, new List<Camera>(cameras));
        }

        /// <inheritdoc/>
        protected override void Render(ScriptableRenderContext renderContext, List<Camera> cameras)
        {
            SetHDRState(cameras);

            // When HDR is active we render UI overlay per camera as we want all UI to be calibrated to white paper inside a single pass
            // for performance reasons otherwise we render UI overlay after all camera
            SupportedRenderingFeatures.active.rendersUIOverlay = HDROutputIsActive();

            // TODO: Would be better to add Profiling name hooks into RenderPipelineManager.
            // C#8 feature, only in >= 2020.2
            using var profScope = new ProfilingScope(null, ProfilingSampler.Get(URPProfileId.UniversalRenderTotal));
            
            using (new ProfilingScope(null, Profiling.Pipeline.beginContextRendering))
            {
                BeginContextRendering(renderContext, cameras);
            }

            GraphicsSettings.lightsUseLinearIntensity = (QualitySettings.activeColorSpace == ColorSpace.Linear);
            GraphicsSettings.lightsUseColorTemperature = true;
            GraphicsSettings.defaultRenderingLayerMask = k_DefaultRenderingLayerMask;
            SetupPerFrameShaderConstants();
            XRSystem.SetDisplayMSAASamples((MSAASamples)asset.msaaSampleCount);

#if UNITY_EDITOR
            // We do not want to start rendering if URP global settings are not ready (m_globalSettings is null)
            // or been deleted/moved (m_globalSettings is not necessarily null)
            if (m_GlobalSettings == null || UniversalRenderPipelineGlobalSettings.instance == null)
            {
                m_GlobalSettings = UniversalRenderPipelineGlobalSettings.Ensure();
                if(m_GlobalSettings == null) return;
            }
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (DebugManager.instance.isAnyDebugUIActive)
                UniversalRenderPipelineDebugDisplaySettings.Instance.UpdateFrameTiming();
#endif

            SortCameras(cameras);
            
            for (int i = 0; i < cameras.Count; ++i)
            {
                var camera = cameras[i];
                if (IsGameCamera(camera))
                {
                    RenderCameraStack(renderContext, camera);
                }
                else
                {
                    using (new ProfilingScope(null, Profiling.Pipeline.beginCameraRendering))
                    {
                        BeginCameraRendering(renderContext, camera);
                    }
                    UpdateVolumeFramework(camera, null);

                    RenderSingleCameraInternal(renderContext, camera);

                    using (new ProfilingScope(null, Profiling.Pipeline.endCameraRendering))
                    {
                        EndCameraRendering(renderContext, camera);
                    }
                }
            }

            s_RenderGraph.EndFrame();
            s_RTHandlePool.PurgeUnusedResources(Time.frameCount);
            
            using (new ProfilingScope(null, Profiling.Pipeline.endContextRendering))
            {
                EndContextRendering(renderContext, cameras);
            }

            //不太明白做啥的，可能是DebugShader的？Shader啥时候能打印了？？？
#if ENABLE_SHADER_DEBUG_PRINT
            ShaderDebugPrintManager.instance.EndFrame();
#endif
        }

        /// <summary>
        /// Check whether RenderRequest is supported
        /// </summary>
        /// <param name="camera"></param>
        /// <param name="data"></param>
        /// <typeparam name="RequestData"></typeparam>
        /// <returns></returns>
        protected override bool IsRenderRequestSupported<RequestData>(Camera camera, RequestData data)
        {
            if (data is StandardRequest)
                return true;
            else if (data is SingleCameraRequest)
                return true;

            return false;
        }

        /// <summary>
        /// Process a render request
        /// </summary>
        /// <param name="context"></param>
        /// <param name="camera"></param>
        /// <param name="renderRequest"></param>
        /// <typeparam name="RequestData"></typeparam>
        protected override void ProcessRenderRequests<RequestData>(ScriptableRenderContext context, Camera camera, RequestData renderRequest)
        {
            StandardRequest standardRequest = renderRequest as StandardRequest;
            SingleCameraRequest singleRequest = renderRequest as SingleCameraRequest;

            if(standardRequest != null || singleRequest != null)
            {
                RenderTexture destination = standardRequest != null ? standardRequest.destination : singleRequest.destination;
                int mipLevel = standardRequest != null ? standardRequest.mipLevel : singleRequest.mipLevel;
                int slice = standardRequest != null ? standardRequest.slice : singleRequest.slice;
                int face = standardRequest != null ? (int)standardRequest.face : (int)singleRequest.face;

                //store data that will be changed
                var orignalTarget = camera.targetTexture;

                //set data
                RenderTexture temporaryRT = null;
                RenderTextureDescriptor RTDesc = destination.descriptor;
                //need to set use default constructor of RenderTextureDescriptor which doesn't enable allowVerticalFlip which matters for cubemaps.
                if (destination.dimension == TextureDimension.Cube)
                    RTDesc = new RenderTextureDescriptor();

                RTDesc.colorFormat = destination.format;
                RTDesc.volumeDepth = 1;
                RTDesc.msaaSamples = destination.descriptor.msaaSamples;
                RTDesc.dimension = TextureDimension.Tex2D;
                RTDesc.width = destination.width / (int)Math.Pow(2, mipLevel);
                RTDesc.height = destination.height / (int)Math.Pow(2, mipLevel);
                RTDesc.width = Mathf.Max(1, RTDesc.width);
                RTDesc.height = Mathf.Max(1, RTDesc.height);

                //if mip is 0 and target is Texture2D we can immediately render to the requested destination
                if(destination.dimension != TextureDimension.Tex2D || mipLevel != 0)
                {
                    temporaryRT = RenderTexture.GetTemporary(RTDesc);
                }

                camera.targetTexture = temporaryRT ? temporaryRT : destination;

                if (standardRequest != null)
                {
                    Render(context, new Camera[] { camera });
                }
                else
                {
                    RenderSingleCameraInternal(context, camera);
                }

                if(temporaryRT)
                {
                    switch(destination.dimension)
                    {
                        case TextureDimension.Tex2D:
                        case TextureDimension.Tex2DArray:
                        case TextureDimension.Tex3D:
                            Graphics.CopyTexture(temporaryRT, 0, 0, destination, slice, mipLevel);
                            break;
                        case TextureDimension.Cube:
                        case TextureDimension.CubeArray:
                            Graphics.CopyTexture(temporaryRT, 0, 0, destination, face + slice * 6, mipLevel);
                            break;
                    }
                }

                //restore data
                camera.targetTexture = orignalTarget;
                Graphics.SetRenderTarget(orignalTarget);
                RenderTexture.ReleaseTemporary(temporaryRT);
            }
            else
            {
                Debug.LogWarning("The given RenderRequest type: " + typeof(RequestData).FullName  + ", is either invalid or unsupported by the current pipeline");
            }
        }

        /// <summary>
        /// Standalone camera rendering. Use this to render procedural cameras.
        /// This method doesn't call <c>BeginCameraRendering</c> and <c>EndCameraRendering</c> callbacks.
        /// </summary>
        /// <param name="context">Render context used to record commands during execution.</param>
        /// <param name="camera">Camera to render.</param>
        /// <seealso cref="ScriptableRenderContext"/>
        [Obsolete("RenderSingleCamera is obsolete, please use RenderPipeline.SubmitRenderRequest with UniversalRenderer.SingleCameraRequest as RequestData type", false)]
        public static void RenderSingleCamera(ScriptableRenderContext context, Camera camera)
        {
            RenderSingleCameraInternal(context, camera);
        }

        internal static void RenderSingleCameraInternal(ScriptableRenderContext context, Camera camera)
        {
            UniversalAdditionalCameraData additionalCameraData = null;
            if (IsGameCamera(camera))
                camera.gameObject.TryGetComponent(out additionalCameraData);

            if (additionalCameraData != null && additionalCameraData.renderType != CameraRenderType.Base)
            {
                Debug.LogWarning("Only Base cameras can be rendered with standalone RenderSingleCamera. Camera will be skipped.");
                return;
            }

            InitializeCameraData(camera, additionalCameraData, true, out var cameraData);
            InitializeAdditionalCameraData(camera, additionalCameraData, true, ref cameraData);
#if ADAPTIVE_PERFORMANCE_2_0_0_OR_NEWER
            if (asset.useAdaptivePerformance)
                ApplyAdaptivePerformance(ref cameraData);
#endif
            RenderSingleCamera(context, ref cameraData, cameraData.postProcessEnabled);
        }

        static bool TryGetCullingParameters(CameraData cameraData, out ScriptableCullingParameters cullingParams)
        {

            return cameraData.camera.TryGetCullingParameters(false, out cullingParams);
        }

        /// <summary>
        /// Renders a single camera. This method will do culling, setup and execution of the renderer.
        /// </summary>
        /// <param name="context">Render context used to record commands during execution.</param>
        /// <param name="cameraData">Camera rendering data. This might contain data inherited from a base camera.</param>
        /// <param name="anyPostProcessingEnabled">True if at least one camera has post-processing enabled in the stack, false otherwise.</param>
        static void RenderSingleCamera(ScriptableRenderContext context, ref CameraData cameraData, bool anyPostProcessingEnabled)
        {
            var renderer = cameraData.renderer;
            if (renderer == null)
                return;
            if (!TryGetCullingParameters(cameraData, out var cullingParameters))
                return;

            ScriptableRenderer.current = renderer;
            
            CommandBuffer cmd = CommandBufferPool.Get();
            renderer.Clear(cameraData.renderType);
            renderer.SetupCullingParameters(ref cullingParameters, ref cameraData);
            
            RTHandles.SetReferenceSize(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
            var cullResults = context.Cull(ref cullingParameters);
            InitializeRenderingData(asset, ref cameraData, ref cullResults, anyPostProcessingEnabled, cmd, out var renderingData);
            //renderer.AddRenderPasses(ref renderingData);
            
            renderer.Setup(context, ref renderingData);
            renderer.Execute(context, ref renderingData);
            CommandBufferPool.Release(cmd);
            
            context.Submit();
            ScriptableRenderer.current = null;
        }
        
        /// <summary>
        /// Renders a camera stack. This method calls RenderSingleCamera for each valid camera in the stack.
        /// The last camera resolves the final target to screen.
        /// </summary>
        /// <param name="context">Render context used to record commands during execution.</param>
        /// <param name="camera">Camera to render.</param>
        static void RenderCameraStack(ScriptableRenderContext context, Camera baseCamera)
        {
            using var profScope = new ProfilingScope(null, ProfilingSampler.Get(URPProfileId.RenderCameraStack));

            baseCamera.TryGetComponent<UniversalAdditionalCameraData>(out var baseCameraAdditionalData);

            // Overlay cameras will be rendered stacked while rendering base cameras
            if (baseCameraAdditionalData != null && baseCameraAdditionalData.renderType == CameraRenderType.Overlay)
                return;
            
            // Post-processing not supported in GLES2.
            bool anyPostProcessingEnabled = baseCameraAdditionalData != null && baseCameraAdditionalData.renderPostProcessing;
            anyPostProcessingEnabled &= SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;
            
            using (new ProfilingScope(null, Profiling.Pipeline.beginCameraRendering))
                BeginCameraRendering(context, baseCamera);

            #region SetCameraData
            CameraData mCameraData = new CameraData();
            //................................原始相机参数.............................................
            mInitializeStackedCameraData(ref mCameraData,baseCamera,baseCameraAdditionalData);
            mCameraData.camera = baseCamera;
            mCameraData.cameraTargetDescriptor = CreateRenderTextureDescriptor(baseCamera,mCameraData.renderScale,
                mCameraData.isHdrEnabled,asset.hdrColorBufferPrecision,asset.msaaSampleCount,Graphics.preserveFramebufferAlpha,
                mCameraData.requiresDepthTexture);
            //................................URP新的相机参数.............................................
            mInitializeAdditionalCameraData(baseCamera, baseCameraAdditionalData, ref mCameraData);
            #endregion
            
            RenderSingleCamera(context, ref mCameraData, anyPostProcessingEnabled);
            
            using (new ProfilingScope(null, Profiling.Pipeline.endCameraRendering))
                EndCameraRendering(context, baseCamera);
        }

        static void UpdateVolumeFramework(Camera camera, UniversalAdditionalCameraData additionalCameraData)
        {
            using var profScope = new ProfilingScope(null, ProfilingSampler.Get(URPProfileId.UpdateVolumeFramework));

            // We update the volume framework for:
            // * All cameras in the editor when not in playmode
            // * scene cameras
            // * cameras with update mode set to EveryFrame
            // * cameras with update mode set to UsePipelineSettings and the URP Asset set to EveryFrame
            bool shouldUpdate = camera.cameraType == CameraType.SceneView;
            shouldUpdate |= additionalCameraData != null && additionalCameraData.requiresVolumeFrameworkUpdate;

#if UNITY_EDITOR
            shouldUpdate |= Application.isPlaying == false;
#endif

            // When we have volume updates per-frame disabled...
            if (!shouldUpdate && additionalCameraData)
            {
                // Create a local volume stack and cache the state if it's null
                if (additionalCameraData.volumeStack == null)
                {
                    camera.UpdateVolumeStack(additionalCameraData);
                }

                VolumeManager.instance.stack = additionalCameraData.volumeStack;
                return;
            }

            // When we want to update the volumes every frame...

            // We destroy the volumeStack in the additional camera data, if present, to make sure
            // it gets recreated and initialized if the update mode gets later changed to ViaScripting...
            if (additionalCameraData && additionalCameraData.volumeStack != null)
            {
                camera.DestroyVolumeStack(additionalCameraData);
            }

            // Get the mask + trigger and update the stack
            camera.GetVolumeLayerMaskAndTrigger(additionalCameraData, out LayerMask layerMask, out Transform trigger);
            VolumeManager.instance.ResetMainStack();
            VolumeManager.instance.Update(trigger, layerMask);
        }

        static bool CheckPostProcessForDepth(ref CameraData cameraData)
        {
            if (!cameraData.postProcessEnabled)
                return false;

            if ((cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing || cameraData.IsTemporalAAEnabled())
                && cameraData.renderType == CameraRenderType.Base)
                return true;

            return CheckPostProcessForDepth();
        }

        static bool CheckPostProcessForDepth()
        {
            var stack = VolumeManager.instance.stack;

            if (stack.GetComponent<DepthOfField>().IsActive())
                return true;

            if (stack.GetComponent<MotionBlur>().IsActive())
                return true;

            return false;
        }

        static void SetSupportedRenderingFeatures(UniversalRenderPipelineAsset pipelineAsset)
        {
#if UNITY_EDITOR
            SupportedRenderingFeatures.active = new SupportedRenderingFeatures()
            {
                reflectionProbeModes = SupportedRenderingFeatures.ReflectionProbeModes.None,
                defaultMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.Subtractive,
                mixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.Subtractive | SupportedRenderingFeatures.LightmapMixedBakeModes.IndirectOnly | SupportedRenderingFeatures.LightmapMixedBakeModes.Shadowmask,
                lightmapBakeTypes = LightmapBakeType.Baked | LightmapBakeType.Mixed | LightmapBakeType.Realtime,
                lightmapsModes = LightmapsMode.CombinedDirectional | LightmapsMode.NonDirectional,
                lightProbeProxyVolumes = false,
                motionVectors = true,
                receiveShadows = false,
                reflectionProbes = false,
                reflectionProbesBlendDistance = true,
                particleSystemInstancing = true,
                overridesEnableLODCrossFade = true
            };
            SceneViewDrawMode.SetupDrawMode();
#endif

            SupportedRenderingFeatures.active.supportsHDR = pipelineAsset.supportsHDR;
        }

        static void InitializeCameraData(Camera camera, UniversalAdditionalCameraData additionalCameraData, bool resolveFinalTarget, out CameraData cameraData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeCameraData);

            cameraData = new CameraData();
            InitializeStackedCameraData(camera, additionalCameraData, ref cameraData);

            cameraData.camera = camera;

            ///////////////////////////////////////////////////////////////////
            // Descriptor settings                                            /
            ///////////////////////////////////////////////////////////////////

            var renderer = additionalCameraData?.scriptableRenderer;
            bool rendererSupportsMSAA = renderer != null && renderer.supportedRenderingFeatures.msaa;

            int msaaSamples = 1;
            if (camera.allowMSAA && asset.msaaSampleCount > 1 && rendererSupportsMSAA)
                msaaSamples = (camera.targetTexture != null) ? camera.targetTexture.antiAliasing : asset.msaaSampleCount;

            // Use XR's MSAA if camera is XR camera. XR MSAA needs special handle here because it is not per Camera.
            // Multiple cameras could render into the same XR display and they should share the same MSAA level.
            if (cameraData.xrRendering && rendererSupportsMSAA && camera.targetTexture == null)
                msaaSamples = (int)XRSystem.GetDisplayMSAASamples();

            bool needsAlphaChannel = Graphics.preserveFramebufferAlpha;

            cameraData.hdrColorBufferPrecision = asset ? asset.hdrColorBufferPrecision : HDRColorBufferPrecision._32Bits;
            cameraData.cameraTargetDescriptor = CreateRenderTextureDescriptor(camera, cameraData.renderScale,
                cameraData.isHdrEnabled, cameraData.hdrColorBufferPrecision, msaaSamples, needsAlphaChannel, cameraData.requiresOpaqueTexture);
        }

        /// <summary>
        /// Initialize camera data settings common for all cameras in the stack. Overlay cameras will inherit
        /// settings from base camera.
        /// </summary>
        /// <param name="baseCamera">Base camera to inherit settings from.</param>
        /// <param name="baseAdditionalCameraData">Component that contains additional base camera data.</param>
        /// <param name="cameraData">Camera data to initialize setttings.</param>
        static void InitializeStackedCameraData(Camera baseCamera, UniversalAdditionalCameraData baseAdditionalCameraData, ref CameraData cameraData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeStackedCameraData);

            var settings = asset;
            cameraData.targetTexture = baseCamera.targetTexture;
            cameraData.cameraType = baseCamera.cameraType;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            ///////////////////////////////////////////////////////////////////
            // Environment and Post-processing settings                       /
            ///////////////////////////////////////////////////////////////////
            if (isSceneViewCamera)
            {
                cameraData.volumeLayerMask = 1; // "Default"
                cameraData.volumeTrigger = null;
                cameraData.isStopNaNEnabled = false;
                cameraData.isDitheringEnabled = false;
                cameraData.antialiasing = AntialiasingMode.None;
                cameraData.antialiasingQuality = AntialiasingQuality.High;
                cameraData.xrRendering = false;
                cameraData.allowHDROutput = false;
            }
            else if (baseAdditionalCameraData != null)
            {
                cameraData.volumeLayerMask = baseAdditionalCameraData.volumeLayerMask;
                cameraData.volumeTrigger = baseAdditionalCameraData.volumeTrigger == null ? baseCamera.transform : baseAdditionalCameraData.volumeTrigger;
                cameraData.isStopNaNEnabled = baseAdditionalCameraData.stopNaN && SystemInfo.graphicsShaderLevel >= 35;
                cameraData.isDitheringEnabled = baseAdditionalCameraData.dithering;
                cameraData.antialiasing = baseAdditionalCameraData.antialiasing;
                cameraData.antialiasingQuality = baseAdditionalCameraData.antialiasingQuality;
                cameraData.xrRendering = baseAdditionalCameraData.allowXRRendering && XRSystem.displayActive;
                cameraData.allowHDROutput = baseAdditionalCameraData.allowHDROutput;
            }
            else
            {
                cameraData.volumeLayerMask = 1; // "Default"
                cameraData.volumeTrigger = null;
                cameraData.isStopNaNEnabled = false;
                cameraData.isDitheringEnabled = false;
                cameraData.antialiasing = AntialiasingMode.None;
                cameraData.antialiasingQuality = AntialiasingQuality.High;
                cameraData.xrRendering = XRSystem.displayActive;
                cameraData.allowHDROutput = true;
            }

            ///////////////////////////////////////////////////////////////////
            // Settings that control output of the camera                     /
            ///////////////////////////////////////////////////////////////////

            cameraData.isHdrEnabled = baseCamera.allowHDR && settings.supportsHDR;
            cameraData.allowHDROutput &= settings.supportsHDR;

            Rect cameraRect = baseCamera.rect;
            cameraData.pixelRect = baseCamera.pixelRect;
            cameraData.pixelWidth = baseCamera.pixelWidth;
            cameraData.pixelHeight = baseCamera.pixelHeight;
            cameraData.aspectRatio = (float)cameraData.pixelWidth / (float)cameraData.pixelHeight;
            cameraData.isDefaultViewport = (!(Math.Abs(cameraRect.x) > 0.0f || Math.Abs(cameraRect.y) > 0.0f ||
                Math.Abs(cameraRect.width) < 1.0f || Math.Abs(cameraRect.height) < 1.0f));

            bool isScenePreviewOrReflectionCamera = cameraData.cameraType == CameraType.SceneView || cameraData.cameraType == CameraType.Preview || cameraData.cameraType == CameraType.Reflection;

            // Discard variations lesser than kRenderScaleThreshold.
            // Scale is only enabled for gameview.
            const float kRenderScaleThreshold = 0.05f;
            bool disableRenderScale = ((Mathf.Abs(1.0f - settings.renderScale) < kRenderScaleThreshold) || isScenePreviewOrReflectionCamera);
            cameraData.renderScale = disableRenderScale ? 1.0f : settings.renderScale;

            // Convert the upscaling filter selection from the pipeline asset into an image upscaling filter
            cameraData.upscalingFilter = ResolveUpscalingFilterSelection(new Vector2(cameraData.pixelWidth, cameraData.pixelHeight), cameraData.renderScale, settings.upscalingFilter);

            if (cameraData.renderScale > 1.0f)
            {
                cameraData.imageScalingMode = ImageScalingMode.Downscaling;
            }
            else if ((cameraData.renderScale < 1.0f) || (!isScenePreviewOrReflectionCamera && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR)))
            {
                // When FSR is enabled, we still consider 100% render scale an upscaling operation. (This behavior is only intended for game view cameras)
                // This allows us to run the FSR shader passes all the time since they improve visual quality even at 100% scale.

                cameraData.imageScalingMode = ImageScalingMode.Upscaling;
            }
            else
            {
                cameraData.imageScalingMode = ImageScalingMode.None;
            }

            cameraData.fsrOverrideSharpness = settings.fsrOverrideSharpness;
            cameraData.fsrSharpness = settings.fsrSharpness;

            cameraData.xr = XRSystem.emptyPass;
            XRSystem.SetRenderScale(cameraData.renderScale);

            var commonOpaqueFlags = SortingCriteria.CommonOpaque;
            var noFrontToBackOpaqueFlags = SortingCriteria.SortingLayer | SortingCriteria.RenderQueue | SortingCriteria.OptimizeStateChanges | SortingCriteria.CanvasOrder;
            bool hasHSRGPU = SystemInfo.hasHiddenSurfaceRemovalOnGPU;
            bool canSkipFrontToBackSorting = (baseCamera.opaqueSortMode == OpaqueSortMode.Default && hasHSRGPU) || baseCamera.opaqueSortMode == OpaqueSortMode.NoDistanceSort;

            cameraData.defaultOpaqueSortFlags = canSkipFrontToBackSorting ? noFrontToBackOpaqueFlags : commonOpaqueFlags;
            cameraData.captureActions = CameraCaptureBridge.GetCaptureActions(baseCamera);
        }
        
        static void mInitializeStackedCameraData(ref CameraData mCameraData,Camera baseCamera,UniversalAdditionalCameraData baseCameraAdditionalData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeStackedCameraData);
            
            mCameraData.targetTexture = baseCamera.targetTexture;
            mCameraData.cameraType = baseCamera.cameraType;
            //..........................Volume.......................................
            mCameraData.volumeLayerMask = baseCameraAdditionalData.volumeLayerMask;
            mCameraData.volumeTrigger = baseCameraAdditionalData.volumeTrigger == null ? baseCamera.transform : baseCameraAdditionalData.volumeTrigger;
            //..........................抗锯齿.......................................
            mCameraData.antialiasing = baseCameraAdditionalData.antialiasing;
            mCameraData.antialiasingQuality = baseCameraAdditionalData.antialiasingQuality;
            //..........................HDR.......................................
            mCameraData.allowHDROutput = baseCameraAdditionalData.allowHDROutput;
            mCameraData.isHdrEnabled = baseCamera.allowHDR && asset.supportsHDR;
            mCameraData.allowHDROutput &= asset.supportsHDR;
            //..........................Others.......................................
            mCameraData.isStopNaNEnabled = baseCameraAdditionalData.stopNaN && SystemInfo.graphicsShaderLevel >= 35;
            mCameraData.isDitheringEnabled = baseCameraAdditionalData.dithering;
            
            //..........................Camera View Rect.......................................
            Rect cameraRect = baseCamera.rect;
            mCameraData.pixelRect = baseCamera.pixelRect;
            mCameraData.pixelWidth = baseCamera.pixelWidth;
            mCameraData.pixelHeight = baseCamera.pixelHeight;
            mCameraData.aspectRatio = (float)mCameraData.pixelWidth / (float)mCameraData.pixelHeight;
            mCameraData.isDefaultViewport = (!(Math.Abs(cameraRect.x) > 0.0f || Math.Abs(cameraRect.y) > 0.0f ||
                                               Math.Abs(cameraRect.width) < 1.0f || Math.Abs(cameraRect.height) < 1.0f));
            //.....................................................................................
            bool isScenePreviewOrReflectionCamera =  mCameraData.cameraType == CameraType.SceneView 
                                                     ||  mCameraData.cameraType == CameraType.Preview 
                                                     ||  mCameraData.cameraType == CameraType.Reflection;
            //..........................Camera renderScale.......................................
            const float kRenderScaleThreshold = 0.05f;
            bool disableRenderScale = ((Mathf.Abs(1.0f - asset.renderScale) < kRenderScaleThreshold) || isScenePreviewOrReflectionCamera);
            mCameraData.renderScale = disableRenderScale ? 1.0f : asset.renderScale;
            //..........................Set Filter.......................................
            // Convert the upscaling filter selection from the pipeline asset into an image upscaling filter
            mCameraData.upscalingFilter = ResolveUpscalingFilterSelection(
                new Vector2(mCameraData.pixelWidth, mCameraData.pixelHeight), mCameraData.renderScale, asset.upscalingFilter);
            //..........................绘制Scale大于1或者小于1等，设置ImageScalingMode，放大缩小等不同情况采用不同策略.......................................
            if (mCameraData.renderScale > 1.0f)
            {
                mCameraData.imageScalingMode = ImageScalingMode.Downscaling;
            }
            else if ((mCameraData.renderScale < 1.0f) || (!isScenePreviewOrReflectionCamera && 
                                                          (mCameraData.upscalingFilter == ImageUpscalingFilter.FSR)))
            {
                // When FSR is enabled, we still consider 100% render scale an upscaling operation. (This behavior is only intended for game view cameras)
                // This allows us to run the FSR shader passes all the time since they improve visual quality even at 100% scale.
                mCameraData.imageScalingMode = ImageScalingMode.Upscaling;
            }
            else
            {
                mCameraData.imageScalingMode = ImageScalingMode.None;
            }
            
            mCameraData.fsrOverrideSharpness = asset.fsrOverrideSharpness;
            mCameraData.fsrSharpness = asset.fsrSharpness;

            mCameraData.defaultOpaqueSortFlags = SortingCriteria.CommonOpaque;
            mCameraData.captureActions = CameraCaptureBridge.GetCaptureActions(baseCamera);
        }

        /// <summary>
        /// Initialize settings that can be different for each camera in the stack.
        /// </summary>
        /// <param name="camera">Camera to initialize settings from.</param>
        /// <param name="additionalCameraData">Additional camera data component to initialize settings from.</param>
        /// <param name="resolveFinalTarget">True if this is the last camera in the stack and rendering should resolve to camera target.</param>
        /// <param name="cameraData">Settings to be initilized.</param>
        static void InitializeAdditionalCameraData(Camera camera, UniversalAdditionalCameraData additionalCameraData, bool resolveFinalTarget, ref CameraData cameraData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeAdditionalCameraData);

            var settings = asset;

            bool anyShadowsEnabled = settings.supportsMainLightShadows || settings.supportsAdditionalLightShadows;
            cameraData.maxShadowDistance = Mathf.Min(settings.shadowDistance, camera.farClipPlane);
            cameraData.maxShadowDistance = (anyShadowsEnabled && cameraData.maxShadowDistance >= camera.nearClipPlane) ? cameraData.maxShadowDistance : 0.0f;

            bool isSceneViewCamera = cameraData.isSceneViewCamera;
            if (isSceneViewCamera)
            {
                cameraData.renderType = CameraRenderType.Base;
                cameraData.clearDepth = true;
                cameraData.postProcessEnabled = CoreUtils.ArePostProcessesEnabled(camera);
                cameraData.requiresDepthTexture = settings.supportsCameraDepthTexture;
                cameraData.requiresOpaqueTexture = settings.supportsCameraOpaqueTexture;
                cameraData.renderer = asset.scriptableRenderer;
                cameraData.useScreenCoordOverride = false;
                cameraData.screenSizeOverride = cameraData.pixelRect.size;
                cameraData.screenCoordScaleBias = Vector2.one;
            }
            else if (additionalCameraData != null)
            {
                cameraData.renderType = additionalCameraData.renderType;
                cameraData.clearDepth = (additionalCameraData.renderType != CameraRenderType.Base) ? additionalCameraData.clearDepth : true;
                cameraData.postProcessEnabled = additionalCameraData.renderPostProcessing;
                cameraData.maxShadowDistance = (additionalCameraData.renderShadows) ? cameraData.maxShadowDistance : 0.0f;
                cameraData.requiresDepthTexture = additionalCameraData.requiresDepthTexture;
                cameraData.requiresOpaqueTexture = additionalCameraData.requiresColorTexture;
                cameraData.renderer = additionalCameraData.scriptableRenderer;
                cameraData.useScreenCoordOverride = additionalCameraData.useScreenCoordOverride;
                cameraData.screenSizeOverride = additionalCameraData.screenSizeOverride;
                cameraData.screenCoordScaleBias = additionalCameraData.screenCoordScaleBias;
            }
            else
            {
                cameraData.renderType = CameraRenderType.Base;
                cameraData.clearDepth = true;
                cameraData.postProcessEnabled = false;
                cameraData.requiresDepthTexture = settings.supportsCameraDepthTexture;
                cameraData.requiresOpaqueTexture = settings.supportsCameraOpaqueTexture;
                cameraData.renderer = asset.scriptableRenderer;
                cameraData.useScreenCoordOverride = false;
                cameraData.screenSizeOverride = cameraData.pixelRect.size;
                cameraData.screenCoordScaleBias = Vector2.one;
            }

            // Disables post if GLes2
            cameraData.postProcessEnabled &= SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;

            cameraData.requiresDepthTexture |= isSceneViewCamera;
            cameraData.postProcessingRequiresDepthTexture = CheckPostProcessForDepth(ref cameraData);
            cameraData.resolveFinalTarget = resolveFinalTarget;

            // Disable depth and color copy. We should add it in the renderer instead to avoid performance pitfalls
            // of camera stacking breaking render pass execution implicitly.
            bool isOverlayCamera = (cameraData.renderType == CameraRenderType.Overlay);
            if (isOverlayCamera)
            {
                cameraData.requiresOpaqueTexture = false;
            }

            // NOTE: TAA depends on XR modifications of cameraTargetDescriptor.
            if (additionalCameraData != null)
                UpdateTemporalAAData(ref cameraData, additionalCameraData);

            Matrix4x4 projectionMatrix = camera.projectionMatrix;

            // Overlay cameras inherit viewport from base.
            // If the viewport is different between them we might need to patch the projection to adjust aspect ratio
            // matrix to prevent squishing when rendering objects in overlay cameras.
            if (isOverlayCamera && !camera.orthographic && cameraData.pixelRect != camera.pixelRect)
            {
                // m00 = (cotangent / aspect), therefore m00 * aspect gives us cotangent.
                float cotangent = camera.projectionMatrix.m00 * camera.aspect;

                // Get new m00 by dividing by base camera aspectRatio.
                float newCotangent = cotangent / cameraData.aspectRatio;
                projectionMatrix.m00 = newCotangent;
            }

            // TAA debug settings
            // Affects the jitter set just below. Do not move.
            ApplyTaaRenderingDebugOverrides(ref cameraData.taaSettings);

            // Depends on the cameraTargetDesc, size and MSAA also XR modifications of those.
            Matrix4x4 jitterMat = TemporalAA.CalculateJitterMatrix(ref cameraData);
            cameraData.SetViewProjectionAndJitterMatrix(camera.worldToCameraMatrix, projectionMatrix, jitterMat);

            cameraData.worldSpaceCameraPos = camera.transform.position;

            var backgroundColorSRGB = camera.backgroundColor;
            // Get the background color from preferences if preview camera
#if UNITY_EDITOR
            if (camera.cameraType == CameraType.Preview && camera.clearFlags != CameraClearFlags.SolidColor)
            {
                backgroundColorSRGB = CoreRenderPipelinePreferences.previewBackgroundColor;
            }
#endif

            cameraData.backgroundColor = CoreUtils.ConvertSRGBToActiveColorSpace(backgroundColorSRGB);
        }
        
        static void mInitializeAdditionalCameraData(Camera baseCamera, UniversalAdditionalCameraData baseCameraAdditionalData, ref CameraData mCameraData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeAdditionalCameraData);
            
            mCameraData.maxShadowDistance = Mathf.Min(asset.shadowDistance, baseCamera.farClipPlane);
            mCameraData.maxShadowDistance =  mCameraData.maxShadowDistance >= baseCamera.nearClipPlane ?  mCameraData.maxShadowDistance : 0.0f;
            
            mCameraData.renderType = baseCameraAdditionalData.renderType;
            mCameraData.clearDepth = (baseCameraAdditionalData.renderType != CameraRenderType.Base) ? baseCameraAdditionalData.clearDepth : true;
            mCameraData.postProcessEnabled = baseCameraAdditionalData.renderPostProcessing;
            mCameraData.maxShadowDistance = (baseCameraAdditionalData.renderShadows) ? mCameraData.maxShadowDistance : 0.0f;
            mCameraData.requiresDepthTexture = baseCameraAdditionalData.requiresDepthTexture;
            mCameraData.requiresOpaqueTexture = baseCameraAdditionalData.requiresColorTexture;
            mCameraData.renderer = baseCameraAdditionalData.scriptableRenderer;
            mCameraData.useScreenCoordOverride = baseCameraAdditionalData.useScreenCoordOverride;
            mCameraData.screenSizeOverride = baseCameraAdditionalData.screenSizeOverride;
            mCameraData.screenCoordScaleBias = baseCameraAdditionalData.screenCoordScaleBias;
            
            mCameraData.postProcessingRequiresDepthTexture = CheckPostProcessForDepth(ref mCameraData);
            mCameraData.resolveFinalTarget = true;
            
            mCameraData.SetViewProjectionAndJitterMatrix(baseCamera.worldToCameraMatrix, baseCamera.projectionMatrix, Matrix4x4.identity);
            mCameraData.worldSpaceCameraPos = baseCamera.transform.position;
            
            mCameraData.backgroundColor = CoreUtils.ConvertSRGBToActiveColorSpace(baseCamera.backgroundColor);
        }

        static void InitializeRenderingData(UniversalRenderPipelineAsset settings, ref CameraData cameraData, ref CullingResults cullResults,
            bool anyPostProcessingEnabled, CommandBuffer cmd, out RenderingData renderingData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeRenderingData);
            NativeArray<VisibleLight> visibleLights = cullResults.visibleLights;

            int mainLightIndex = GetMainLightIndex(settings, visibleLights);
            bool mainLightCastShadows = false;
            bool additionalLightsCastShadows = false;

            if (cameraData.maxShadowDistance > 0.0f)
            {
                mainLightCastShadows = (mainLightIndex != -1 && visibleLights[mainLightIndex].light != null &&
                    visibleLights[mainLightIndex].light.shadows != LightShadows.None);
            }

            renderingData.cullResults = cullResults;
            renderingData.cameraData = cameraData;
            InitializeLightData(settings, visibleLights, mainLightIndex, out renderingData.lightData);
            InitializeShadowData(settings, visibleLights, mainLightCastShadows, additionalLightsCastShadows && !renderingData.lightData.shadeAdditionalLightsPerVertex, false, out renderingData.shadowData);
            InitializePostProcessingData(settings, out renderingData.postProcessingData);
            renderingData.supportsDynamicBatching = settings.supportsDynamicBatching;

            renderingData.perObjectData = GetPerObjectLightFlags(renderingData.lightData.additionalLightsCount, false);
            renderingData.postProcessingEnabled = anyPostProcessingEnabled;
            renderingData.commandBuffer = cmd;

            CheckAndApplyDebugSettings(ref renderingData);
        }

        static void InitializeShadowData(UniversalRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights, bool mainLightCastShadows, bool additionalLightsCastShadows, bool isForwardPlus, out ShadowData shadowData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeShadowData);

            m_ShadowBiasData.Clear();
            m_ShadowResolutionData.Clear();

            for (int i = 0; i < visibleLights.Length; ++i)
            {
                ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(i);
                Light light = vl.light;
                UniversalAdditionalLightData data = null;
                if (light != null)
                {
                    light.gameObject.TryGetComponent(out data);
                }

                if (data && !data.usePipelineSettings)
                    m_ShadowBiasData.Add(new Vector4(light.shadowBias, light.shadowNormalBias, 0.0f, 0.0f));
                else
                    m_ShadowBiasData.Add(new Vector4(settings.shadowDepthBias, settings.shadowNormalBias, 0.0f, 0.0f));

                if (data && (data.additionalLightsShadowResolutionTier == 
                             UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierCustom))
                {
                    m_ShadowResolutionData.Add((int)light.shadowResolution); // native code does not clamp light.shadowResolution between -1 and 3
                }
                else if (data && (data.additionalLightsShadowResolutionTier != 
                                  UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierCustom))
                {
                    int resolutionTier = Mathf.Clamp(data.additionalLightsShadowResolutionTier, 
                        UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierLow, 
                        UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierHigh);
                    m_ShadowResolutionData.Add(settings.GetAdditionalLightsShadowResolution(resolutionTier));
                }
                else
                {
                    m_ShadowResolutionData.Add(settings.GetAdditionalLightsShadowResolution(
                        UniversalAdditionalLightData.AdditionalLightsShadowDefaultResolutionTier));
                }
            }

            shadowData.bias = m_ShadowBiasData;
            shadowData.resolution = m_ShadowResolutionData;
            shadowData.mainLightShadowsEnabled = settings.supportsMainLightShadows && settings.mainLightRenderingMode == LightRenderingMode.PerPixel;
            shadowData.supportsMainLightShadows = SystemInfo.supportsShadows && shadowData.mainLightShadowsEnabled && mainLightCastShadows;
           

            // We no longer use screen space shadows in URP.
            // This change allows us to have particles & transparent objects receive shadows.
#pragma warning disable 0618
            shadowData.requiresScreenSpaceShadowResolve = false;
#pragma warning restore 0618

            // On GLES2 we strip the cascade keywords from the lighting shaders, so for consistency we force disable the cascades here too
            shadowData.mainLightShadowCascadesCount = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES2 ? 1 : settings.shadowCascadeCount;
            shadowData.mainLightShadowmapWidth = settings.mainLightShadowmapResolution;
            shadowData.mainLightShadowmapHeight = settings.mainLightShadowmapResolution;

            switch (shadowData.mainLightShadowCascadesCount)
            {
                case 1:
                    shadowData.mainLightShadowCascadesSplit = new Vector3(1.0f, 0.0f, 0.0f);
                    break;

                case 2:
                    shadowData.mainLightShadowCascadesSplit = new Vector3(settings.cascade2Split, 1.0f, 0.0f);
                    break;

                case 3:
                    shadowData.mainLightShadowCascadesSplit = new Vector3(settings.cascade3Split.x, settings.cascade3Split.y, 0.0f);
                    break;

                default:
                    shadowData.mainLightShadowCascadesSplit = settings.cascade4Split;
                    break;
            }

            shadowData.mainLightShadowCascadeBorder = settings.cascadeBorder;

            shadowData.additionalLightShadowsEnabled = settings.supportsAdditionalLightShadows && (settings.additionalLightsRenderingMode == LightRenderingMode.PerPixel || isForwardPlus);
            shadowData.supportsAdditionalLightShadows = SystemInfo.supportsShadows && shadowData.additionalLightShadowsEnabled && additionalLightsCastShadows;
            shadowData.additionalLightsShadowmapWidth = shadowData.additionalLightsShadowmapHeight = settings.additionalLightsShadowmapResolution;
            shadowData.supportsSoftShadows = settings.supportsSoftShadows && (shadowData.supportsMainLightShadows || shadowData.supportsAdditionalLightShadows);
            shadowData.shadowmapDepthBufferBits = 16;

            // This will be setup in AdditionalLightsShadowCasterPass.
            shadowData.isKeywordAdditionalLightShadowsEnabled = false;
            shadowData.isKeywordSoftShadowsEnabled = false;
        }

        static void InitializePostProcessingData(UniversalRenderPipelineAsset settings, out PostProcessingData postProcessingData)
        {
            postProcessingData.gradingMode = settings.supportsHDR
                ? settings.colorGradingMode
                : ColorGradingMode.LowDynamicRange;

            if (HDROutputIsActive())
                postProcessingData.gradingMode = ColorGradingMode.HighDynamicRange;

            postProcessingData.lutSize = settings.colorGradingLutSize;
            postProcessingData.useFastSRGBLinearConversion = settings.useFastSRGBLinearConversion;
            postProcessingData.supportDataDrivenLensFlare = settings.supportDataDrivenLensFlare;
        }

        static void InitializeLightData(UniversalRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights, int mainLightIndex, out LightData lightData)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.initializeLightData);

            int maxPerObjectAdditionalLights = UniversalRenderPipeline.maxPerObjectLights;
            int maxVisibleAdditionalLights = UniversalRenderPipeline.maxVisibleAdditionalLights;

            lightData.mainLightIndex = mainLightIndex;

            if (settings.additionalLightsRenderingMode != LightRenderingMode.Disabled)
            {
                lightData.additionalLightsCount =
                    Math.Min((mainLightIndex != -1) ? visibleLights.Length - 1 : visibleLights.Length,
                        maxVisibleAdditionalLights);
                lightData.maxPerObjectAdditionalLightsCount = Math.Min(settings.maxAdditionalLightsCount, maxPerObjectAdditionalLights);
            }
            else
            {
                lightData.additionalLightsCount = 0;
                lightData.maxPerObjectAdditionalLightsCount = 0;
            }

            lightData.supportsAdditionalLights = settings.additionalLightsRenderingMode != LightRenderingMode.Disabled;
            lightData.shadeAdditionalLightsPerVertex = settings.additionalLightsRenderingMode == LightRenderingMode.PerVertex;
            lightData.visibleLights = visibleLights;
            lightData.supportsMixedLighting = settings.supportsMixedLighting;
            lightData.reflectionProbeBlending = settings.reflectionProbeBlending;
            lightData.reflectionProbeBoxProjection = settings.reflectionProbeBoxProjection;
            lightData.supportsLightLayers = RenderingUtils.SupportsLightLayers(SystemInfo.graphicsDeviceType) && settings.useRenderingLayers;
        }

        private static void ApplyTaaRenderingDebugOverrides(ref TemporalAA.Settings taaSettings)
        {
            var debugDisplaySettings = UniversalRenderPipelineDebugDisplaySettings.Instance;
            DebugDisplaySettingsRendering renderingSettings = debugDisplaySettings.renderingSettings;
            switch (renderingSettings.taaDebugMode)
            {
                case DebugDisplaySettingsRendering.TaaDebugMode.ShowClampedHistory:
                    taaSettings.frameInfluence = 0;
                    break;

                case DebugDisplaySettingsRendering.TaaDebugMode.ShowRawFrame:
                    taaSettings.frameInfluence = 1;
                    break;

                case DebugDisplaySettingsRendering.TaaDebugMode.ShowRawFrameNoJitter:
                    taaSettings.frameInfluence = 1;
                    taaSettings.jitterScale = 0;
                    break;
            }
        }

        private static void UpdateTemporalAAData(ref CameraData cameraData, UniversalAdditionalCameraData additionalCameraData)
        {
            // Initialize shared TAA target desc.
            ref var desc = ref cameraData.cameraTargetDescriptor;
            cameraData.taaPersistentData = additionalCameraData.taaPersistentData;
            cameraData.taaPersistentData.Init(desc.width, desc.height, desc.volumeDepth, desc.graphicsFormat, desc.vrUsage, desc.dimension);

            // Update TAA settings
            ref var taaSettings = ref additionalCameraData.taaSettings;
            cameraData.taaSettings = taaSettings;

            // Decrease history clear counter. Typically clear is only 1 frame, but can be many for XR multipass eyes!
            taaSettings.resetHistoryFrames -= taaSettings.resetHistoryFrames > 0 ? 1 : 0;
        }

        private static void UpdateTemporalAATargets(ref CameraData cameraData)
        {
            if (cameraData.IsTemporalAAEnabled())
            {
                bool xrMultipassEnabled = false;
#if ENABLE_VR && ENABLE_XR_MODULE
                xrMultipassEnabled = cameraData.xr.enabled && !cameraData.xr.singlePassEnabled;
#endif
                bool allocation = cameraData.taaPersistentData.AllocateTargets(xrMultipassEnabled);

                // Fill new history with current frame
                // XR Multipass renders a "frame" per eye
                if(allocation)
                    cameraData.taaSettings.resetHistoryFrames += xrMultipassEnabled ? 2 : 1;
            }
            else
                cameraData.taaPersistentData.DeallocateTargets();
        }

        static void UpdateCameraStereoMatrices(Camera camera, XRPass xr)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (xr.enabled)
            {
                if (xr.singlePassEnabled)
                {
                    for (int i = 0; i < Mathf.Min(2, xr.viewCount); i++)
                    {
                        camera.SetStereoProjectionMatrix((Camera.StereoscopicEye)i, xr.GetProjMatrix(i));
                        camera.SetStereoViewMatrix((Camera.StereoscopicEye)i, xr.GetViewMatrix(i));
                    }
                }
                else
                {
                    camera.SetStereoProjectionMatrix((Camera.StereoscopicEye)xr.multipassId, xr.GetProjMatrix(0));
                    camera.SetStereoViewMatrix((Camera.StereoscopicEye)xr.multipassId, xr.GetViewMatrix(0));
                }
            }
#endif
        }

        static PerObjectData GetPerObjectLightFlags(int additionalLightsCount, bool isForwardPlus)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.getPerObjectLightFlags);

            var configuration = PerObjectData.Lightmaps | PerObjectData.LightProbe | PerObjectData.OcclusionProbe | PerObjectData.ShadowMask;

            if (!isForwardPlus)
            {
                configuration |= PerObjectData.ReflectionProbes | PerObjectData.LightData;
            }

            if (additionalLightsCount > 0 && !isForwardPlus)
            {
                // In this case we also need per-object indices (unity_LightIndices)
                if (!RenderingUtils.useStructuredBuffer)
                    configuration |= PerObjectData.LightIndices;
            }

            return configuration;
        }

        // Main Light is always a directional light
        static int GetMainLightIndex(UniversalRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.getMainLightIndex);

            int totalVisibleLights = visibleLights.Length;

            if (totalVisibleLights == 0 || settings.mainLightRenderingMode != LightRenderingMode.PerPixel)
                return -1;

            Light sunLight = RenderSettings.sun;
            int brightestDirectionalLightIndex = -1;
            float brightestLightIntensity = 0.0f;
            for (int i = 0; i < totalVisibleLights; ++i)
            {
                ref VisibleLight currVisibleLight = ref visibleLights.UnsafeElementAtMutable(i);
                Light currLight = currVisibleLight.light;

                // Particle system lights have the light property as null. We sort lights so all particles lights
                // come last. Therefore, if first light is particle light then all lights are particle lights.
                // In this case we either have no main light or already found it.
                if (currLight == null)
                    break;

                if (currVisibleLight.lightType == LightType.Directional)
                {
                    // Sun source needs be a directional light
                    if (currLight == sunLight)
                        return i;

                    // In case no sun light is present we will return the brightest directional light
                    if (currLight.intensity > brightestLightIntensity)
                    {
                        brightestLightIntensity = currLight.intensity;
                        brightestDirectionalLightIndex = i;
                    }
                }
            }

            return brightestDirectionalLightIndex;
        }

        static void SetupPerFrameShaderConstants()
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.setupPerFrameShaderConstants);

            // Required for 2D Unlit Shadergraph master node as it doesn't currently support hidden properties.
            Shader.SetGlobalColor(ShaderPropertyId.rendererColor, Color.white);

            if (asset.lodCrossFadeDitheringType == LODCrossFadeDitheringType.BayerMatrix)
            {
                Shader.SetGlobalFloat(ShaderPropertyId.ditheringTextureInvSize, 1.0f / asset.textures.bayerMatrixTex.width);
                Shader.SetGlobalTexture(ShaderPropertyId.ditheringTexture, asset.textures.bayerMatrixTex);
            }
            else if (asset.lodCrossFadeDitheringType == LODCrossFadeDitheringType.BlueNoise)
            {
                Shader.SetGlobalFloat(ShaderPropertyId.ditheringTextureInvSize, 1.0f / asset.textures.blueNoise64LTex.width);
                Shader.SetGlobalTexture(ShaderPropertyId.ditheringTexture, asset.textures.blueNoise64LTex);
            }
        }

        static void SetupPerCameraShaderConstants(CommandBuffer cmd)
        {
            using var profScope = new ProfilingScope(null, Profiling.Pipeline.setupPerCameraShaderConstants);

            // When glossy reflections are OFF in the shader we set a constant color to use as indirect specular
            SphericalHarmonicsL2 ambientSH = RenderSettings.ambientProbe;
            Color linearGlossyEnvColor = new Color(ambientSH[0, 0], ambientSH[1, 0], ambientSH[2, 0]) * RenderSettings.reflectionIntensity;
            Color glossyEnvColor = CoreUtils.ConvertLinearToActiveColorSpace(linearGlossyEnvColor);
            cmd.SetGlobalVector(ShaderPropertyId.glossyEnvironmentColor, glossyEnvColor);

            // Used as fallback cubemap for reflections
            cmd.SetGlobalTexture(ShaderPropertyId.glossyEnvironmentCubeMap, ReflectionProbe.defaultTexture);
            cmd.SetGlobalVector(ShaderPropertyId.glossyEnvironmentCubeMapHDR, ReflectionProbe.defaultTextureHDRDecodeValues);

            // Ambient
            cmd.SetGlobalVector(ShaderPropertyId.ambientSkyColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientSkyColor));
            cmd.SetGlobalVector(ShaderPropertyId.ambientEquatorColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientEquatorColor));
            cmd.SetGlobalVector(ShaderPropertyId.ambientGroundColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientGroundColor));

            // Used when subtractive mode is selected
            cmd.SetGlobalVector(ShaderPropertyId.subtractiveShadowColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.subtractiveShadowColor));
        }

        static void CheckAndApplyDebugSettings(ref RenderingData renderingData)
        {
            var debugDisplaySettings = UniversalRenderPipelineDebugDisplaySettings.Instance;
            ref CameraData cameraData = ref renderingData.cameraData;

            if (debugDisplaySettings.AreAnySettingsActive && !cameraData.isPreviewCamera)
            {
                DebugDisplaySettingsRendering renderingSettings = debugDisplaySettings.renderingSettings;
                int msaaSamples = cameraData.cameraTargetDescriptor.msaaSamples;

                if (!renderingSettings.enableMsaa)
                    msaaSamples = 1;

                if (!renderingSettings.enableHDR)
                    cameraData.isHdrEnabled = false;

                if (!debugDisplaySettings.IsPostProcessingAllowed)
                    cameraData.postProcessEnabled = false;

                cameraData.hdrColorBufferPrecision = asset ? asset.hdrColorBufferPrecision : HDRColorBufferPrecision._32Bits;
                cameraData.cameraTargetDescriptor.graphicsFormat = MakeRenderTextureGraphicsFormat(cameraData.isHdrEnabled, cameraData.hdrColorBufferPrecision, true);
                cameraData.cameraTargetDescriptor.msaaSamples = msaaSamples;

            }
        }

        /// <summary>
        /// Returns the best supported image upscaling filter based on the provided upscaling filter selection
        /// </summary>
        /// <param name="imageSize">Size of the final image</param>
        /// <param name="renderScale">Scale being applied to the final image size</param>
        /// <param name="selection">Upscaling filter selected by the user</param>
        /// <returns>Either the original filter provided, or the best replacement available</returns>
        static ImageUpscalingFilter ResolveUpscalingFilterSelection(Vector2 imageSize, float renderScale, UpscalingFilterSelection selection)
        {
            // By default we just use linear filtering since it's the most compatible choice
            ImageUpscalingFilter filter = ImageUpscalingFilter.Linear;

            // Fall back to the automatic filter if FSR was selected, but isn't supported on the current platform
            // TODO: Investigate how to make FSR work with HDR output.
            if ((selection == UpscalingFilterSelection.FSR) && (!FSRUtils.IsSupported()))
            {
                selection = UpscalingFilterSelection.Auto;
            }

            switch (selection)
            {
                case UpscalingFilterSelection.Auto:
                {
                    // The user selected "auto" for their upscaling filter so we should attempt to choose the best filter
                    // for the current situation. When the current resolution and render scale are compatible with integer
                    // scaling we use the point sampling filter. Otherwise we just use the default filter (linear).
                    float pixelScale = (1.0f / renderScale);
                    bool isIntegerScale = Mathf.Approximately((pixelScale - Mathf.Floor(pixelScale)), 0.0f);

                    if (isIntegerScale)
                    {
                        float widthScale = (imageSize.x / pixelScale);
                        float heightScale = (imageSize.y / pixelScale);

                        bool isImageCompatible = (Mathf.Approximately((widthScale - Mathf.Floor(widthScale)), 0.0f) &&
                                                  Mathf.Approximately((heightScale - Mathf.Floor(heightScale)), 0.0f));

                        if (isImageCompatible)
                        {
                            filter = ImageUpscalingFilter.Point;
                        }
                    }

                    break;
                }

                case UpscalingFilterSelection.Linear:
                {
                    // Do nothing since linear is already the default

                    break;
                }

                case UpscalingFilterSelection.Point:
                {
                    filter = ImageUpscalingFilter.Point;

                    break;
                }

                case UpscalingFilterSelection.FSR:
                {
                    filter = ImageUpscalingFilter.FSR;

                    break;
                }
            }

            return filter;
        }

        /// <summary>
        /// Checks if the hardware (main display and platform) and the render pipeline support HDR.
        /// </summary>
        /// <returns>True if the main display and platform support HDR and HDR output is enabled on the platform.</returns>
        internal static bool HDROutputIsActive()
        {
            bool hdrOutputSupported = SystemInfo.hdrDisplaySupportFlags.HasFlag(HDRDisplaySupportFlags.Supported) && asset.supportsHDR;
            bool hdrOutputActive = HDROutputSettings.main.available && HDROutputSettings.main.active;
            // TODO: Until we can test it, disable for mobile
            return !Application.isMobilePlatform && hdrOutputSupported && hdrOutputActive;
        }

        /// <summary>
        /// Configures the render pipeline to render to HDR output or disables HDR output.
        /// </summary>
        static void SetHDRState(List<Camera> cameras)
        {
            bool hdrOutputActive = HDROutputSettings.main.available && HDROutputSettings.main.active;

            // If the pipeline doesn't support HDR rendering, output to SDR.
            bool supportsSwitchingHDR = SystemInfo.hdrDisplaySupportFlags.HasFlag(HDRDisplaySupportFlags.RuntimeSwitchable);
            bool switchHDRToSDR = supportsSwitchingHDR && !asset.supportsHDR && hdrOutputActive;
            if (switchHDRToSDR)
            {
                HDROutputSettings.main.RequestHDRModeChange(false);
            }

#if UNITY_EDITOR
            bool requestedHDRModeChange = false;

            // Automatically switch to HDR in the editor if it's available
            if (supportsSwitchingHDR && asset.supportsHDR && PlayerSettings.useHDRDisplay && HDROutputSettings.main.available)
            {
                int cameraCount = cameras.Count;

                if (cameraCount > 0 && cameras[0].cameraType != CameraType.Game)
                {
                    requestedHDRModeChange = hdrOutputActive;
                    HDROutputSettings.main.RequestHDRModeChange(false);
                }
                else
                {
                    requestedHDRModeChange = !hdrOutputActive;
                    HDROutputSettings.main.RequestHDRModeChange(true);
                }
            }

            if (requestedHDRModeChange || switchHDRToSDR)
            {
                // Repaint scene views and game views so the HDR mode request is applied
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
#endif

            // Make sure HDR auto tonemap is off if the URP is handling it
            if (hdrOutputActive)
            {
                HDROutputSettings.main.automaticHDRTonemapping = false;
            }
        }

        internal static void GetHDROutputLuminanceParameters(Tonemapping tonemapping, out Vector4 hdrOutputParameters)
        {
            float minNits = HDROutputSettings.main.minToneMapLuminance;
            float maxNits = HDROutputSettings.main.maxToneMapLuminance;
            float paperWhite = HDROutputSettings.main.paperWhiteNits;
            //ColorPrimaries colorPrimaries = ColorGamutUtility.GetColorPrimaries(HDROutputSettings.main.displayColorGamut);

            if (!tonemapping.detectPaperWhite.value)
            {
                paperWhite = tonemapping.paperWhite.value;
            }
            if (!tonemapping.detectBrightnessLimits.value)
            {
                minNits = tonemapping.minNits.value;
                maxNits = tonemapping.maxNits.value;
            }

            hdrOutputParameters = new Vector4(minNits, maxNits, paperWhite, 1f / paperWhite);
        }

        internal static void GetHDROutputGradingParameters(Tonemapping tonemapping, out Vector4 hdrOutputParameters)
        {
            int eetfMode = 0;
            float hueShift = 0.0f;

            switch (tonemapping.mode.value)
            {
                case TonemappingMode.Neutral:
                    eetfMode = (int)tonemapping.neutralHDRRangeReductionMode.value;
                    hueShift = tonemapping.hueShiftAmount.value;
                    break;

                case TonemappingMode.ACES:
                    eetfMode = (int)tonemapping.acesPreset.value;
                    break;
            }

            hdrOutputParameters = new Vector4(eetfMode, hueShift, 0.0f, 0.0f);
        }

#if ADAPTIVE_PERFORMANCE_2_0_0_OR_NEWER
        static void ApplyAdaptivePerformance(ref CameraData cameraData)
        {
            var noFrontToBackOpaqueFlags = SortingCriteria.SortingLayer | SortingCriteria.RenderQueue | SortingCriteria.OptimizeStateChanges | SortingCriteria.CanvasOrder;
            if (AdaptivePerformance.AdaptivePerformanceRenderSettings.SkipFrontToBackSorting)
                cameraData.defaultOpaqueSortFlags = noFrontToBackOpaqueFlags;

            var MaxShadowDistanceMultiplier = AdaptivePerformance.AdaptivePerformanceRenderSettings.MaxShadowDistanceMultiplier;
            cameraData.maxShadowDistance *= MaxShadowDistanceMultiplier;

            var RenderScaleMultiplier = AdaptivePerformance.AdaptivePerformanceRenderSettings.RenderScaleMultiplier;
            cameraData.renderScale *= RenderScaleMultiplier;

            // TODO
            if (!cameraData.xr.enabled)
            {
                cameraData.cameraTargetDescriptor.width = (int)(cameraData.camera.pixelWidth * cameraData.renderScale);
                cameraData.cameraTargetDescriptor.height = (int)(cameraData.camera.pixelHeight * cameraData.renderScale);
            }

            var antialiasingQualityIndex = (int)cameraData.antialiasingQuality - AdaptivePerformance.AdaptivePerformanceRenderSettings.AntiAliasingQualityBias;
            if (antialiasingQualityIndex < 0)
                cameraData.antialiasing = AntialiasingMode.None;
            cameraData.antialiasingQuality = (AntialiasingQuality)Mathf.Clamp(antialiasingQualityIndex, (int)AntialiasingQuality.Low, (int)AntialiasingQuality.High);
        }

        static void ApplyAdaptivePerformance(ref RenderingData renderingData)
        {
            if (AdaptivePerformance.AdaptivePerformanceRenderSettings.SkipDynamicBatching)
                renderingData.supportsDynamicBatching = false;

            var MainLightShadowmapResolutionMultiplier = AdaptivePerformance.AdaptivePerformanceRenderSettings.MainLightShadowmapResolutionMultiplier;
            renderingData.shadowData.mainLightShadowmapWidth = (int)(renderingData.shadowData.mainLightShadowmapWidth * MainLightShadowmapResolutionMultiplier);
            renderingData.shadowData.mainLightShadowmapHeight = (int)(renderingData.shadowData.mainLightShadowmapHeight * MainLightShadowmapResolutionMultiplier);

            var MainLightShadowCascadesCountBias = AdaptivePerformance.AdaptivePerformanceRenderSettings.MainLightShadowCascadesCountBias;
            renderingData.shadowData.mainLightShadowCascadesCount = Mathf.Clamp(renderingData.shadowData.mainLightShadowCascadesCount - MainLightShadowCascadesCountBias, 0, 4);

            var shadowQualityIndex = AdaptivePerformance.AdaptivePerformanceRenderSettings.ShadowQualityBias;
            for (int i = 0; i < shadowQualityIndex; i++)
            {
                if (renderingData.shadowData.supportsSoftShadows)
                {
                    renderingData.shadowData.supportsSoftShadows = false;
                    continue;
                }

                if (renderingData.shadowData.supportsAdditionalLightShadows)
                {
                    renderingData.shadowData.supportsAdditionalLightShadows = false;
                    continue;
                }

                if (renderingData.shadowData.supportsMainLightShadows)
                {
                    renderingData.shadowData.supportsMainLightShadows = false;
                    continue;
                }

                break;
            }

            if (AdaptivePerformance.AdaptivePerformanceRenderSettings.LutBias >= 1 && renderingData.postProcessingData.lutSize == 32)
                renderingData.postProcessingData.lutSize = 16;
        }

#endif

        /// <summary>
        /// Data structure describing the data for a specific render request
        /// </summary>
        public class SingleCameraRequest
        {
            /// <summary>
            /// Target texture
            /// </summary>
            public RenderTexture destination = null;

            /// <summary>
            /// Target texture mip level
            /// </summary>
            public int mipLevel = 0;

            /// <summary>
            /// Target texture cubemap face
            /// </summary>
            public CubemapFace face = CubemapFace.Unknown;

            /// <summary>
            /// Target texture slice
            /// </summary>
            public int slice = 0;
        }
    }
}
