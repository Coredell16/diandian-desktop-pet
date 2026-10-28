Shader "Diandian/Feather"
{
    Properties
    {
        [HideInInspector] _MainTex("UV", 2D) = "white" {}
        _Color("羽毛底色", Color) = (0.35,0.36,0.34,1)
        _Mark("羽毛斑纹", Color) = (0.80,0.79,0.62,1)
        _Pattern("斑纹：0纯色 1珍珠 2横斑 3冠羽", Float) = 0
        _Smoothness("光泽", Range(0,1)) = 0.14
        _Feather("羽毛表面（裸皮和眼睛关闭）", Range(0,1)) = 1
        _Surface("纹理坐标：0单根羽毛 1连续体表", Float) = 0
        _DetailTex("细羽灰度纹理（中灰为中性）", 2D) = "gray" {}
        _DetailStrength("细羽纹理强度", Range(0,1)) = 0
        _AlbedoTex("真实羽色贴图", 2D) = "white" {}
        _AlbedoStrength("羽色贴图强度", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        struct Input { float2 uv_MainTex; float2 uv_DetailTex; float2 uv_AlbedoTex; float4 color : COLOR; };
        sampler2D _MainTex, _DetailTex, _AlbedoTex;
        fixed4 _Color, _Mark;
        float _Pattern, _Smoothness, _Feather, _Surface, _DetailStrength, _AlbedoStrength;

        float hash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        float softNoise(float2 p)
        {
            float2 cell = floor(p), f = frac(p);
            f = f * f * (3 - 2 * f);
            return lerp(lerp(hash21(cell), hash21(cell + float2(1,0)), f.x),
                lerp(hash21(cell + float2(0,1)), hash21(cell + 1), f.x), f.y);
        }

        // Suppress sub-pixel fibres instead of letting them flicker while the bird rotates.
        float fibre(float phase)
        {
            return sin(phase) * (1 - smoothstep(1.3, 5.5, fwidth(phase)));
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float2 uv = IN.uv_MainTex;
            float side = abs(uv.x - .5) * 2;
            float broad = softNoise(uv * float2(17, 27));
            float fine = softNoise(uv * float2(53, 91));
            float grain, rachis = 0, edgeShade = 1;
            if (_Surface > .5)
            {
                // Short down feathers read as soft, directional fibres, not scales.
                float flow = softNoise(uv * float2(23, 11));
                grain = fibre(uv.x * 1900 + uv.y * 67 + flow * 15) * .025;
                grain += (broad - .5) * .08 + (fine - .5) * .025;
            }
            else
            {
                grain = fibre(uv.y * 390 + side * 170 + broad * 3) * .028;
                grain += fibre(uv.y * 820 + side * 350) * .012;
                grain += (fine - .5) * .04;
                float shaftWidth = max(.012, fwidth(side) * 1.2);
                rachis = (1 - smoothstep(shaftWidth, shaftWidth * 2.6, side))
                    * (1 - smoothstep(.77, 1, uv.y));
                edgeShade = 1 - smoothstep(.73, 1, side) * .045;
            }
            float pattern = 0;
            if (_Pattern > .5 && _Pattern < 1.5)
            {
                // Pearl coverts have an irregular cream area framed by a narrow grey edge.
                float2 q = float2(side/.88, (uv.y-.63)/.37);
                float patch = dot(q,q) + (fine-.5)*.13 + (broad-.5)*.12;
                float aa = max(.035, fwidth(patch));
                pattern = 1 - smoothstep(.80-aa, 1.04+aa, patch);
            }
            else if (_Pattern > 1.5 && _Pattern < 2.5)
            {
                float band = sin(uv.y*48 + side*1.9 + broad*2.8);
                float aa = max(.12, fwidth(band));
                pattern = .28 + .57 * smoothstep(-.40-aa, .35+aa, band);
                pattern *= .80 + fine*.20;
            }
            else if (_Pattern > 2.5)
                pattern = 1-smoothstep(.18,.84,uv.y + (fine-.5)*.035);
            float3 tone = lerp(_Color.rgb, _Mark.rgb, pattern);
            tone = lerp(tone, tex2D(_AlbedoTex, IN.uv_AlbedoTex).rgb, _AlbedoStrength);
            float3 detail = clamp(tex2D(_DetailTex, IN.uv_DetailTex).rgb * 2, .60, 1.45);
            float3 featherDetail = (1 + grain) * edgeShade
                * lerp(float3(1,1,1), detail, _DetailStrength);
            o.Albedo = saturate(tone * IN.color.rgb
                * lerp(float3(1,1,1), featherDetail, _Feather) + rachis*.010*_Feather);
            o.Metallic = 0;
            o.Smoothness = lerp(_Smoothness, min(_Smoothness, .22), _Feather);
            o.Occlusion = 1;
            o.Alpha = 1;
        }
        ENDCG
    }
    Fallback "Diffuse"
}
