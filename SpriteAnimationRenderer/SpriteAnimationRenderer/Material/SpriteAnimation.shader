 Shader "Instanced/SpriteAnimation"
 {
    Properties
    {
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
    }
    
    SubShader
    {
        Tags
        {
            "Queue"="Transparent-1"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
        }

        Cull Off
        Lighting Off
        ZWrite On
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            // Upgrade NOTE: excluded shader from OpenGL ES 2.0 because it uses non-square matrices
            #pragma exclude_renderers gles
            #pragma require integers
            #pragma require instancing
            #pragma require fragcoord
            #pragma require compute
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            #define EPSILON         1.0e-4
            #define MIN_ALPHA       1.0 / 255.0

            sampler2D _MainTex;

            StructuredBuffer<float4x2> transformBuffer;
            StructuredBuffer<float4> uvBuffer;
            StructuredBuffer<int> indexBuffer;
			StructuredBuffer<uint3> colorBuffer;
            StructuredBuffer<float4x3> paintBuffer;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv: TEXCOORD0;
				half4 color : COLOR0;
                uint2 paint : COLOR1;
            };

            float4 Rotate(float4 vertex, float4 quaternion)
            {
                vertex.xyz += 2.0 * cross(quaternion.xyz, cross(quaternion.xyz, vertex.xyz) + quaternion.w * vertex.xyz);
                return vertex;
            }

            half4 Decode(uint u)
            {
                uint x = u & 0xff;
                uint y = (u >> 8) & 0xff;
                uint z = (u >> 16) & 0xff;
                uint w = (u >> 24) & 0xff;
                return half4(x, y, z, w) / 0xff;
            }

            half3 RgbToHsv(half3 c)
            {
                half4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                half4 p = lerp(half4(c.bg, K.wz), half4(c.gb, K.xy), step(c.b, c.g));
                half4 q = lerp(half4(p.xyw, c.r), half4(c.r, p.yzx), step(p.x, c.r));
                half d = q.x - min(q.w, q.y);
                half e = EPSILON;
                return half3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            half3 HsvToRgb(half3 c)
            {
                half4 K = half4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                half3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            half4 ApplyPaintToColor(half4 color, half4x3 paint)
            {
                half3 findColor = paint._11_21_31;
                half3 swapColor = paint._12_22_32;
                half threshold = paint._13;
                half smooth = paint._23;
                half blend = paint._33;
                //rgb
                half delta = length(color.rgb - findColor);
                delta = smoothstep(threshold - smooth, threshold + smooth, delta);
                half3 baseHSV = RgbToHsv(color.rgb);
                half3 swapHSV = RgbToHsv(swapColor);
                swapColor = HsvToRgb(half3(swapHSV.r, baseHSV.g, baseHSV.b));
                swapColor = lerp(swapColor, color.rgb, delta);
                color.rgb = lerp(color.rgb, swapColor, blend);
                //alpha
                delta = abs(color.a - paint._41);
                delta = smoothstep(threshold - smooth, threshold + smooth, delta);
                half alpha = lerp(paint._42, color.a, delta);
                color.a = lerp(color.a, alpha, blend);
                return color;
            }

            v2f vert(appdata_base v, uint instanceID : SV_InstanceID)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float4x2 transform = transformBuffer[instanceID];
                float4 uv = uvBuffer[indexBuffer[instanceID]];
                uint3 color = colorBuffer[instanceID];
                //anchor origin to the centre of the image
                v.vertex = v.vertex - float4(0.5, 0.5, 0, 0);
                //rotate
                v.vertex = Rotate(v.vertex, transform._12_22_32_42);
                //position
                float4 position = float4(transform._11_21_31, 1.0);
                //scale
                position.xy += v.vertex.xy * transform._41;
                
                v2f o;
                o.pos = UnityObjectToClipPos(position);
                o.uv = v.texcoord * uv.xy + uv.zw;
                o.color = Decode(color.x);
                o.paint = color.yz;
    
                #ifdef PIXELSNAP_ON
                o.pos = UnityPixelSnap(o.pos);
                #endif
    
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 color = tex2D(_MainTex, i.uv) * i.color;
                half4x3 paint = paintBuffer[i.paint.x];
                color = ApplyPaintToColor(color, paint);
                half cutoff = max(MIN_ALPHA, paint._43);
                paint = paintBuffer[i.paint.y];
                color = ApplyPaintToColor(color, paint);
                cutoff = max(cutoff, paint._43);
                clip(color.a - cutoff);
                color.rgb *= color.a;
                return color;
            }

            ENDCG
        }
    }
}