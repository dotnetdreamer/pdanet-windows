using Vortice.Direct3D11;

namespace PdaNetPortable.Share.Media;

/// <summary>An H.264 encoder for the screen, see H264Encoder and D3D12H264Encoder.</summary>
internal interface IVideoEncoder : IDisposable
{
    string Name { get; }
    bool Hardware { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Each encoded frame: an H.264 access unit with start codes, its duration in 100 ns units and
    /// whether it is a keyframe. Valid during the call only.
    /// </summary>
    Action<ReadOnlyMemory<byte>, long, bool>? Output { get; set; }

    void RequestKeyframe();

    /// <summary>Follows the bitrate the link can take, small changes are skipped.</summary>
    void SetBitrate(int bps);

    /// <summary>Encodes a frame in memory, times in 100 ns units.</summary>
    void Encode(Nv12Frame frame, long time, long duration);

    /// <summary>Encodes an NV12 texture on the GPU device the encoder was made for, times in 100 ns units.</summary>
    void Encode(ID3D11Texture2D texture, long time, long duration);
}
