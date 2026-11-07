// The other PC's screen, from the decoder's NV12 to this PC's window, on the GPU. Two passes:
// the picture in BGRA at its own size, with exactly ColorConvert.Nv12ToBgra's integer math, then
// that picture into the window: scaled, turned and filtered the way WPF draws a bitmap.
// Compiled with fxc (vs_4_0 and ps_4_0, /O3) into SwapChainPresenter.Shaders.cs, so the app
// needs no shader compiler. The entry points:
//   fxc /T vs_4_0 /E Fill   /O3    fxc /T ps_4_0 /E ToBgra /O3
//   fxc /T vs_4_0 /E Quad   /O3    fxc /T ps_4_0 /E Show   /O3

// One triangle that covers the target.
float4 Fill(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// ---- Pass 1: NV12 to BGRA, every pixel as ColorConvert makes it ----
Texture2D<uint> Luma : register(t0);    // R8_UINT, the picture's size
Texture2D<uint2> Chroma : register(t1); // R8G8_UINT, half each way: U and V

// BT.709 at video range, times 65536, rounded half up, like the CPU: color from the 2 by 2
// block's own sample, no blending between blocks.
float4 ToBgra(float4 p : SV_Position) : SV_Target
{
    int2 at = int2(p.xy);
    int l = 76309 * ((int)Luma.Load(int3(at, 0)) - 16);
    int2 c = (int2)Chroma.Load(int3(at >> 1, 0)) - 128;
    int3 rgb = int3(l + 117489 * c.y, l - 13975 * c.x - 34925 * c.y, l + 138438 * c.x) + 32768;
    return float4(clamp(rgb >> 16, 0, 255) / 255.0, 1);
}

// ---- Pass 2: the picture into the window ----
Texture2D<float4> Picture : register(t0);
SamplerState Filter : register(s0);
// Where the picture's corners go, in clip space: its top left, and the steps to its top right
// and to its bottom left. Any turn and scale WPF gives the picture is such a parallelogram.
cbuffer Place : register(b0) { float2 Origin; float2 AlongU; float2 AlongV; float2 Pad; };

struct Corner
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

// A strip of two triangles: 0 top left, 1 top right, 2 bottom left, 3 bottom right.
Corner Quad(uint id : SV_VertexID)
{
    Corner c;
    c.uv = float2(id & 1, id >> 1);
    c.pos = float4(Origin + c.uv.x * AlongU + c.uv.y * AlongV, 0, 1);
    return c;
}

float4 Show(Corner c) : SV_Target
{
    return float4(Picture.Sample(Filter, c.uv).rgb, 1);
}
