#ifndef EVSM_LIGHTS_INCLUDED
#define EVSM_LIGHTS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
#include "Assets/Shaders/EVSMLibrary/EVSMShadows.hlsl"

Light GetAdditionalEVSMLight(uint i, float3 positionWS, half4 shadowMask)
{
#if USE_FORWARD_PLUS
    int lightIndex = i;
#else
    int lightIndex = GetPerObjectLightIndex(i);
#endif
    Light light = GetAdditionalPerObjectLight(lightIndex, positionWS);

#if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
    half4 occlusionProbeChannels = _AdditionalLightsBuffer[lightIndex].occlusionProbeChannels;
#else
    half4 occlusionProbeChannels = _AdditionalLightsOcclusionProbes[lightIndex];
#endif
    light.shadowAttenuation = AdditionalLightEVSMShadow(lightIndex, positionWS, light.direction, shadowMask, occlusionProbeChannels);
#if defined(_LIGHT_COOKIES)
    real3 cookieColor = SampleAdditionalLightCookie(lightIndex, positionWS);
    light.color *= cookieColor;
#endif

    return light;
}


Light GetAdditionalEVSMLight(uint i, InputData inputData, half4 shadowMask, AmbientOcclusionFactor aoFactor)
{
    Light light = GetAdditionalEVSMLight(i, inputData.positionWS, shadowMask);

#if defined(_SCREEN_SPACE_OCCLUSION) && !defined(_SURFACE_TYPE_TRANSPARENT)
    if (IsLightingFeatureEnabled(DEBUGLIGHTINGFEATUREFLAGS_AMBIENT_OCCLUSION))
    {
        light.color *= aoFactor.directAmbientOcclusion;
    }
#endif

    return light;
}




////////////////////////////////////////////////////////////////////////////////
/// PBR lighting...
////////////////////////////////////////////////////////////////////////////////
half4 UniversalEVSMFragmentPBR(InputData inputData, SurfaceData surfaceData)
{
#if defined(_SPECULARHIGHLIGHTS_OFF)
    bool specularHighlightsOff = true;
#else
    bool specularHighlightsOff = false;
#endif
    BRDFData brdfData;

    // NOTE: can modify "surfaceData"...
    InitializeBRDFData(surfaceData, brdfData);

#if defined(DEBUG_DISPLAY)
    half4 debugColor;

    if (CanDebugOverrideOutputColor(inputData, surfaceData, brdfData, debugColor))
    {
        return debugColor;
    }
#endif

    // Clear-coat calculation...
    BRDFData brdfDataClearCoat = CreateClearCoatBRDFData(surfaceData, brdfData);
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
    uint meshRenderingLayers = GetMeshRenderingLayer();
    Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);

    // NOTE: We don't apply AO to the GI here because it's done in the lighting calculation below...
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

    LightingData lightingData = CreateLightingData(inputData, surfaceData);

    lightingData.giColor = GlobalIllumination(brdfData, brdfDataClearCoat, surfaceData.clearCoatMask,
        inputData.bakedGI, aoFactor.indirectAmbientOcclusion, inputData.positionWS,
        inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
    {
        lightingData.mainLightColor = LightingPhysicallyBased(brdfData, brdfDataClearCoat,
            mainLight,
            inputData.normalWS, inputData.viewDirectionWS,
            surfaceData.clearCoatMask, specularHighlightsOff);
    }

#if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();

#if USE_FORWARD_PLUS
    for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        FORWARD_PLUS_SUBTRACTIVE_LIGHT_CHECK

            Light light = GetAdditionalEVSMLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
                inputData.normalWS, inputData.viewDirectionWS,
                surfaceData.clearCoatMask, specularHighlightsOff);
        }
    }
#endif

    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalEVSMLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
    {
        lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
            inputData.normalWS, inputData.viewDirectionWS,
            surfaceData.clearCoatMask, specularHighlightsOff);
    }
    LIGHT_LOOP_END
#endif

#if defined(_ADDITIONAL_LIGHTS_VERTEX)
        lightingData.vertexLightingColor += inputData.vertexLighting * brdfData.diffuse;
#endif

#if REAL_IS_HALF
    // Clamp any half.inf+ to HALF_MAX
    return min(CalculateFinalColor(lightingData, surfaceData.alpha), HALF_MAX);
#else
    return CalculateFinalColor(lightingData, surfaceData.alpha);
#endif
}
#endif