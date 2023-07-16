#ifndef EVSM_SHADOWS_INCLUDED
#define EVSM_SHADOWS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
//#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

TEXTURE2D(_VarianceShadowMap);
SAMPLER(sampler_VarianceShadowMap);
//SamplerState sampler_trilinear_clamp_aniso16;

// Credit for my discovery of this function goes to Wicked Engine: https://github.com/turanszkij/WickedEngine/blob/master/WickedEngine/shaders/shadowHF.hlsli
// This is used to clamp the uvs to last texel center to avoid sampling on the border and overfiltering into a different shadow
float2 shadow_border_shrink(float2 shadow_coord)
{
    //float2 shadow_resolution = light.shadowAtlasMulAdd.xy * GetFrame().shadow_atlas_resolution;
    //float border_size = 1.5;
    //shadow_uv = clamp(shadow_uv * shadow_resolution, border_size, shadow_resolution - border_size) / shadow_resolution;

    return shadow_coord;
}


// conversion helper for VSM flavors
// Chebychev's inequality (one-tailed version)
// P( x >= t ) <= pmax(t) := sigma^2 / (sigma^2 + (t - u)^2)
// for us t is depth, u is E(x) i.d. the blurred depth
//float ShadowMoments_ChebyshevsInequality(float2 moments, float depth, float minVariance, float lightLeakBias)
//{
//    // variance sig^2 = E(x^2) - E(x)^2
//    float variance = max(moments.y - (moments.x * moments.x), minVariance);
//
//    // probabilistic upper bound
//    float mD = depth - moments.x;
//    float p = variance / (variance + mD * mD);
//
//    p = saturate((p - lightLeakBias) / (1.0 - lightLeakBias));
//    return max(p, depth <= moments.x);
//}

// One-tailed inequality valid if t > Moments.x
float VarianceShadowMapping(float2 moments, float depth, float minVariance, float lightLeakBias)
{
    float p = step(depth, moments.x);
    float variance = max(moments.y - (moments.x * moments.x), minVariance);

    // Compute probabalistic upper bound
    float d = depth - moments.x;
    float p_max = variance / (variance + d * d);
    
    p_max = saturate((p_max - lightLeakBias) / (1.0 - lightLeakBias));

    return saturate(max(p, p_max));
} 


// Shadow sampling 
real SampleEVSMShadowmap(TEXTURE2D_PARAM(ShadowMap, sampler_ShadowMap), float4 shadowCoord, ShadowSamplingData samplingData, half4 shadowParams, bool isPerspectiveProjection = true)
{
    // Compiler will optimize this branch away as long as isPerspectiveProjection is known at compile time
    if (isPerspectiveProjection)
        shadowCoord.xyz /= shadowCoord.w;

#if UNITY_REVERSED_Z
    shadowCoord.z = 1 - shadowCoord.z;
#endif

    // Setup values
    real attenuation;
    real shadowStrength = shadowParams.x;


    // VSM sampling
    float2 Moments = SAMPLE_TEXTURE2D(ShadowMap, sampler_ShadowMap, shadowCoord.xy).xy;
    //return shadowCoord.z;

    attenuation = (real)VarianceShadowMapping(Moments, shadowCoord.z, 0.00002, 0.4);
    //attenuation = 1 - attenuation;

    // Integrate shadowStrength
    attenuation = LerpWhiteTo(attenuation, shadowStrength);

    // Shadow coords that fall out of the light frustum volume must always return attenuation 1.0
    // TODO: We could use branch here to save some perf on some platforms.
    return BEYOND_SHADOW_FAR(shadowCoord) ? 1.0 : attenuation;
}






// Specifically for realtime shadows

// returns 0.0 if position is in light's shadow
// returns 1.0 if position is in light
half AdditionalLightEVSMRealtimeShadow(int lightIndex, float3 positionWS, half3 lightDirection)
{
#if defined(ADDITIONAL_LIGHT_CALCULATE_SHADOWS)
    ShadowSamplingData shadowSamplingData = GetAdditionalLightShadowSamplingData(lightIndex);

    half4 shadowParams = GetAdditionalLightShadowParams(lightIndex);

    int shadowSliceIndex = shadowParams.w;
    if (shadowSliceIndex < 0)
        return 1.0;

    half isPointLight = shadowParams.z;

    UNITY_BRANCH
        if (isPointLight)
        {
            // This is a point light, we have to find out which shadow slice to sample from
            float cubemapFaceId = CubeMapFaceID(-lightDirection);
            shadowSliceIndex += cubemapFaceId;
        }

#if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
    float4 shadowCoord = mul(_AdditionalLightsWorldToShadow_SSBO[shadowSliceIndex], float4(positionWS, 1.0));
#else
    float4 shadowCoord = mul(_AdditionalLightsWorldToShadow[shadowSliceIndex], float4(positionWS, 1.0));
#endif

    return SampleEVSMShadowmap(TEXTURE2D_ARGS(_VarianceShadowMap, sampler_VarianceShadowMap), shadowCoord, shadowSamplingData, shadowParams, true);
#else
    return half(1.0);
#endif
}







// Shadow getting function

half AdditionalLightEVSMShadow(int lightIndex, float3 positionWS, half3 lightDirection, half4 shadowMask, half4 occlusionProbeChannels)
{
    half realtimeShadow = AdditionalLightEVSMRealtimeShadow(lightIndex, positionWS, lightDirection);

#ifdef CALCULATE_BAKED_SHADOWS
    half bakedShadow = BakedShadow(shadowMask, occlusionProbeChannels);
#else
    half bakedShadow = half(1.0);
#endif

#ifdef ADDITIONAL_LIGHT_CALCULATE_SHADOWS
    half shadowFade = GetAdditionalLightShadowFade(positionWS);
#else
    half shadowFade = half(1.0);
#endif

    return MixRealtimeAndBakedShadows(realtimeShadow, bakedShadow, shadowFade);
}


































// helper for EVSM
float2 ShadowMoments_WarpDepth(float depth, float2 exponents)
{
    // Rescale depth into [-1;1]
    depth = 2.0 * depth - 1.0;
    float pos = exp(exponents.x * depth);
    float neg = -exp(-exponents.y * depth);
    return float2(pos, neg);
}

#endif