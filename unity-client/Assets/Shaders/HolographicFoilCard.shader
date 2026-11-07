Shader "Custom/HolographicFoilCard"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)

        _Tilt ("Card Tilt XY", Vector) = (0, 0, 0, 0)
        _FoilIntensity ("Foil Intensity", Range(0, 1.5)) = 0.85
        _SheenWidth ("Specular Sheen Width", Range(0.02, 0.5)) = 0.16
        _FoilMode ("Foil Mode (0=Iridescent, 1=LegendaryGold)", Float) = 0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;

            float4 _Tilt;
            float _FoilIntensity;
            float _SheenWidth;
            float _FoilMode;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            float3 EvaluateIridescentPalette(float phase)
            {
                float3 rainbow = 0.56 + 0.44 * cos(6.2831853 * (phase + float3(0.00, 0.33, 0.67)));
                float3 opalCyan = float3(0.36, 0.94, 1.00);
                float mixAmt = 0.20 * (0.5 + 0.5 * sin(phase * 6.2831853 + 1.1));
                return lerp(rainbow, opalCyan, mixAmt);
            }

            float3 EvaluateLegendaryPalette(float phase)
            {
                float p = frac(phase);
                float3 cGold    = float3(1.00, 0.82, 0.22);
                float3 cCrimson = float3(0.96, 0.20, 0.30);
                float3 cCyan    = float3(0.22, 0.94, 0.98);
                float3 cAmber   = float3(1.00, 0.60, 0.12);

                float3 g0 = lerp(cGold, cCrimson, smoothstep(0.00, 0.34, p));
                float3 g1 = lerp(cCrimson, cCyan, smoothstep(0.34, 0.66, p));
                float3 g2 = lerp(cCyan, cAmber, smoothstep(0.66, 0.84, p));
                float3 g3 = lerp(cAmber, cGold, smoothstep(0.84, 1.00, p));

                if (p < 0.34) return g0;
                if (p < 0.66) return g1;
                if (p < 0.84) return g2;
                return g3;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                half4 rawTex = tex2D(_MainTex, uv) + _TextureSampleAdd;

                // Soft inner UV border mask so outer card rim and transparent rounded corners stay razor clean
                float2 edgeDist = min(uv, 1.0 - uv);
                float innerBorderMask = smoothstep(0.025, 0.072, edgeDist.x) * smoothstep(0.020, 0.058, edgeDist.y);

                // Luminance & saturation analysis so #EAEAEA / #F2F2F2 inner frame (~0.82..0.97) receives full foil
                // while dark Clubs/Spades and saturated Hearts/Diamonds pips remain crisp and legible.
                float lum = dot(rawTex.rgb, float3(0.299, 0.587, 0.114));
                float maxCh = max(rawTex.r, max(rawTex.g, rawTex.b));
                float minCh = min(rawTex.r, min(rawTex.g, rawTex.b));
                float chroma = maxCh - minCh;

                float frameLumBand = smoothstep(0.77, 0.83, lum) * (1.0 - smoothstep(0.968, 0.996, lum) * 0.82);
                float lowChromaMask = 1.0 - smoothstep(0.16, 0.38, chroma);
                float innerFrameMask = frameLumBand * lowChromaMask * innerBorderMask;

                // Allow custom artwork (weapons/potions) to catch a crisp specular glint without washing out dark ink
                float artGlintMask = smoothstep(0.24, 0.58, lum) * (1.0 - smoothstep(0.30, 0.65, chroma) * 0.65) * innerBorderMask;

                float2 tilt = clamp(_Tilt.xy, -1.5, 1.5);
                float t = _Time.y;

                float2 centeredUV = uv - 0.5;
                float diag = dot(centeredUV, float2(0.78, 0.62));
                float crossDiag = dot(centeredUV, float2(-0.62, 0.78));
                float tiltShift = dot(tilt, float2(0.85, 0.65));

                // Smooth vector-friendly harmonic ripple
                float ripple = sin(crossDiag * 8.5 + t * 1.35 + tilt.x * 2.4) * 0.032;

                float foilPhase = diag * 2.25 + ripple * 2.2 - tiltShift * 1.18 + t * 0.22;
                float3 foilGrad = (_FoilMode >= 0.5)
                    ? EvaluateLegendaryPalette(foilPhase * 0.85)
                    : EvaluateIridescentPalette(foilPhase);

                // Specular diagonal sheen band driven by pointer _Tilt.xy + subtle idle shimmer
                float idleSweep = sin(t * 1.15) * 0.20;
                float sheenCenter = clamp(tiltShift * 0.58 + idleSweep, -0.78, 0.78);
                float width = max(0.02, _SheenWidth);

                float distPrimary = (diag + ripple) - sheenCenter;
                float primarySheen = exp(-(distPrimary * distPrimary) / (width * width) * 1.85);

                float thinWidth = max(0.012, width * 0.38);
                float distSecondary = (diag - ripple * 0.5) - (sheenCenter + 0.13);
                float secondarySheen = exp(-(distSecondary * distSecondary) / (thinWidth * thinWidth) * 2.4) * 0.52;

                float totalSheen = saturate(primarySheen + secondarySheen);

                float intensity = max(0.0, _FoilIntensity);
                float foilBlend = saturate(intensity) * innerFrameMask;

                // Blend prismatic/molten foil onto the inner frame while keeping crisp contrast
                float3 baseRGB = rawTex.rgb;
                float3 foilTinted = lerp(baseRGB, baseRGB * lerp(float3(1.0, 1.0, 1.0), foilGrad * 1.10, 0.62), foilBlend);

                float3 sheenTint = lerp(foilGrad, float3(1.0, 0.99, 0.94), 0.50);
                float sheenMask = innerFrameMask * 0.78 + artGlintMask * 0.24;
                float3 finalRGB = foilTinted + sheenTint * (totalSheen * intensity * sheenMask);

                half4 outColor;
                outColor.rgb = saturate(finalRGB) * IN.color.rgb;
                outColor.a = rawTex.a * IN.color.a;

                #ifdef UNITY_UI_CLIP_RECT
                outColor.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(outColor.a - 0.001);
                #endif

                return outColor;
            }
            ENDCG
        }
    }
}
