using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal.Internal;

public static class URPInternalReferences
{
    public static float ExtractPointLightShadowFrustumFovBiasInDegrees(int shadowSliceResolution, bool shadowFiltering)
    {
        return AdditionalLightsShadowCasterPass.GetPointLightShadowFrustumFovBiasInDegrees(shadowSliceResolution, shadowFiltering);
    }


    // I just need some spot to dump this
    public static class BlitShaderIDs
    {
        public static readonly int _BlitTexture = Shader.PropertyToID("_BlitTexture");
        public static readonly int _BlitCubeTexture = Shader.PropertyToID("_BlitCubeTexture");
        public static readonly int _BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
        public static readonly int _BlitScaleBiasRt = Shader.PropertyToID("_BlitScaleBiasRt");
        public static readonly int _BlitMipLevel = Shader.PropertyToID("_BlitMipLevel");
        public static readonly int _BlitTextureSize = Shader.PropertyToID("_BlitTextureSize");
        public static readonly int _BlitPaddingSize = Shader.PropertyToID("_BlitPaddingSize");
        public static readonly int _BlitDecodeInstructions = Shader.PropertyToID("_BlitDecodeInstructions");
        public static readonly int _InputDepth = Shader.PropertyToID("_InputDepthTexture");
    }
}
