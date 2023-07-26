using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using System.Reflection;
using System;
using System.Runtime.InteropServices;
using static URPInternalReferences;
using Unity.Collections;

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
    ProfilingSampler m_ProfilingSamplerBlurPass = new ProfilingSampler("Blur Pass");
    AdditionalLightVSMRenderFeature.VSMParameters m_EVSMParameters;


    //Rendering variables
    Material _BufferMaterial;
    ComputeShader _CubemapBlurX, _CubemapBlurY;

    RTHandle BaseShadowMap;
    RTHandle VarianceShadowMap;
    RTHandle ShadowMapBackBuffer;

    ComputeBuffer FaceOffsetsBuffer, CubemapDataBuffer;

    MaterialPropertyBlock _BlitPropertyBlock = new MaterialPropertyBlock();
    float nearClipZ;

    // Reflected shadow data
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

    VertexAttributeDescriptor[] BlurMeshVertexLayout = new[]
    {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.SInt32, 4),
    };

    [StructLayout(LayoutKind.Sequential)]
    struct BlurMeshVertex
    {
        public Vector3 pos;
        public Vector2 uv;
        public Vector2Int xy, zw;

        public BlurMeshVertex(Vector3 position, Vector2 texcoord, Vector2Int Bounds_XY, Vector2Int Bounds_ZW)
        {
            pos = position;
            uv = texcoord;
            xy = Bounds_XY;
            zw = Bounds_ZW;
        }
    }


    List<Vector2Int> shadowedPointLightIndicies = new List<Vector2Int>(); // x is visible light index, y is first shadow slice index
    List<int> shadowedSpotLightIndicies = new List<int>(); // maps to shadow slice

    public AdditionalVSMRenderPass(Shader bufferCopyShader, ComputeShader cubemapBlurX, ComputeShader cubemapBlurY, ref AdditionalLightVSMRenderFeature.VSMParameters parameters)
    {
        if (_BufferMaterial == null) _BufferMaterial = CoreUtils.CreateEngineMaterial(bufferCopyShader);
        _CubemapBlurX = cubemapBlurX;
        _CubemapBlurY = cubemapBlurY;

        renderPassEvent = RenderPassEvent.AfterRenderingShadows;

        m_EVSMParameters = parameters;

        // Blitting Related
        nearClipZ = -1;
        if (SystemInfo.usesReversedZBuffer)
            nearClipZ = 1;
    } 

    // Consider moving to configure
    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
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

        RenderingUtils.ReAllocateIfNeeded(ref VarianceShadowMap, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_VarianceShadowMap");
        RenderingUtils.ReAllocateIfNeeded(ref ShadowMapBackBuffer, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_VarianceShadowMapBackBuffer");
        //VarianceShadowMap.rt.anisoLevel = 16;

        cmd.SetGlobalTexture(VarianceShadowMap.name, VarianceShadowMap);
        BuildLightList();

        // There has to be a better way to do this
        int desiredFaceBufferCount = shadowedPointLightIndicies.Count > 0 ? shadowedPointLightIndicies.Count * 6: 6;
        if (FaceOffsetsBuffer == null || FaceOffsetsBuffer.count != desiredFaceBufferCount)
        {
            FaceOffsetsBuffer?.Release();
            FaceOffsetsBuffer = null;

            FaceOffsetsBuffer = new ComputeBuffer(desiredFaceBufferCount, sizeof(float) * 2);
        }

        int desiredCubemapDataBufferCount = shadowedPointLightIndicies.Count > 0 ? shadowedPointLightIndicies.Count : 1;
        if (CubemapDataBuffer == null || CubemapDataBuffer.count != desiredCubemapDataBufferCount)
        {
            CubemapDataBuffer?.Release();
            CubemapDataBuffer = null;

            CubemapDataBuffer = new ComputeBuffer(desiredCubemapDataBufferCount, sizeof(int) * 2);
        }
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (BaseShadowMap == null) { return; }
        
        CommandBuffer cmd = CommandBufferPool.Get("Variance Shadow Mapping");
        cmd.Clear();

        CoreUtils.SetRenderTarget(cmd, ShadowMapBackBuffer);
        CoreUtils.ClearRenderTarget(cmd, ClearFlag.Color, Color.black);
         
        using (new ProfilingScope(cmd, m_ProfilingSamplerCopy))
        {
            // Copy shadowmap to custom buffer and setup two moments
            Blitter.BlitCameraTexture(cmd, BaseShadowMap, VarianceShadowMap, _BufferMaterial, 0);
        }

        // Blur pass
        if (m_EVSMParameters.BlurRadius > 0 && (shadowedPointLightIndicies.Count > 0 || shadowedSpotLightIndicies.Count > 0))
        {
            using (new ProfilingScope(cmd, m_ProfilingSamplerBlurPass))
            {
                // Set blur radius for the fragment blur material
                _BufferMaterial.SetInteger("_BlurRadius", m_EVSMParameters.BlurRadius);

                // Data for blur mesh
                List<BlurMeshVertex> Verticies = new List<BlurMeshVertex>();
                List<int> Indices = new List<int>();

                List<Vector2> FaceOffsetData = new List<Vector2>();
                List<Vector2Int> CubemapData = new List<Vector2Int>();

                int SummedPointResolutions = 0;

                Rect ShadowmapRect = new Rect(0, 0, VarianceShadowMap.rt.width, VarianceShadowMap.rt.height);
                // Build compute shader data for point lights
                for (int i = 0; i < shadowedPointLightIndicies.Count; i++)
                {
                    VisibleLight PointLight = renderingData.lightData.visibleLights[shadowedPointLightIndicies[i].x];

                    int firstSliceIndex = (int)AdditionalLightParms[ShadowSliceToAdditionalLight[shadowedPointLightIndicies[i].y]].w;
                    int sliceResolution = ShadowSlices[firstSliceIndex].resolution;

                    // Pretty accurate 
                    int paddingPixelCount = GetPaddingPixelCount(PointLight, sliceResolution);

                    //Set face offsets in atlas
                    FaceOffsetData.AddRange(ComputeCubemapFaceOffsets(firstSliceIndex));
                    CubemapData.Add(new Vector2Int(sliceResolution, paddingPixelCount));

                    SummedPointResolutions += sliceResolution;
                }

                // Dispatch compute shader for point lights
                if (shadowedPointLightIndicies.Count > 0)
                {
                    //Get compute shader kernel handle
                    //Cache it? It shouldn't change? Maybe use a const? Is this consistently 0?
                    int kernelHandleX = _CubemapBlurX.FindKernel("CubemapBlurX");
                    int kernelHandleY = _CubemapBlurY.FindKernel("CubemapBlurY");

                    cmd.SetBufferData(FaceOffsetsBuffer, FaceOffsetData);
                    cmd.SetBufferData(CubemapDataBuffer, CubemapData);

                    /* Set Compute Shader Parameters */

                    // X blur params
                    cmd.SetComputeBufferParam(_CubemapBlurX, kernelHandleX, "_CubeSliceOffsets", FaceOffsetsBuffer);
                    cmd.SetComputeBufferParam(_CubemapBlurX, kernelHandleX, "_CubemapData", CubemapDataBuffer);

                    cmd.SetComputeIntParam(_CubemapBlurX, "_Radius", m_EVSMParameters.BlurRadius);

                    cmd.SetComputeTextureParam(_CubemapBlurX, kernelHandleX, "_InputBuffer", VarianceShadowMap);
                    cmd.SetComputeTextureParam(_CubemapBlurX, kernelHandleX, "_OutputBuffer", ShadowMapBackBuffer);


                    // Y blur params
                    cmd.SetComputeBufferParam(_CubemapBlurY, kernelHandleY, "_CubeSliceOffsets", FaceOffsetsBuffer);
                    cmd.SetComputeBufferParam(_CubemapBlurY, kernelHandleY, "_CubemapData", CubemapDataBuffer);

                    cmd.SetComputeIntParam(_CubemapBlurY, "_Radius", m_EVSMParameters.BlurRadius);

                    cmd.SetComputeTextureParam(_CubemapBlurY, kernelHandleY, "_InputBuffer", ShadowMapBackBuffer);
                    cmd.SetComputeTextureParam(_CubemapBlurY, kernelHandleY, "_OutputBuffer", VarianceShadowMap);

                    /*    Dispatch Compute Shaders    */
                    /*  Covers all faces in one pass  */
                    cmd.DispatchCompute(_CubemapBlurY, kernelHandleY, SummedPointResolutions / 64, 1, 6); // Y blur
                    cmd.DispatchCompute(_CubemapBlurX, kernelHandleX, 1, SummedPointResolutions / 64, 6); // X blur 
                    
                }





                // Generate blur quads for spotlights
                for (int i = 0; i < shadowedSpotLightIndicies.Count; i++)
                {
                    // Compute rect for current shadow slice
                    ShadowSliceData slice = ShadowSlices[shadowedSpotLightIndicies[i]];
                    Rect sliceRect = new Rect(slice.offsetX, slice.offsetY, slice.resolution, slice.resolution);

                    Vector2Int HorizontalBounds = new Vector2Int(slice.offsetX, slice.offsetX + slice.resolution - 1);
                    Vector2Int VerticalBounds = new Vector2Int(slice.offsetY, slice.offsetY + slice.resolution - 1);

                    GenerateBlurQuad(sliceRect, ShadowmapRect, ref Verticies, ref Indices, HorizontalBounds, VerticalBounds);
                }


                // Mesh Generation from quad data
                Mesh blurPassMesh = new Mesh();
                blurPassMesh.SetVertexBufferParams(Verticies.Count, BlurMeshVertexLayout);
                blurPassMesh.SetVertexBufferData(Verticies, 0, 0, Verticies.Count);

                blurPassMesh.SetIndices(Indices, MeshTopology.Triangles, 0);

                // Blur the spotlights in one draw call
                BlitMeshToTarget(cmd, VarianceShadowMap, ShadowMapBackBuffer, blurPassMesh, _BufferMaterial, 1);
                BlitMeshToTarget(cmd, ShadowMapBackBuffer, VarianceShadowMap, blurPassMesh, _BufferMaterial, 2);
            }
        }

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
        CubemapDataBuffer?.Release();

        FaceOffsetsBuffer = null;
        CubemapDataBuffer = null;
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

    private void GenerateBlurQuad(Rect quadSize, Rect destinationSize, ref List<BlurMeshVertex> verticies, ref List<int> indicies, Vector2Int HorizontalBounds, Vector2Int VerticalBounds)
    {
        int startingVertex = verticies.Count;

        // Verts
        verticies.Add(new BlurMeshVertex(
            new Vector3(quadSize.x / destinationSize.width, quadSize.y / destinationSize.height, nearClipZ),       // 0, 0
            new Vector2(quadSize.x / destinationSize.width, quadSize.y / destinationSize.height),
            HorizontalBounds, VerticalBounds
        ));
        verticies.Add(new BlurMeshVertex(
            new Vector3(quadSize.xMax / destinationSize.width, quadSize.y / destinationSize.height, nearClipZ),    // 1, 0
            new Vector2(quadSize.xMax / destinationSize.width, quadSize.y / destinationSize.height),
            HorizontalBounds, VerticalBounds
        ));
        verticies.Add(new BlurMeshVertex(
            new Vector3(quadSize.x / destinationSize.width, quadSize.yMax / destinationSize.height, nearClipZ),    // 0, 1
            new Vector2(quadSize.x / destinationSize.width, quadSize.yMax / destinationSize.height),
            HorizontalBounds, VerticalBounds
        ));
        verticies.Add(new BlurMeshVertex(
            new Vector3(quadSize.xMax / destinationSize.width, quadSize.yMax / destinationSize.height, nearClipZ), // 1, 1
            new Vector2(quadSize.xMax / destinationSize.width, quadSize.yMax / destinationSize.height),
            HorizontalBounds, VerticalBounds
        )); 


        // Triangles

        //Lower left triangle
        indicies.Add(startingVertex + 0);
        indicies.Add(startingVertex + 2);
        indicies.Add(startingVertex + 1);

        //Upper right triangle
        indicies.Add(startingVertex + 2);
        indicies.Add(startingVertex + 3);
        indicies.Add(startingVertex + 1);
    }

    private void BlitMeshToTarget(CommandBuffer cmd, RTHandle source, RTHandle target, Mesh mesh, Material material, int pass)
    {
        _BlitPropertyBlock.SetTexture(URPInternalReferences.BlitShaderIDs._BlitTexture, source);
        _BlitPropertyBlock.SetVector(BlitShaderIDs._BlitScaleBias, Vector2.one);

        CoreUtils.SetRenderTarget(cmd, target);
        CoreUtils.SetViewport(cmd, target);
        //cmd.SetProjectionMatrix(Matrix4x4.Ortho(0, 1, 0, 1, -100, 100)); // Try without this at some point once it all works
        cmd.DrawMesh(mesh, Matrix4x4.identity, material, 0, pass, _BlitPropertyBlock);
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

    private void BuildLightList()
    {
        shadowedPointLightIndicies.Clear();
        shadowedSpotLightIndicies.Clear();

        // Collect list of shadowed lights
        for (int i = 0; i < sliceCount; i++)
        {
            int additionalLightIndex = ShadowSliceToAdditionalLight[i];

            if (AdditionalLightParms[additionalLightIndex].z == 1)
            {

                int cubeFace = i - (int)AdditionalLightParms[ShadowSliceToAdditionalLight[i]].w;
                if (cubeFace == 0)
                {
                    //It's a point light
                    shadowedPointLightIndicies.Add(new Vector2Int(AdditionalLightIndexToVisibleLightIndex[additionalLightIndex], i));
                }
            }
            else
            {
                // It's a spotlight
                shadowedSpotLightIndicies.Add(i);
            }
        }
    }
    
    private void AddPointLightBlurMeshFaces(int firstSliceIndex, Rect ShadowmapRect, ref List<BlurMeshVertex> Verticies, ref List<int> Indices)
    {
        // Compute rect for current shadow slice
        for (int face = 0; face < 6; face++)
        {
            ShadowSliceData slice = ShadowSlices[firstSliceIndex + face];
            Rect sliceRect = new Rect(slice.offsetX, slice.offsetY, slice.resolution, slice.resolution);

            Vector2Int HorizontalBounds = new Vector2Int(slice.offsetX, slice.offsetX + slice.resolution - 1);
            Vector2Int VerticalBounds = new Vector2Int(slice.offsetY, slice.offsetY + slice.resolution - 1);

            GenerateBlurQuad(sliceRect, ShadowmapRect, ref Verticies, ref Indices, HorizontalBounds, VerticalBounds);
        }
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
