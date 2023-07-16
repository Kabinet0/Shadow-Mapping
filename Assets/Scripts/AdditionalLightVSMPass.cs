using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using System.Reflection;
using System;
using static UnityEditor.Progress;
using System.Runtime.InteropServices;
using UnityEngine.Experimental.GlobalIllumination;

// TODO:

// Really good resource on cubemap blurring https://www.reddit.com/r/vulkan/comments/y6sz28/point_light_shadow_mapping_using_cube_maps/

// Figure out how unity's PCF doesn't blur over edges
// Update: Turns out they use an FOV bias and don't sample over edges that way

// [==] Foundation Work [==]
// [Canceled] Swap to Tex2dArray. MAYBE, WE AREN'T SURE https://docs.unity3d.com/ScriptReference/Rendering.TextureDimension.html
// [X] Figure out how to get unity to shut up about RTHandles - new goal, don't leak memory while doing it..
// [X] Actual shadow filtering. (Fast ish box blur basically)
// [X] Figure out how to unslice texture
// [X] Custom Lit shader?? (Debatably Finished)
// [X] VARIANCE SHADOW MAPPING WOO YEAH!1!!1!!
// [X] A more permenant solution for the z-buffer flipping thing (probably #ifdef)

// [==]  Texture Filtering Bugs [==]
// [2/3] Two bugs.. The mip thing, and the bilinear filtering thing (Wait no third one: Anisotropic filtering bug??(Use derrivatives or smth)
// [X] Maybe forget about mipmaps
// [Canned] Consider... uhh, using multiple sets of split uvs for different mips clamping? Perhaps a pixel based solution? Might be easier?
// [-] Linear depth

// [==]  Blur Features [==]
// [X] Seperate blur shader passes for cubemaps. Gotta blur over edges. // FINALLY GODDAMN
// [-] Optimize blur - linear filtering gpu optimzations: https://www.rastergrid.com/blog/2010/09/efficient-gaussian-blur-with-linear-sampling/
// [-] Consider new compute based blur approach. Maybe a moving average or something. Could probably kill a ton of draw calls + texture samples like that.
// [-] Gaussian blur, probably just with a two pass linear filter
// [-] Redo CPU light blur loop so it no longer sucks
// [-] Investigate performance improvements for compute blurs, perhaps groupshared memory could help somehow?

// [==] Next Steps [==]
// [X] Avoid some Reflection by extending (hacking) URP: https://www.youtube.com/watch?v=ZiHH_BvjoGk
// [-] EVSM (once done variance shadow mapping)
// [-] Optimize all that reflection usage perhaps
// [-] Configurabillity - Slap all of the numbers into the render feature
// [1/2] Clean up the goddamn mess that is your shaderLibrary I mean wow.
// [-] Implement keywords to disable additional light VSM entirely in the custom shader, revert to how Lit works.

// [+] Maybe a directional light EVSM system?! ;P
// [+] Directional light only obviously, but try a SAVSM, with PCSS, allegedly pretty fast (How gaussian though... D:)
//     Maybe even see if SAEVSM would work at all if light bleeding is an issue. Brand new algorithm maybe :D
// [+] Ultra Basic Caching, just skip copy + blur on that light?

public class AdditionalVSMRenderPass : ScriptableRenderPass
{
    ProfilingSampler m_ProfilingSamplerCopy = new ProfilingSampler("Buffer Copy");
    ProfilingSampler m_ProfilingSamplerHorizontal = new ProfilingSampler("Horizontal Blur Pass");
    ProfilingSampler m_ProfilingSamplerVertical = new ProfilingSampler("Vertical Blur Pass");
    AdditionalLightVSMRenderFeature.VSMParameters m_EVSMParameters;


    //Rendering variables
    Material _BufferMaterial;
    ComputeShader _CubemapBlurX, _CubemapBlurY;

    RTHandle BaseShadowMap;
    RTHandle VarianceShadowMap;
    RTHandle ShadowMapBackBuffer;

    ComputeBuffer FaceOffsetsBuffer;


    // Reflected shadow data
    private int lightCount;
    private int sliceCount;
    private List<int> ShadowSliceToAdditionalLight;
    private int[] AdditionalLightIndexToVisibleLightIndex;
    private Vector4[] AdditionalLightParms;
    private ShadowSliceData[] ShadowSlices;

    //Reflection Boilerplate
    private AdditionalLightsShadowCasterPass AdditionalShadowCasterPass;
    private FieldInfo ShadowSliceField;
    private FieldInfo ShadowSliceToLightField;
    private FieldInfo AdditionalLightsShadowParmsField; 
    private FieldInfo ShadowRTField;
    private FieldInfo AdditionalLightToVisibleLightField;


    public AdditionalVSMRenderPass(Shader bufferCopyShader, ComputeShader cubemapBlurX, ComputeShader cubemapBlurY, ref AdditionalLightVSMRenderFeature.VSMParameters parameters)
    {
        if (_BufferMaterial == null) _BufferMaterial = CoreUtils.CreateEngineMaterial(bufferCopyShader);
        _CubemapBlurX = cubemapBlurX;
        _CubemapBlurY = cubemapBlurY;

        renderPassEvent = RenderPassEvent.AfterRenderingShadows;

        m_EVSMParameters = parameters;
    } 

    // Consider moving to configure
    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        // There has to be a better way to do this than null checks
        if (FaceOffsetsBuffer == null) {
            FaceOffsetsBuffer = new ComputeBuffer(6, sizeof(float) * 2);
        }

        if (AdditionalShadowCasterPass == null) {
            ReflectRenderPassData(renderingData);
        }  

        // Reflection :D
        ShadowSlices = (ShadowSliceData[])ShadowSliceField?.GetValue(AdditionalShadowCasterPass);
        ShadowSliceToAdditionalLight = (List<int>)ShadowSliceToLightField?.GetValue(AdditionalShadowCasterPass);
        BaseShadowMap = (RTHandle)ShadowRTField?.GetValue(AdditionalShadowCasterPass);
        AdditionalLightParms = (Vector4[])AdditionalLightsShadowParmsField?.GetValue(AdditionalShadowCasterPass);
        sliceCount = ShadowSliceToAdditionalLight.Count;
        AdditionalLightIndexToVisibleLightIndex = (int[])AdditionalLightToVisibleLightField?.GetValue(AdditionalShadowCasterPass);


        RenderTextureDescriptor desc;
        if (BaseShadowMap != null) {
            desc = new RenderTextureDescriptor(BaseShadowMap.rt.width, BaseShadowMap.rt.height, RenderTextureFormat.RGFloat, 0);
        }
        else {
            desc = new RenderTextureDescriptor(1, 1, RenderTextureFormat.RGFloat, 0);
        }
         
        desc.useMipMap = false; // Implement all this https://developer.nvidia.com/gpugems/gpugems3/part-ii-light-and-shadows/chapter-8-summed-area-variance-shadow-maps
        desc.enableRandomWrite = true;
        //desc.dimension = TextureDimension.Tex2DArray; <- this will need a shader change, but should also be done at some point probably I think

        RenderingUtils.ReAllocateIfNeeded(ref VarianceShadowMap, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: "_VarianceShadowMap");
        RenderingUtils.ReAllocateIfNeeded(ref ShadowMapBackBuffer, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: "_VarianceShadowMapBackBuffer");
        //VarianceShadowMap.rt.anisoLevel = 16;

        cmd.SetGlobalTexture(VarianceShadowMap.name, VarianceShadowMap);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (BaseShadowMap == null) { return; }
        
        CommandBuffer cmd = CommandBufferPool.Get("Variance Shadow Mapping");
        cmd.Clear();
         
        using (new ProfilingScope(cmd, m_ProfilingSamplerCopy))
        {
            // Copy shadowmap to custom buffer and setup two moments
            Blitter.BlitCameraTexture(cmd, BaseShadowMap, VarianceShadowMap, _BufferMaterial, 0);

            // Set up for blur passes
            _BufferMaterial.SetInteger("_BlurRadius", m_EVSMParameters.BlurRadius);
        }

        // Burn this entire logic to the ground
        // Do it better next time
        using (new ProfilingScope(cmd, m_ProfilingSamplerHorizontal))
        {
            // Blur each shadow slice horizontally
            for (int i = 0; i < sliceCount; i++)
            {
                if (AdditionalLightParms[ShadowSliceToAdditionalLight[i]].z == 1)
                {
                    int cubeFace = i - (int)AdditionalLightParms[ShadowSliceToAdditionalLight[i]].w;
                    if (cubeFace == 0)
                    {
                        VisibleLight PointLight = renderingData.lightData.visibleLights[AdditionalLightIndexToVisibleLightIndex[ShadowSliceToAdditionalLight[i]]];

                        int firstSliceIndex = (int)AdditionalLightParms[ShadowSliceToAdditionalLight[i]].w;
                        int sliceResolution = ShadowSlices[firstSliceIndex].resolution;

                        // Pretty accurate 
                        int paddingPixelCount = GetPaddingPixelCount(PointLight, sliceResolution);

                        //Get compute shader kernel handle
                        //Cache it? It shouldn't change? Maybe use a const? Is this consistently 0?
                        int kernelHandle = _CubemapBlurX.FindKernel("CubemapBlurX");

                        //Set face offsets in atlas
                        Vector2[] FaceOffsets = ComputeCubemapFaceOffsets(firstSliceIndex);
                        cmd.SetBufferData(FaceOffsetsBuffer, FaceOffsets);

                        // Set compute shader parameters
                        cmd.SetComputeBufferParam(_CubemapBlurX, kernelHandle, "_CubeSliceOffsets", FaceOffsetsBuffer);

                        cmd.SetComputeIntParam(_CubemapBlurX, "_FovPaddingPx", paddingPixelCount);
                        cmd.SetComputeIntParam(_CubemapBlurX, "_Resolution", sliceResolution);
                        cmd.SetComputeIntParam(_CubemapBlurX, "_Radius", m_EVSMParameters.BlurRadius);

                        cmd.SetComputeTextureParam(_CubemapBlurX, kernelHandle, "_InputBuffer", VarianceShadowMap);
                        cmd.SetComputeTextureParam(_CubemapBlurX, kernelHandle, "_OutputBuffer", ShadowMapBackBuffer);

                        // Dispach compute shader for all six faces
                        cmd.DispatchCompute(_CubemapBlurX, kernelHandle, 1, sliceResolution / 64, 6);
                    }
                }
                else
                {
                    // Compute rect for current shadow slice
                    ShadowSliceData slice = ShadowSlices[i];
                    Rect sliceRect = new Rect(slice.offsetX, slice.offsetY, slice.resolution, slice.resolution);

                    cmd.SetGlobalInteger("_LowBounds", slice.offsetX);
                    cmd.SetGlobalInteger("_UpBounds", slice.offsetX + slice.resolution);

                    // Perform a horizontal box blur pass
                    BlitShadowSlice(cmd, VarianceShadowMap, ShadowMapBackBuffer, sliceRect, _BufferMaterial, 1);
                }
            }
        }

        // This code is horrifying
        using (new ProfilingScope(cmd, m_ProfilingSamplerVertical))
        {
            // Blur each shadow slice vertically
            for (int i = 0; i < sliceCount; i++)
            {
                if (AdditionalLightParms[ShadowSliceToAdditionalLight[i]].z == 1)
                {
                    int cubeFace = i - (int)AdditionalLightParms[ShadowSliceToAdditionalLight[i]].w;
                    if (cubeFace == 0)
                    {
                        VisibleLight PointLight = renderingData.lightData.visibleLights[AdditionalLightIndexToVisibleLightIndex[ShadowSliceToAdditionalLight[i]]];

                        int firstSliceIndex = (int)AdditionalLightParms[ShadowSliceToAdditionalLight[i]].w;
                        int sliceResolution = ShadowSlices[firstSliceIndex].resolution;

                        // Pretty accurate 
                        int paddingPixelCount = GetPaddingPixelCount(PointLight, sliceResolution);

                        //Get compute shader kernel handle
                        //Cache it? It shouldn't change? Maybe use a const? Is this consistently 0?
                        int kernelHandle = _CubemapBlurY.FindKernel("CubemapBlurY");

                        //Set face offsets in atlas
                        Vector2[] FaceOffsets = ComputeCubemapFaceOffsets(firstSliceIndex);
                        cmd.SetBufferData(FaceOffsetsBuffer, FaceOffsets);

                        // Set compute shader parameters
                        cmd.SetComputeBufferParam(_CubemapBlurY, kernelHandle, "_CubeSliceOffsets", FaceOffsetsBuffer);

                        cmd.SetComputeIntParam(_CubemapBlurY, "_FovPaddingPx", paddingPixelCount);
                        cmd.SetComputeIntParam(_CubemapBlurY, "_Resolution", sliceResolution);
                        cmd.SetComputeIntParam(_CubemapBlurY, "_Radius", m_EVSMParameters.BlurRadius);

                        cmd.SetComputeTextureParam(_CubemapBlurY, kernelHandle, "_InputBuffer", ShadowMapBackBuffer);
                        cmd.SetComputeTextureParam(_CubemapBlurY, kernelHandle, "_OutputBuffer", VarianceShadowMap);

                        // Dispach compute shader for all six faces
                        cmd.DispatchCompute(_CubemapBlurY, kernelHandle, sliceResolution / 64, 1, 6);
                    }
                }
                else
                {
                    // Compute rect for current shadow slice
                    ShadowSliceData slice = ShadowSlices[i];
                    Rect sliceRect = new Rect(slice.offsetX, slice.offsetY, slice.resolution, slice.resolution);

                    cmd.SetGlobalInteger("_LowBounds", slice.offsetY);
                    cmd.SetGlobalInteger("_UpBounds", slice.offsetY + slice.resolution);

                    // Perform a vertical box blur pass
                    BlitShadowSlice(cmd, ShadowMapBackBuffer, VarianceShadowMap, sliceRect, _BufferMaterial, 2);
                }
            }
        }
        //Debug.Log("Cubemap Dispatches: " + CubemapDispatchCount);

        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        CommandBufferPool.Release(cmd);
    }

    private void ReflectRenderPassData(RenderingData renderingData)
    {
        UniversalRenderer renderer = renderingData.cameraData.renderer as UniversalRenderer;
        FieldInfo shadowCasterInfo = renderer.GetType().GetField("m_AdditionalLightsShadowCasterPass", BindingFlags.NonPublic | BindingFlags.Instance);
        AdditionalShadowCasterPass = (AdditionalLightsShadowCasterPass)shadowCasterInfo.GetValue(renderer);

        if (AdditionalShadowCasterPass == null) { return; }

        ShadowSliceField = AdditionalShadowCasterPass.GetType().GetField("m_AdditionalLightsShadowSlices", BindingFlags.NonPublic | BindingFlags.Instance);

        ShadowSliceToLightField = AdditionalShadowCasterPass.GetType().GetField("m_ShadowSliceToAdditionalLightIndex", BindingFlags.NonPublic | BindingFlags.Instance);

        AdditionalLightsShadowParmsField = AdditionalShadowCasterPass.GetType().GetField("m_AdditionalLightIndexToShadowParams", BindingFlags.NonPublic | BindingFlags.Instance);

        ShadowRTField = AdditionalShadowCasterPass.GetType().GetField("m_AdditionalLightsShadowmapHandle", BindingFlags.NonPublic | BindingFlags.Instance);

        AdditionalLightToVisibleLightField = AdditionalShadowCasterPass.GetType().GetField("m_AdditionalLightIndexToVisibleLightIndex", BindingFlags.NonPublic | BindingFlags.Instance);
    }

    public void ReleaseTargets()
    {
        VarianceShadowMap?.Release();
        ShadowMapBackBuffer?.Release();
        FaceOffsetsBuffer?.Release();

        FaceOffsetsBuffer = null;
    }

    // Conservative with Mathf.Floor, might be 1 pixel too low, either way, still improves things
    private int GetPaddingPixelCount(VisibleLight ShadowLight, int sliceResolution)
    {
        // I stole this part from ShadowUtils
        float frustumSize;
        float fovBias = URPInternalReferences.ExtractPointLightShadowFrustumFovBiasInDegrees(sliceResolution, (ShadowLight.light.shadows == LightShadows.Soft));
        // Note: the same fovBias was also used to compute ShadowUtils.ExtractPointLightMatrix
        float cubeFaceAngle = 90 + fovBias;
        frustumSize = Mathf.Tan(cubeFaceAngle * 0.5f * Mathf.Deg2Rad) * ShadowLight.range; // half-width (in world-space units) of shadow frustum's "far plane"

        // My code :D
        float paddingRatio = (frustumSize - ShadowLight.range) / frustumSize; // Light range is the same as the frustum size of a perfect 45 degree frustum

        return Mathf.FloorToInt((sliceResolution / 2) * paddingRatio); // Theoretically the number of pixels over 90 Degrees (On all sides)
    }

    // Draw an individual shadow slice with a custom viewport
    private void BlitShadowSlice(CommandBuffer cmd, RTHandle source, RTHandle destination, Rect viewport, Material material, int pass)
    {
        CoreUtils.SetRenderTarget(cmd, destination);
        cmd.SetViewport(viewport);
        Blitter.BlitTexture(cmd, source, new Vector4(viewport.width / source.rt.width, viewport.height / source.rt.height, viewport.x / source.rt.width, viewport.y / source.rt.height), material, pass);
    }


    private Vector2[] ComputeCubemapFaceOffsets(int FirstSlice)
    {
        // POS_X 0
        // NEG_X 1
        // POS_Y 2
        // NEG_Y 3
        // POS_Z 4
        // NEG_Z 5
        Vector2[] Offsets = new Vector2[6];
        for (int i = 0; i < 6; i++)
        {
            ShadowSliceData slice = ShadowSlices[FirstSlice + i];
            Offsets[i] = new Vector2(slice.offsetX, slice.offsetY);
        }
        
        return Offsets;
    }

    // Leftovers
    private int ComputeLightCount()
    {
        int count = 0;
        int i = 0;
        while (i < sliceCount) 
        {
            if (AdditionalLightParms[ShadowSliceToAdditionalLight[i]].z == 1) { i += 6; }
            else { i++; }
            count++;
        }

        return count;
    }
}
