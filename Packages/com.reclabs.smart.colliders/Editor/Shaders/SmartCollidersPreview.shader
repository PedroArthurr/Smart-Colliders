// Editor-only collider preview. Drawn with Graphics.DrawMeshNow from SceneView GUI, so it is
// pipeline-agnostic (Built-in / URP / HDRP). Pass 0 = visible surface, pass 1 = occluded (x-ray).
Shader "Hidden/SmartColliders/Preview"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 0.2)
        _RimStrength ("Rim Strength", Float) = 1.4
        _OccludedAlpha ("Occluded Alpha Scale", Float) = 0.3
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    float4 _Color;
    float _RimStrength;
    float _OccludedAlpha;

    struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
    struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 v : TEXCOORD1; };

    v2f vert (appdata i)
    {
        v2f o;
        float3 world = mul(unity_ObjectToWorld, i.vertex).xyz;
        o.pos = UnityWorldToClipPos(world);
        o.n = UnityObjectToWorldNormal(i.normal);
        o.v = _WorldSpaceCameraPos - world;
        return o;
    }

    float4 shade (v2f i, float alphaScale)
    {
        float3 n = normalize(i.n);
        float3 v = normalize(i.v);
        float ndv = saturate(dot(n, v));
        float rim = pow(1.0 - ndv, 2.5);
        // Soft key light from the camera's upper-left keeps volumes readable without scene lights.
        float key = saturate(dot(n, normalize(v + float3(0, 0.6, 0)))) * 0.15;
        float3 rgb = _Color.rgb * (0.85 + key) + rim * _RimStrength * 0.35;
        float a = _Color.a * (0.65 + rim * _RimStrength) * alphaScale;
        return float4(rgb, saturate(a));
    }

    float4 fragVisible (v2f i) : SV_Target { return shade(i, 1.0); }
    float4 fragOccluded (v2f i) : SV_Target { return shade(i, _OccludedAlpha); }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back
        Offset -1, -1

        Pass
        {
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragVisible
            ENDCG
        }

        Pass
        {
            ZTest Greater
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOccluded
            ENDCG
        }
    }
}
