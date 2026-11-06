using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace PdaNetPortable.Share.Media;

/// <summary>
/// Media Foundation, the codecs that come with Windows: the GPU's H.264 encoder (Intel Quick
/// Sync, NVIDIA NVENC, AMD AMF) when there is one, and Microsoft's own H.264 encoder and decoder
/// otherwise. Started on first use and shut down when no session needs it.
/// </summary>
internal static class Mf
{
    private static readonly object Gate = new();
    private static int _users;

    public static readonly Guid NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    // MF results that are part of the normal flow.
    public const int E_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int E_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    // IMFTransform output stream flags.
    public const int ProvidesSamples = 0x100, CanProvideSamples = 0x200;

    public const int H264ProfileBase = 66, H264ProfileMain = 77, H264ProfileHigh = 100;
    public const uint InterlaceProgressive = 2;

    public static void Start()
    {
        lock (Gate)
        {
            if (_users++ == 0) MediaFactory.MFStartup(true).CheckError();
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_users > 0 && --_users == 0) MediaFactory.MFShutdown();
        }
    }

    public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public static (int High, int Low) Unpack(ulong v) => ((int)(v >> 32), (int)(uint)v);

    public static IMFMediaType VideoType(Guid subtype, int width, int height, int fps)
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        t.Set(MediaTypeAttributeKeys.Subtype, subtype);
        if (width > 0) t.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
        if (fps > 0) t.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
        t.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        t.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
        return t;
    }

    /// <summary>Lists the transforms that turn <paramref name="input"/> into <paramref name="output"/>, best first.</summary>
    public static List<IMFActivate> Find(Guid category, EnumFlag flags, Guid input, Guid output)
    {
        var list = new List<IMFActivate>();
        try
        {
            var found = MediaFactory.MFTEnumEx(category, (uint)(flags | EnumFlag.EnumFlagSortandfilter),
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = input },
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = output });
            foreach (var a in found) list.Add(a);
        }
        catch (SharpGenException) { /* none of that kind on this PC */ }
        return list;
    }

    public static string Name(IMFActivate a)
    {
        try { return a.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? "H.264"; }
        catch (SharpGenException) { return "H.264"; }
    }
}

/// <summary>
/// ICodecAPI, how an encoder takes its settings: bitrate, rate control, low latency and keyframes
/// on demand. Vortice does not wrap it, so SetValue is called through the COM vtable with a
/// VARIANT laid out by hand. Every setting is best effort, encoders support different sets.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IID = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    // codecapi.h
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid MinQP = new("0ee22c6a-a37c-4568-b5f1-9d4c2b3ab886");
    public static readonly Guid MaxQP = new("3daf6f66-a6a7-45e0-a8e5-f2743f46a3a2");
    /// <summary>The quantizer of the next frames in constant QP mode, four 16 bit fields: default, I, P and B frames.</summary>
    public static readonly Guid EncodeQP = new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca");

    public const uint RateCbr = 0, RateQuality = 3, RateLowDelayVbr = 4;

    private const ushort VT_BOOL = 11, VT_UI4 = 19, VT_UI8 = 21;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public ulong ULong;
        [FieldOffset(8)] public short Bool;
    }

    private IntPtr _ptr;

    private CodecApi(IntPtr ptr) => _ptr = ptr;

    public static CodecApi? Of(ComObject transform)
    {
        var hr = transform.QueryInterface(IID, out var ptr);
        return hr.Success && ptr != IntPtr.Zero ? new CodecApi(ptr) : null;
    }

    public bool Set(Guid api, uint value)
    {
        var v = new Variant { Type = VT_UI4, UInt = value };
        return Call(api, ref v);
    }

    public bool Set(Guid api, bool value)
    {
        var v = new Variant { Type = VT_BOOL, Bool = (short)(value ? -1 : 0) };
        return Call(api, ref v);
    }

    public bool Set(Guid api, ulong value)
    {
        var v = new Variant { Type = VT_UI8, ULong = value };
        return Call(api, ref v);
    }

    /// <summary>The encoder knows the setting: ICodecAPI::IsSupported, the 4th entry, says S_OK.</summary>
    public bool IsSupported(Guid api)
    {
        if (_ptr == IntPtr.Zero) return false;
        var vtbl = *(IntPtr**)_ptr;
        var isSupported = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)vtbl[3];
        return isSupported(_ptr, &api) == 0;
    }

    private bool Call(Guid api, ref Variant value)
    {
        if (_ptr == IntPtr.Zero) return false;
        // ICodecAPI::SetValue is the 10th entry: IUnknown's three, then IsSupported, IsModifiable,
        // GetParameterRange, GetParameterValues, GetDefaultValue, GetValue.
        var vtbl = *(IntPtr**)_ptr;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
        fixed (Variant* pv = &value)
            return setValue(_ptr, &api, pv) >= 0;
    }

    public void Dispose()
    {
        if (_ptr == IntPtr.Zero) return;
        Marshal.Release(_ptr);
        _ptr = IntPtr.Zero;
    }
}
