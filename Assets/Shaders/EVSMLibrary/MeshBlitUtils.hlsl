#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

struct MeshAttributes
{
    float4 positionOS       : POSITION;
    float2 uv               : TEXCOORD0;
    int4 sliceBounds        : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct MeshVaryings
{
    float4 positionCS : SV_POSITION;
    float2 texcoord   : TEXCOORD0;
    nointerpolation int4 sliceBounds  : TEXCOORD1;
    UNITY_VERTEX_OUTPUT_STEREO
};


MeshVaryings MeshVert(MeshAttributes input)
{
    MeshVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float4 pos = input.positionOS;
    float2 uv = input.uv;

//#ifdef UNITY_PRETRANSFORM_TO_DISPLAY_ORIENTATION
//    pos = ApplyPretransformRotation(pos);
//#endif

#if !UNITY_UV_STARTS_AT_TOP // Why?
    uv.y = 1.0 - uv.y;
#endif
    output.positionCS = pos;
    output.positionCS.xy = output.positionCS.xy * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f); //convert to -1..1
    output.texcoord = uv;

    output.sliceBounds = input.sliceBounds;

    return output;
}