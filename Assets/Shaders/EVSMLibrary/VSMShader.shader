// Thanks alexanderameye for the really smart blur algorithm implementation
// https://gist.github.com/alexanderameye/9bb6b081d3dac7dfb655128b0b3b5e91
Shader "Hidden/EVSMShader"
{
    Properties
    {
        //_BlurRadius ("BlurRadius", Integer) = 0
    }
    SubShader
    {
        Tags{ "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        // No culling or depth
        ZWrite Off Cull Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // The Blit.hlsl file provides the vertex shader (Vert),
            // input structure (Attributes) and output strucutre (Varyings)
            //#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Assets/Shaders/EVSMLibrary/MeshBlitUtils.hlsl" // Includes Blit.hlsl

            // Per shader pass vertex func now
            //#pragma vertex Vert
            #pragma fragment frag
            
            //StructuredBuffer<float4> _AtlasSplitData;
            //int _SliceCount;
            int _BlurRadius;



            // TODO: Move to int4, but later
            int _LowBounds;
            int _UpBounds;



            float4 _BlitTexture_TexelSize;
        ENDHLSL


        Pass
        {
            Name "Copy Texture"

            HLSLPROGRAM

            #pragma vertex Vert

            // Optimize or something
            float2 frag (Varyings i) : SV_Target
            {
                float depth = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_PointClamp, i.texcoord, 0).x;
                
#if UNITY_REVERSED_Z
                depth = 1 - depth;
#endif

                float2 moments;
                
                // First moment is the depth itself.
                moments.x = depth;
                 
                // Compute partial derivatives of depth.
                float dx = ddx(depth);
                float dy = ddy(depth);

                // Compute second moment over the pixel extents.
                moments.y = depth * depth + 0.25 * (dx * dx + dy * dy);
                
                return moments;
            }

            ENDHLSL
        }

        Pass
        {
            Name "Horizontal Blur"

            HLSLPROGRAM 

            #pragma vertex Vert

            float2 frag(Varyings i) : SV_Target
            {
                int upBoundD = _UpBounds - i.positionCS.x;
                int lowBoundD = _LowBounds - i.positionCS.x;

                int distVal = lerp(upBoundD, lowBoundD, step(abs(lowBoundD), abs(upBoundD)));

                // Compute samples count
                int samples = 2 * _BlurRadius + 1;
                int sampleClamp = samples - _BlurRadius + abs(distVal);
                samples = min(samples, sampleClamp);

                float2 texelSize = _BlitTexture_TexelSize.xy;
                half2 sum = 0; // consider a float if that's something that matters

                for (int x = 0; x < samples; x++) {
                    float2 offset = float2((x - _BlurRadius) * sign(distVal), 0);

                    sum += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_PointClamp, i.texcoord + offset * texelSize, 0).xy;
                }

                //return float2(_UpBounds,0);

                return sum / samples;
                //return float2(i.sliceBounds.y)
                //return sign(distVal);
            }

            ENDHLSL
        }

        Pass
        {
            Name "Vertical Blur"

            HLSLPROGRAM

            #pragma vertex Vert

            float2 frag(Varyings i) : SV_Target
            {
                // return SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, i.texcoord);
                int upBoundD = _UpBounds - i.positionCS.y;
                int lowBoundD = _LowBounds - i.positionCS.y;

                int distVal = lerp(upBoundD, lowBoundD, step(abs(lowBoundD), abs(upBoundD)));

                // Compute samples count
                int samples = 2 * _BlurRadius + 1;
                int sampleClamp = samples - _BlurRadius + abs(distVal);
                samples = min(samples, sampleClamp);

                float2 texelSize = _BlitTexture_TexelSize.xy;
                half2 sum = 0; // consider a float if that's something that matters

                for (int y = 0; y < samples; y++) {
                    float2 offset = float2(0, (y - _BlurRadius) * sign(distVal));

                    sum += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_PointClamp, i.texcoord + offset * texelSize, 0).xy;
                }

                return sum/samples;
                //return sign(distVal);
            }

            ENDHLSL
        }

        Pass
        {
            Name "Test Pass"

            HLSLPROGRAM

            #pragma vertex MeshVert

            float2 frag(MeshVaryings i) : SV_Target
            {
                return i.sliceBounds;
            }

            ENDHLSL
        }
    }
}




//Shader "Hidden/EVSMShader"
//{
//    Properties
//    {
//        _MainTex("Texture", 2D) = "white" {}
//    }
//        SubShader
//    {
//        Tags{ "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "RenderType" = "Opaque" }
//        // No culling or depth
//        ZWrite Off Cull Off
//
//        HLSLINCLUDE
//        #pragma vertex vert
//        #pragma fragment frag
//
//        #include "UnityCG.cginc"
//
//
//        struct Attributes
//        {
//            float4 vertex : POSITION;
//            float2 uv : TEXCOORD0;
//        };
//
//        struct VertexToPixel
//        {
//            float2 uv : TEXCOORD0;
//            float4 vertex : SV_POSITION;
//        };
//
//        VertexToPixel vert(Attributes v)
//        {
//            VertexToPixel data;
//            data.vertex = UnityObjectToClipPos(v.vertex);
//            data.uv = v.uv;
//            return data;
//        }
//
//
//        sampler2D _MainTex;
//
//        ENDHLSL
//
//        Pass
//        {
//            Name "Copy Texture"
//
//            HLSLPROGRAM
//
//            float2 frag(VertexToPixel i) : SV_Target
//            {
//                float2 Texcol = tex2D(_MainTex, i.uv);
//
//                float2 col;
//                col.rg = Texcol.r;
//                col.g = col.g * col.g;
//
//                // just invert the colors
//                //col.rgb = 1 - col.rgb;
//                return col;
//            }
//
//            ENDHLSL
//        }
//
//        Pass
//        {
//            Name "Horizontal Blur"
//
//            HLSLPROGRAM
//
//            float2 frag(VertexToPixel i) : SV_Target
//            {
//                float2 Texcol = tex2D(_MainTex, i.uv);
//                //float2 col = Texcol;
//
//                // just invert the colors
//                //col.rg = col.gr;
//
//                return Texcol;
//            }
//
//            ENDHLSL
//        }
//    }
//}
