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
}
