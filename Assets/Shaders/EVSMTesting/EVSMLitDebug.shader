Shader "Kabinet/EVSMLitDebug"
{
    Properties
    {
        
    }
    SubShader
    {
        Tags{"RenderPipeline" = "UniversalPipeline"}

        Pass {
            Name "ForwardLit"
            Tags{"LightMode" = "UniversalForward"} // Pass specific tags. 
            
            HLSLPROGRAM 
                #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
                #pragma multi_compile_fragment _ _SHADOWS_SOFT
                #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS

                #pragma multi_compile _ _ADDITIONAL_LIGHTS

                #pragma vertex Vertex
                #pragma fragment Fragment     


                #include "Assets/Shaders/EVSMTesting/EVSMDebugForwardLit.hlsl"
            ENDHLSL
        }


        Pass {
            Name "ShadowCaster"
            Tags{"LightMode" = "ShadowCaster"}

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Assets/Shaders/EVSMTesting/EVSMDebugShadowCaster.hlsl"
            ENDHLSL
        }
    }
}
