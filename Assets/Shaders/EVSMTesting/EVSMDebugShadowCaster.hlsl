#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

struct Attributes {
	float4 positionOS   : POSITION;
	float3 normalOS     : NORMAL;
	float2 texcoord     : TEXCOORD0;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings {
	float2 uv           : TEXCOORD0;
	float4 positionCS   : SV_POSITION;
};

float3 _LightDirection;
float3 _LightPosition;

float4 GetShadowCasterPositionCS(float3 positionWS, float3 normalWS) {
	float3 lightDirectionWS = _LightDirection;

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
	float4 positionCS = TransformWorldToHClip(positionWS); // No bias if punctual light
#else
	float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS)); // Bias, if directional light
#endif

#if UNITY_REVERSED_Z
	positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
	positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

	return positionCS;
}


Varyings Vertex(Attributes input) {
	Varyings output;

	VertexPositionInputs posnInputs = GetVertexPositionInputs(input.positionOS);
	VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

	output.positionCS = GetShadowCasterPositionCS(posnInputs.positionWS, normInputs.normalWS);
	return output;
}

float4 Fragment(Varyings input) : SV_TARGET{
	return 0;
}