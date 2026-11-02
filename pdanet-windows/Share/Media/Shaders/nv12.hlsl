// The screen in BGRA to NV12 at BT.709 video range, the same math as ColorConvert.
Texture2D<float4> Screen : register(t0);
SamplerState Linear : register(s0);
cbuffer Params : register(b0) { float2 Texel; float Scale; float Pad; };

static const float3 YC = float3(11966, 40254, 4064) / 65536.0;
static const float3 UC = float3(-6596, -22188, 28784) / 65536.0;
static const float3 VC = float3(28784, -26145, -2639) / 65536.0;

// One triangle that covers the target.
float4 Fill(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// A pixel of the frame is a pixel of the screen, or at scale 2 the average of 2 by 2.
float Luma(float4 p : SV_Position) : SV_Target
{
    float2 at = (floor(p.xy) * Scale + Scale * 0.5) * Texel;
    return 16.0 / 255.0 + dot(Screen.SampleLevel(Linear, at, 0).rgb, YC);
}

// Color for 2 by 2 pixels of the frame: one filtered sample averages 2 by 2 of the screen.
float2 Chroma(float4 p : SV_Position) : SV_Target
{
    float2 base = floor(p.xy) * 2 * Scale;
    float3 c = Screen.SampleLevel(Linear, (base + 1) * Texel, 0).rgb;
    if (Scale > 1)
    {
        c += Screen.SampleLevel(Linear, (base + float2(3, 1)) * Texel, 0).rgb;
        c += Screen.SampleLevel(Linear, (base + float2(1, 3)) * Texel, 0).rgb;
        c += Screen.SampleLevel(Linear, (base + float2(3, 3)) * Texel, 0).rgb;
        c *= 0.25;
    }
    return 128.0 / 255.0 + float2(dot(c, UC), dot(c, VC));
}
