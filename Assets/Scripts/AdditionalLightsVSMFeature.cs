using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DisallowMultipleRendererFeature]
public class AdditionalLightVSMRenderFeature : ScriptableRendererFeature
{
    public Shader copyBufferShader;
    public ComputeShader cubemapBlurX;
    public ComputeShader cubemapBlurY;

    [System.Serializable] 
    public struct VSMParameters
    {
        public int BlurRadius;
    }
    public VSMParameters Quality;
    

    private AdditionalVSMRenderPass m_renderPass = null;

    public override void Create()
    {
        m_renderPass?.ReleaseTargets();
        m_renderPass = new AdditionalVSMRenderPass(copyBufferShader, cubemapBlurX, cubemapBlurY, ref Quality);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (copyBufferShader == null || cubemapBlurX == null || cubemapBlurY == null)
        {
            //Debug.Log("Missing Arguments");
            return;
        }

        renderer.EnqueuePass(m_renderPass);
    }

    protected override void Dispose(bool disposing)
    {
        m_renderPass.ReleaseTargets();
        base.Dispose(disposing);
    }
}
