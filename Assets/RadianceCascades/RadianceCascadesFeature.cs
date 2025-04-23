using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RendererUtils;
using Unity.Mathematics;
using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Assertions;


public class RadianceCascadesPassFeature : ScriptableRendererFeature
{
    // collect light source and obstacle in this pass
    class CollectEnvironmentPass : ScriptableRenderPass
    {
        public static readonly string RenderPassName = "CollectEnvironmentPass";

        private static readonly ProfilingSampler mProfilingSampler = new ProfilingSampler(RenderPassName);
        private static readonly ShaderTagId ShaderPassTagLightSource = new ShaderTagId("LightSource");

        private RTHandle mEnvironmentTexture;
        private Camera mSDFCamera; // camera for generate sdf texture

        private class SLightSource 
        {
            public static readonly int SDFCameraVP = Shader.PropertyToID("_SDFCameraVP");
        }

        public CollectEnvironmentPass(Camera sdf_cameras)
        {
            mSDFCamera = sdf_cameras;
        }

        public void Dispose()
        {
            mEnvironmentTexture?.Release();
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            int sdf_tex_size = Mathf.Max(renderingData.cameraData.camera.pixelWidth, renderingData.cameraData.camera.pixelHeight) * SDFTextureRatio;
            var desc = new RenderTextureDescriptor(sdf_tex_size, sdf_tex_size, GraphicsFormat.R16G16B16A16_SFloat, 0);

            RenderingUtils.ReAllocateIfNeeded(ref mEnvironmentTexture, desc, FilterMode.Point, TextureWrapMode.Clamp, false, 0, 0, "EnvironmentTexture");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd =  CommandBufferPool.Get();

            using (new ProfilingScope(cmd, mProfilingSampler)) 
            {
                Camera main_camera = Camera.main;

                mSDFCamera.CopyFrom(main_camera);
                mSDFCamera.orthographic = true;
                mSDFCamera.aspect = 1.0f;
                mSDFCamera.orthographicSize = main_camera.orthographicSize * SDFTextureRatio;
                mSDFCamera.gameObject.transform.position = main_camera.gameObject.transform.position;
                mSDFCamera.gameObject.transform.rotation = main_camera.gameObject.transform.rotation;

                // d3d and opengl have different NDC space range, we need to adjust the projection matrix depends on what the platform we are on
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(mSDFCamera.projectionMatrix, true);

                cmd.SetGlobalMatrix(SLightSource.SDFCameraVP, projection * mSDFCamera.worldToCameraMatrix);

                if (!mSDFCamera.TryGetCullingParameters(out ScriptableCullingParameters culling_parameters)) 
                {
                    Debug.LogWarning("failed to get culling parameters");
                    return;
                }

                CullingResults culling_results = context.Cull(ref culling_parameters);

                var desc = new RendererListDesc(ShaderPassTagLightSource, culling_results, mSDFCamera)
                {
                    renderQueueRange = RenderQueueRange.all,
                };

                RendererList list = context.CreateRendererList(desc);

                if (!list.isValid)
                {
                    Debug.LogError("RendererList is invalid.");
                    return;
                }

                cmd.SetRenderTarget(mEnvironmentTexture);
                cmd.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

                cmd.DrawRendererList(list);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        public RTHandle GetEnvironmentTexture()
        {
            return mEnvironmentTexture;
        }
    }

    // calculate SDF texture by JFA in this pass
    // explanation is here: https://blog.demofox.org/2016/02/29/fast-voronoi-diagrams-and-distance-dield-textures-on-the-gpu-with-the-jump-flooding-algorithm/
    class GenerateSDFPass : ScriptableRenderPass
    {        
        private static class CSJFA // Compute Shader Jump Flooding Algorithm
        {
            public static readonly ComputeShader ComputeShader = Resources.Load<ComputeShader>("GenerateSDF");
            public static readonly int PreprocessKernel = ComputeShader.FindKernel("_PreprocessKernel");
            public static readonly int FloodKernel = ComputeShader.FindKernel("_FloodKernel");
            public static readonly int DistanceKernel = ComputeShader.FindKernel("_DistanceKernel");

            public static readonly int SDFTexture = Shader.PropertyToID("_SDFTexture");
            public static readonly int SDFIndex = Shader.PropertyToID("_SDFIndex");
            public static readonly int FloodTexture = Shader.PropertyToID("_FloodTexture");
            public static readonly int EnvMap = Shader.PropertyToID("_EnvTexture");
            public static readonly int Step = Shader.PropertyToID("_Step");
            public static readonly int Resolution = Shader.PropertyToID("_Resolution");
        }

        public static readonly string RenderPassName = "GenerateSDFPass";
        private static readonly ProfilingSampler ProfilingSampler = new ProfilingSampler(RenderPassName);

        private CollectEnvironmentPass mEnvPass;
        private RTHandle mSDFTexture;
        private RTHandle mFloodTexture;

        public GenerateSDFPass(CollectEnvironmentPass pass)
        {
            mEnvPass = pass;
        }

        public void Dispose()
        {
            mSDFTexture?.Release();
            mFloodTexture?.Release();
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // SDF texture
            int sdf_tex_size = Mathf.Max(renderingData.cameraData.camera.pixelWidth, renderingData.cameraData.camera.pixelHeight) * SDFTextureRatio;
            RenderTextureDescriptor sdf_tex_desc = new RenderTextureDescriptor(sdf_tex_size, sdf_tex_size, GraphicsFormat.R16_UNorm, 0);
            sdf_tex_desc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref mSDFTexture, sdf_tex_desc, FilterMode.Point, TextureWrapMode.Clamp, false, 0, 0, "SDFTexture");

            // temporary texture for running JFA
            var flood_tex_desc = new RenderTextureDescriptor(sdf_tex_size, sdf_tex_size, GraphicsFormat.R16G16_UInt, 0);
            flood_tex_desc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref mFloodTexture, flood_tex_desc, FilterMode.Point, TextureWrapMode.Clamp, false, 0, 0, "FloodTexture");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if(!CSJFA.ComputeShader)
            {
                Debug.LogError("GenerateSDFPass.Execute() won't execute due to a missing shader file");
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get();

            using(new ProfilingScope(cmd, ProfilingSampler))
            {
                cmd.SetRenderTarget(mSDFTexture);
                cmd.ClearRenderTarget(true, true, Color.red);

                Vector2Int flood_tex_size = mFloodTexture.GetScaledSize();
                uint thread_group_x, thread_group_y, thread_group_z;

                // preprocess part
                cmd.SetComputeTextureParam(CSJFA.ComputeShader, CSJFA.PreprocessKernel, CSJFA.EnvMap, mEnvPass.GetEnvironmentTexture());
                cmd.SetComputeTextureParam(CSJFA.ComputeShader, CSJFA.PreprocessKernel, CSJFA.FloodTexture, mFloodTexture);

                CSJFA.ComputeShader.GetKernelThreadGroupSizes(CSJFA.PreprocessKernel, out thread_group_x, out thread_group_y, out thread_group_z);
                cmd.DispatchCompute(
                    CSJFA.ComputeShader,
                    CSJFA.PreprocessKernel,
                    (int)Mathf.Ceil(flood_tex_size.x / (float)thread_group_x),
                    (int)Mathf.Ceil(flood_tex_size.y / (float)thread_group_y),
                1);

                // flood part
                cmd.SetComputeTextureParam(CSJFA.ComputeShader, CSJFA.FloodKernel, CSJFA.FloodTexture, mFloodTexture);

                CSJFA.ComputeShader.GetKernelThreadGroupSizes(CSJFA.FloodKernel, out thread_group_x, out thread_group_y, out thread_group_z);
                int max_step = (int)Mathf.Log(Mathf.NextPowerOfTwo(Mathf.Max(flood_tex_size.x, flood_tex_size.y)), 2);

                for (int step = max_step; step >= 0; step--)
                {
                    cmd.SetComputeIntParam(CSJFA.ComputeShader, CSJFA.Step, (int)MathF.Pow(2, step));
                    cmd.SetComputeIntParams(CSJFA.ComputeShader, CSJFA.Resolution, flood_tex_size.x, flood_tex_size.y);

                    cmd.DispatchCompute(
                        CSJFA.ComputeShader,
                        CSJFA.FloodKernel,
                        (int)Mathf.Ceil(flood_tex_size.x / (float)thread_group_x),
                        (int)Mathf.Ceil(flood_tex_size.y / (float)thread_group_y),
                        1
                    );
                }

                // distance part
                cmd.SetComputeTextureParam(CSJFA.ComputeShader, CSJFA.DistanceKernel, CSJFA.FloodTexture, mFloodTexture);
                cmd.SetComputeTextureParam(CSJFA.ComputeShader, CSJFA.DistanceKernel, CSJFA.SDFTexture, mSDFTexture);

                CSJFA.ComputeShader.GetKernelThreadGroupSizes(CSJFA.FloodKernel, out thread_group_x, out thread_group_y, out thread_group_z);
                cmd.DispatchCompute(
                    CSJFA.ComputeShader,
                    CSJFA.DistanceKernel,
                    (int)Mathf.Ceil(flood_tex_size.x / (float)thread_group_x),
                    (int)Mathf.Ceil(flood_tex_size.y / (float)thread_group_y),
                1);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        public RTHandle GetSDFTexture()
        {
            return mSDFTexture;
        }
    }

    // radiance cascades, by calculate cascades of radiance interval and merge them into one, we can generate irradiance map of the scene in a short time
    // explanations are here:
    // https://drive.google.com/file/d/1L6v1_7HY2X-LV3Ofb6oyTIxgEaP4LOI6/view
    // https://mini.gmshaders.com/p/radiance-cascades
    class ComputeCascadesPass : ScriptableRenderPass
    {
        class CSComputeCascades
        {
            public static readonly ComputeShader ComputeShader = Resources.Load<ComputeShader>("ComputeCascades");
            public static readonly int ComputeCascadesKernel = ComputeShader.FindKernel("_ComputeCascadesKernel");

            public static readonly int SDFTexture = Shader.PropertyToID("_SDFTexture");
            public static readonly int EnvTexture = Shader.PropertyToID("_EnvTexture");
            public static readonly int CascadeTexture = Shader.PropertyToID("_CascadeTexture");

            public static readonly int SDFTextureSize = Shader.PropertyToID("_SDFTextureSize");
            public static readonly int Resolution = Shader.PropertyToID("_Resolution");
            public static readonly int CascadeRange = Shader.PropertyToID("_CascadeRange");
            public static readonly int ProbeSize = Shader.PropertyToID("_ProbeSize");
            public static readonly int AngleStep = Shader.PropertyToID("_AngleStep");
        }

        public static readonly string RenderPassName = "ComputeCascadesPass";
        private static readonly ProfilingSampler ProfilingSampler = new ProfilingSampler(RenderPassName);

        private RTHandle[] mCascadesTextures;
        private CollectEnvironmentPass mEnvPass;
        private GenerateSDFPass mSDFPass;
        private Vector2Int mCascadeTextureSize;

        public ComputeCascadesPass(CollectEnvironmentPass env_pass, GenerateSDFPass sdf_pass)
        {
            mEnvPass = env_pass;
            mSDFPass = sdf_pass;
            mCascadesTextures = new RTHandle[NumRadianceCascades];
        }

        public void Dispose() 
        {
            if (mCascadesTextures != null) 
            {
                for(int i = 0; i < mCascadesTextures.Length; i++) 
                {
                    mCascadesTextures[i]?.Release();
                }
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            for (uint i = 0; i < NumRadianceCascades; i++) 
            {
                int max_probe_size = (int)Mathf.Pow(2, NumRadianceCascades);
                int width =  (int)Mathf.Ceil(renderingData.cameraData.camera.pixelWidth / (float)max_probe_size) * max_probe_size;
                int height = (int)Mathf.Ceil(renderingData.cameraData.camera.pixelHeight / (float)max_probe_size) * max_probe_size;
                mCascadeTextureSize = new Vector2Int(width, height);

                var desc = new RenderTextureDescriptor(mCascadeTextureSize.x, mCascadeTextureSize.y, GraphicsFormat.R16G16B16A16_SFloat, 0);
                desc.enableRandomWrite = true;

                RenderingUtils.ReAllocateIfNeeded(ref mCascadesTextures[i], desc, FilterMode.Point, TextureWrapMode.Clamp);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, ProfilingSampler)) 
            {
                if (!CSComputeCascades.ComputeShader)
                {
                    Debug.LogError("ComputeCascadesPass.Execute() won't execute due to a missing shader file");
                    return;
                }

                Vector2Int sdf_texture_size = mSDFPass.GetSDFTexture().GetScaledSize();
                Assert.IsTrue(sdf_texture_size.x == sdf_texture_size.y);

                cmd.SetComputeTextureParam(CSComputeCascades.ComputeShader, CSComputeCascades.ComputeCascadesKernel, CSComputeCascades.SDFTexture, mSDFPass.GetSDFTexture());
                cmd.SetComputeTextureParam(CSComputeCascades.ComputeShader, CSComputeCascades.ComputeCascadesKernel, CSComputeCascades.EnvTexture, mEnvPass.GetEnvironmentTexture());
                cmd.SetComputeFloatParams(CSComputeCascades.ComputeShader, CSComputeCascades.SDFTextureSize, sdf_texture_size.x, 1.0f / sdf_texture_size.x);

                cmd.SetComputeIntParams(CSComputeCascades.ComputeShader, CSComputeCascades.Resolution, renderingData.cameraData.camera.pixelWidth, renderingData.cameraData.camera.pixelHeight);
                for (int i = 0; i < NumRadianceCascades; i++) 
                {
                    int2 range = CalculateCascadeRange(i);
                    int probe_size = Cascade0ProbeSize * (int)Mathf.Pow(2, i);
                    float angle_step = ((2 * Mathf.PI) / (probe_size * probe_size));

                    cmd.SetComputeIntParam(CSComputeCascades.ComputeShader, CSComputeCascades.ProbeSize, probe_size);
                    cmd.SetComputeFloatParam(CSComputeCascades.ComputeShader, CSComputeCascades.AngleStep, angle_step);
                    cmd.SetComputeIntParams(CSComputeCascades.ComputeShader, CSComputeCascades.CascadeRange, range.x, range.y);

                    cmd.SetComputeTextureParam(CSComputeCascades.ComputeShader, CSComputeCascades.ComputeCascadesKernel, CSComputeCascades.CascadeTexture, mCascadesTextures[i]);

                    uint thread_group_x, thread_group_y, thread_group_z;
                    CSComputeCascades.ComputeShader.GetKernelThreadGroupSizes(CSComputeCascades.ComputeCascadesKernel, out thread_group_x, out thread_group_y, out thread_group_z);
                    cmd.DispatchCompute(
                        CSComputeCascades.ComputeShader,
                        CSComputeCascades.ComputeCascadesKernel,
                        (int)(Mathf.Ceil(renderingData.cameraData.camera.pixelWidth / (float)thread_group_x)),
                        (int)(Mathf.Ceil(renderingData.cameraData.camera.pixelHeight / (float)thread_group_y)),
                        1
                    );
                }
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            cmd.Release();
        }

        //Returns the range of the Ray that we will be shooting
        //Notice that the end of the lower ray is the start of the upper ray
        //cascade0 range : 0 to 1
        //cascade1 range : 1 to 5
        //cascade2 range : 5 to 21
        //...
        public int2 CalculateCascadeRange(int cascadeLevel)
        {
            // r(L) = factor^(L - 1) : randiance interval range for cascade L [0, inf)
            //                         each radiance interval is 4(value of factor) times larger than the previous level
            const int factor = 4;

            // end(L) = r(0) + r(1) + r(2) + ... + r(L + 1) = (1 - factor^(L + 1)) / (1 - factor)
            // start(L) = end(L - 1) = (1 - factor^L) / (1 - factor)
            int begin = (int)((1.0f - Mathf.Pow(factor, cascadeLevel)) / (1.0f - factor));
            int end = (int)((1.0f - Mathf.Pow(factor, cascadeLevel + 1)) / (1.0f - factor));

            return new int2(begin, end);
        }

        public RTHandle[] GetCascadesTexture() 
        {
            return mCascadesTextures;
        }
    }

    class MergeCascadePass : ScriptableRenderPass
    {
        private static readonly string RenderPassName = "MergeCascadePass";
        private static readonly ProfilingSampler ProfilingSampler = new ProfilingSampler(RenderPassName);

        private class CSMergeCascades 
        {
            public static readonly ComputeShader ComputeShader = Resources.Load<ComputeShader>("MergeCascades");
            public static readonly int MergeCascadesKernel = ComputeShader.FindKernel("_MergeCascadesKernel");
            public static readonly int GenerateIrradianceKernel = ComputeShader.FindKernel("_GenerateIrradiance");

            public static readonly int LowerCascade = Shader.PropertyToID("_LowerCascade");
            public static readonly int LowerCascadeProbeSize = Shader.PropertyToID("_LowerCascadeProbeSize");
            public static readonly int LowerAngleStep = Shader.PropertyToID("_LowerAngleStep");
            public static readonly int UpperCascade = Shader.PropertyToID("_UpperCascade");
            public static readonly int UpperCascadeProbeSize = Shader.PropertyToID("_UpperCascadeProbeSize");
            public static readonly int UpperAngleStep = Shader.PropertyToID("_UpperAngleStep");
            public static readonly int IrradianceTexture = Shader.PropertyToID("_IrradianceTexture");
        }

        private RTHandle mIrradianceTexture;
        private int mNumCascades;
        private ComputeCascadesPass mComputeCascadesPass;

        public MergeCascadePass(ComputeCascadesPass compute_cascade_pass)
        {
            mNumCascades = NumRadianceCascades;
            mComputeCascadesPass = compute_cascade_pass;
        }

        public void Dispose() 
        {
            mIrradianceTexture?.Release();
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            int width = renderingData.cameraData.camera.pixelWidth / 2;
            int height = renderingData.cameraData.camera.pixelHeight / 2;
            var desc = new RenderTextureDescriptor(width, height, GraphicsFormat.R16G16B16A16_SFloat, 0);
            desc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref mIrradianceTexture, desc, FilterMode.Point, TextureWrapMode.Clamp, false, 0, 0, "IrradianceTexture");

            // bind the irradiance texture as a global texture
            int ScreenSpaceIrradiance = Shader.PropertyToID("_ScreenSpaceIrradiance");
            Shader.SetGlobalTexture(ScreenSpaceIrradiance, mIrradianceTexture.rt);
            Shader.SetGlobalVector("_ScreenSpaceIrradianceSize", new Vector4(width, height, 1.0f / width, 1.0f / height));
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using(new ProfilingScope(cmd, ProfilingSampler))
            {
                if (!CSMergeCascades.ComputeShader)
                {
                    Debug.LogError("MergeCascadePass.Execute() won't execute due to a missing shader file");
                    return;
                }

                RTHandle[] cascades = mComputeCascadesPass.GetCascadesTexture();

                // merge the cascades from top to bottom
                for (int i = mNumCascades - 2; i >= 0; i--) 
                {
                    int probe_size = Cascade0ProbeSize * (int)Mathf.Pow(2, i);
                    float angle_step = (2 * Mathf.PI) / (probe_size * probe_size);
                    cmd.SetComputeIntParam(CSMergeCascades.ComputeShader, CSMergeCascades.LowerCascadeProbeSize, probe_size);
                    cmd.SetComputeFloatParams(CSMergeCascades.ComputeShader, CSMergeCascades.LowerAngleStep, angle_step, 1 / angle_step);

                    // each cascade has 4 times more rays per probe than the lower cascades, and has 1/4 the number of probe
                    probe_size = Cascade0ProbeSize * (int)Mathf.Pow(2, i + 1);
                    angle_step = (2 * Mathf.PI) / (probe_size * probe_size);
                    cmd.SetComputeIntParam(CSMergeCascades.ComputeShader, CSMergeCascades.UpperCascadeProbeSize, probe_size);
                    cmd.SetComputeFloatParams(CSMergeCascades.ComputeShader, CSMergeCascades.UpperAngleStep, angle_step, 1 / angle_step);

                    cmd.SetComputeTextureParam(CSMergeCascades.ComputeShader, CSMergeCascades.MergeCascadesKernel, CSMergeCascades.LowerCascade, cascades[i]);
                    cmd.SetComputeTextureParam(CSMergeCascades.ComputeShader, CSMergeCascades.MergeCascadesKernel, CSMergeCascades.UpperCascade, cascades[i + 1]);

                    uint thread_group_x, thread_group_y, thread_group_z;
                    CSMergeCascades.ComputeShader.GetKernelThreadGroupSizes(CSMergeCascades.MergeCascadesKernel, out thread_group_x, out thread_group_y, out thread_group_z);
                    cmd.DispatchCompute(
                        CSMergeCascades.ComputeShader,
                        CSMergeCascades.MergeCascadesKernel,
                        (int)(Mathf.Ceil(renderingData.cameraData.camera.pixelWidth / (float)thread_group_x)),
                        (int)(Mathf.Ceil(renderingData.cameraData.camera.pixelHeight / (float)thread_group_y)),
                        1
                    );
                }

                // generate irradiance texture from level 0 cascade
                uint thread_group_x1, thread_group_y1, thread_group_z1;
                CSMergeCascades.ComputeShader.GetKernelThreadGroupSizes(CSMergeCascades.MergeCascadesKernel, out thread_group_x1, out thread_group_y1, out thread_group_z1);
                cmd.SetComputeTextureParam(CSMergeCascades.ComputeShader, CSMergeCascades.GenerateIrradianceKernel, CSMergeCascades.LowerCascade, cascades[0]);
                cmd.SetComputeTextureParam(CSMergeCascades.ComputeShader, CSMergeCascades.GenerateIrradianceKernel, CSMergeCascades.IrradianceTexture, mIrradianceTexture);
                cmd.DispatchCompute(
                    CSMergeCascades.ComputeShader,
                    CSMergeCascades.GenerateIrradianceKernel,
                    (int)(Mathf.Ceil(renderingData.cameraData.camera.pixelWidth / (float)(2 * thread_group_x1))), // irradiance texture is half the size of the camera textur))e
                    (int)(Mathf.Ceil(renderingData.cameraData.camera.pixelHeight / (float)(2 * thread_group_y1))),
                    1
                );
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            cmd.Release();
        }
    }

    // probe size in the lowest cascade
    public const int Cascade0ProbeSize = 2;
    private const int SDFTextureRatio = 1;
    public const int NumRadianceCascades = 7;

    CollectEnvironmentPass mDrawEnviormentPass;
    GenerateSDFPass mGenerateSDFPass;
    ComputeCascadesPass mComputeCascadesPass;
    MergeCascadePass mMergeCascadesPass;

    Camera mSDFCamera;
    bool mInitialized = false;

    public override void Create()
    {
        // for some reasons, this function may be invoked twice
        if (mInitialized)
            return;

        mInitialized = true;

        // camera for generating SDF texture
        Camera cam = mSDFCamera = new GameObject("SDFCascadeCamera").AddComponent<Camera>();
        cam.gameObject.hideFlags = HideFlags.HideAndDontSave; // hide in hierarchy and ignored when saving the scene
        cam.enabled = false;

        // render pass
        mDrawEnviormentPass = new CollectEnvironmentPass(mSDFCamera);
        mGenerateSDFPass = new GenerateSDFPass(mDrawEnviormentPass);
        mComputeCascadesPass = new ComputeCascadesPass(mDrawEnviormentPass, mGenerateSDFPass);
        mMergeCascadesPass = new MergeCascadePass(mComputeCascadesPass);

        mDrawEnviormentPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        mGenerateSDFPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        mComputeCascadesPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        mMergeCascadesPass.renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(mDrawEnviormentPass);
        renderer.EnqueuePass(mGenerateSDFPass);
        renderer.EnqueuePass(mComputeCascadesPass);
        renderer.EnqueuePass(mMergeCascadesPass);
    }

    protected override void Dispose(bool disposing)
    {
        mInitialized = false;

        mDrawEnviormentPass?.Dispose();
        mGenerateSDFPass?.Dispose();
        mComputeCascadesPass?.Dispose();
        mMergeCascadesPass?.Dispose();

        mDrawEnviormentPass = null;
        mGenerateSDFPass = null;
        mComputeCascadesPass = null;
        mMergeCascadesPass = null;

        if (mSDFCamera != null) 
        {
            DestroyImmediate(mSDFCamera.gameObject);
            mSDFCamera = null;
        }
    }
}
