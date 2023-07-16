#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Assets/Shaders/EVSMLibrary/EVSMLights.hlsl"

struct Attributes {
	float3 positionOS : POSITION; // Position in object space
	float3 normalOS : NORMAL; // Normals in object space
};


struct Varyings
{
	float4 positionCS : SV_POSITION;

	float3 positionWS : TEXCOORD0;
	float3 normalWS : TEXCOORD1;
};


Varyings Vertex(Attributes input) {
	Varyings output;

	VertexPositionInputs posnInputs = GetVertexPositionInputs(input.positionOS);
	VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

	output.positionCS = posnInputs.positionCS;
	output.positionWS = posnInputs.positionWS;

	output.normalWS = normInputs.normalWS;

	return output;
}


float4 Fragment(Varyings input) : SV_TARGET{
	// Initialize input structs
	InputData lightingInput = (InputData)0;
	SurfaceData surfaceData = (SurfaceData)0;

	// Lighting Data
	lightingInput.normalWS = normalize(input.normalWS);
	lightingInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);

	// Surface Data
	surfaceData.albedo = float3(1, 1, 1);
	surfaceData.alpha = 1;



	half4 shadowMask = CalculateShadowMask(lightingInput);
	AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(lightingInput, surfaceData);
	Light mainLight = GetMainLight(lightingInput, shadowMask, aoFactor);

	MixRealtimeAndBakedGI(mainLight, lightingInput.normalWS, lightingInput.bakedGI, aoFactor);
	LightingData lightingData = CreateLightingData(lightingInput, surfaceData);


	lightingData.mainLightColor += CalculateBlinnPhong(mainLight, lightingInput, surfaceData);



	#if defined(_ADDITIONAL_LIGHTS)
	uint pixelLightCount = GetAdditionalLightsCount();
	for (uint lightIndex = 0; lightIndex < pixelLightCount; lightIndex++) {
		Light light = GetAdditionalEVSMLight(lightIndex, input.positionWS, shadowMask);
		lightingData.additionalLightsColor += CalculateBlinnPhong(light, lightingInput, surfaceData);
		//return float4(light.shadowAttenuation,0,0,1);
	}
	#endif

	return CalculateFinalColor(lightingData, surfaceData.alpha);

	//return UniversalFragmentBlinnPhong(lightingInput, surfaceData);
}