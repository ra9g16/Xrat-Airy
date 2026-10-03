// Screen-space point sprites for AiryPointCloudRenderer. Six vertices (two triangles) per point, generated
// from SV_VertexID, so no mesh is needed. The pass has no LightMode tag, which URP draws as SRPDefaultUnlit
// and the Built-in pipeline draws as a normal unlit pass.
// Lives in a Resources folder so Shader.Find also works in player builds.
Shader "Xrat/Airy/PointCloud"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _Points; // xyz = Unity local position, w = intensity + channel * 256
            float4x4 _LocalToWorld;
            float _PointSize;
            float _ColorMode;
            float4 _SolidColor;
            float4 _Ranges;         // x,y = height range; z,w = distance range
            float4 _IntensityRange; // x,y
            float _RoundPoints;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 corner : TEXCOORD0;
            };

            // Google's polynomial approximation of the Turbo colour map (sRGB).
            float3 Turbo(float x)
            {
                const float4 kRed4 = float4(0.13572138, 4.61539260, -42.66032258, 132.13108234);
                const float4 kGreen4 = float4(0.09140261, 2.19418839, 4.84296658, -14.18503333);
                const float4 kBlue4 = float4(0.10667330, 12.64194608, -60.58204836, 110.36276771);
                const float2 kRed2 = float2(-152.94239396, 59.28637943);
                const float2 kGreen2 = float2(4.27729857, 2.82956604);
                const float2 kBlue2 = float2(-89.90310912, 27.34824973);
                x = saturate(x);
                float4 v4 = float4(1.0, x, x * x, x * x * x);
                float2 v2 = v4.zw * v4.z;
                return saturate(float3(
                    dot(v4, kRed4) + dot(v2, kRed2),
                    dot(v4, kGreen4) + dot(v2, kGreen2),
                    dot(v4, kBlue4) + dot(v2, kBlue2)));
            }

            float Ramp(float value, float2 range)
            {
                return (value - range.x) / max(1e-5, range.y - range.x);
            }

            v2f vert(uint vertexId : SV_VertexID)
            {
                uint index = vertexId / 6;
                uint corner = vertexId - index * 6;
                // Triangles (0,1,2) and (3,4,5) -> quad corners (-1,-1) (1,-1) (1,1) / (-1,-1) (1,1) (-1,1).
                float2 c = float2(
                    (corner == 1 || corner == 2 || corner == 4) ? 1.0 : -1.0,
                    (corner == 2 || corner == 4 || corner == 5) ? 1.0 : -1.0);

                float4 data = _Points[index];
                float3 world = mul(_LocalToWorld, float4(data.xyz, 1.0)).xyz;

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                o.pos.xy += c * _PointSize * o.pos.w / _ScreenParams.xy;
                o.corner = c;

                float channel = floor(data.w / 256.0);
                float intensity = data.w - channel * 256.0;

                int mode = (int)round(_ColorMode);
                float3 rgb;
                if (mode == 0)
                    rgb = Turbo(Ramp(intensity, _IntensityRange.xy));
                else if (mode == 1)
                    rgb = Turbo(Ramp(world.y, _Ranges.xy));
                else if (mode == 2)
                    rgb = Turbo(Ramp(length(data.xyz), _Ranges.zw));
                else if (mode == 3)
                    rgb = Turbo(channel / 95.0);
                else
                    rgb = _SolidColor.rgb; // already linear when set through MaterialPropertyBlock.SetColor

                #if !defined(UNITY_COLORSPACE_GAMMA)
                if (mode != 4)
                    rgb = GammaToLinearSpace(rgb);
                #endif

                o.color = float4(rgb, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_RoundPoints > 0.5 && dot(i.corner, i.corner) > 1.0)
                    discard;
                return i.color;
            }
            ENDCG
        }
    }
}
