using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using PdaNetPortable.Services;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Video;
using Vortice.DXGI;
using FeatureVideo = Vortice.Direct3D12.Video.FeatureVideo;

namespace PdaNetPortable.Share.Media;

/// <summary>
/// H.264 on the GPU through Direct3D 12 video encoding, for GPUs that share the PC's memory, as
/// in most laptops. Media Foundation's GPU encoders keep pools of 20 to 30 frames of their own,
/// and on such a GPU that is the app's memory: some 90 MB at 1080p. Here the app makes every
/// frame the encoder touches, the one it reads, the one it refers to and the one it builds, and
/// the whole encoder costs about 35 MB. The capture's GpuConverter writes each frame straight
/// into a texture both GPU devices share, and a shared fence orders the two.
///
/// Set up for remote desktop like H264Encoder: one reference frame, no B frames, keyframes on
/// request, a variable bitrate that follows the link. The driver writes the slices, this class
/// the sequence and picture headers that go with them.
/// </summary>
internal sealed unsafe class D3D12H264Encoder : IVideoEncoder
{
    public string Name { get; }
    public bool Hardware => true;
    public int Width { get; }
    public int Height { get; }
    public Action<ReadOnlyMemory<byte>, long, bool>? Output { get; set; }

    /// <summary>The texture each frame is converted into, on the capture's Direct3D 11 device.</summary>
    public ID3D11Texture2D Input => _input11;

    /// <summary>Set when encoding failed on this PC, later sessions use Media Foundation.</summary>
    public static bool Broken { get; private set; }

    /// <summary>This PC cannot use it, as found when it would not start.</summary>
    public static void MarkBroken() => Broken = true;

    private const uint AllSubresources = 0xFFFFFFFF;
    private const uint StateCommon = 0, StateEncodeRead = 0x200000, StateEncodeWrite = 0x800000;
    private const byte Log2MaxFrameNumMinus4 = 4; // frame_num counts to 256 and wraps
    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    private readonly ID3D12Device _device;
    private readonly ID3D12VideoDevice3 _video;
    private ID3D12VideoEncoder _encoder;
    private readonly ID3D12VideoEncoderHeap _heap;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12VideoEncodeCommandList2 _list;
    private readonly ID3D12Fence _fence;
    private readonly ID3D11Fence _fence11;
    private readonly ID3D11DeviceContext _context11;
    private readonly ID3D11DeviceContext4 _context4;
    private readonly ID3D11Texture2D _input11;
    private readonly ID3D12Resource _input;
    // The frame the encoder builds and the one it refers to swap places after each frame: two
    // textures, or one array of two where the driver wants that.
    private readonly ID3D12Resource[] _recon;
    private readonly bool _reconArray;
    private readonly ID3D12Resource _bitstream, _metadata, _resolved;
    private readonly byte* _bits;
    private readonly OutputMetadata* _result;
    private readonly ulong _bitstreamSize;
    private readonly AutoResetEvent _done = new(false);
    private ulong _fenceValue;

    private readonly int _codedWidth, _codedHeight;
    private VideoEncoderProfileH264 _profile;
    private H264Config _config;
    private H264Gop _gop;
    private Vbr _rate;
    private VideoEncoderRateControlFlags _rateFlags;
    private readonly uint _fps;
    private readonly bool _canChangeRate; // in place; else a new encoder object takes the new rate
    private bool _rateChanged;
    private int _bitrate;
    private readonly byte[] _headers; // SPS and PPS, before each keyframe
    private readonly ArrayBufferWriter<byte> _au = new(256 << 10);

    private bool _forceKey = true;
    private int _target, _reference = -1;
    private uint _frameNum, _poc, _idrId, _sinceKey;
    private uint _refFrameNum, _refPoc;
    private bool _disposed;

    /// <param name="device11">The capture's device, whose GpuConverter fills <see cref="Input"/>.</param>
    /// <param name="context11">Its immediate context.</param>
    public D3D12H264Encoder(ID3D11Device device11, ID3D11DeviceContext context11, int width, int height, int fps, int bitrate)
    {
        Width = width;
        Height = height;
        _fps = (uint)fps;
        _bitrate = bitrate;
        _context11 = context11;
        try
        {
            using (var dxgi = device11.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgi.GetAdapter())
            {
                Name = $"Direct3D 12 video encoding on {adapter.Description.Description.Trim()}";
                _device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);
            }
            // On a GPU with memory of its own, Media Foundation's encoder keeps its frames there.
            if (!_device.Architecture1.Uma) throw new NotSupportedException("the GPU has memory of its own");
            _video = _device.QueryInterfaceOrNull<ID3D12VideoDevice3>()
                     ?? throw new NotSupportedException("this Windows has no Direct3D 12 video encoding");

            var codec = new CodecSupport { Codec = VideoEncoderCodec.H264 };
            if (Check(FeatureVideo.EncoderCodec, ref codec) < 0 || codec.IsSupported == 0)
                throw new NotSupportedException("the GPU cannot encode H.264 through Direct3D 12");

            // High profile packs text best, Main is there for GPUs without it.
            var maxLevel = VideoEncoderLevelsH264.Level1;
            bool found = false;
            foreach (var p in new[] { VideoEncoderProfileH264.High, VideoEncoderProfileH264.Main })
            {
                var profile = p;
                VideoEncoderLevelsH264 min = 0, max = 0;
                var q = new ProfileLevelSupport { Codec = VideoEncoderCodec.H264, Profile = Of(&profile), MinLevel = Of(&min), MaxLevel = Of(&max) };
                if (Check(FeatureVideo.EncoderProfileLevel, ref q) < 0 || q.IsSupported == 0) continue;
                (_profile, maxLevel, found) = (p, max, true);
                break;
            }
            if (!found) throw new NotSupportedException("the GPU's encoder has neither High nor Main profile");
            var prof = _profile;

            var format = new InputFormatSupport { Codec = VideoEncoderCodec.H264, Profile = Of(&prof), Format = Format.NV12 };
            if (Check(FeatureVideo.EncoderInputFormat, ref format) < 0 || format.IsSupported == 0)
                throw new NotSupportedException("the GPU's encoder does not take NV12");
            // Variable bitrate: constant bitrate pads frames up to the bitrate, a still screen
            // would cost as much as a film.
            var rate = new RateControlSupport { Codec = VideoEncoderCodec.H264, Mode = VideoEncoderRateControlMode.Vbr };
            if (Check(FeatureVideo.EncoderRateControlMode, ref rate) < 0 || rate.IsSupported == 0)
                throw new NotSupportedException("the GPU's encoder has no variable bitrate");

            var limits = new H264ConfigSupport();
            var config = new ConfigSupport { Codec = VideoEncoderCodec.H264, Profile = Of(&prof), Limits = Of(&limits) };
            if (Check(FeatureVideo.EncoderCodecConfigurationSupport, ref config) < 0 || config.IsSupported == 0)
                throw new NotSupportedException("the GPU's encoder does not say how it encodes H.264");
            _config.DirectMode = VideoEncoderCodecConfigurationH264DirectModes.Disabled;
            _config.Deblocking = VideoEncoderCodecConfigurationH264SlicesDeblockingModes.AllLumaChromaSliceBlockEdgesAlwaysFiltered;
            if ((limits.Flags & VideoEncoderCodecConfigurationSupportH264Flags.CabacEncodingSupport) != 0)
                _config.Flags |= VideoEncoderCodecConfigurationH264Flags.EnableCabacEncoding;
            if (_profile == VideoEncoderProfileH264.High && (limits.Flags & VideoEncoderCodecConfigurationSupportH264Flags.Adaptive8x8TransformEncodingSupport) != 0)
                _config.Flags |= VideoEncoderCodecConfigurationH264Flags.UseAdaptive8x8Transform;

            // The size the driver encodes, which some drivers want in whole macroblocks: the frame
            // goes in the top left corner and the headers crop the rest away.
            var count = new RatiosCount { Codec = VideoEncoderCodec.H264 };
            Check(FeatureVideo.EncoderOutputResolutionRatiosCount, ref count);
            var ratios = new ulong[Math.Max(1, count.Count)];
            fixed (ulong* r = ratios)
            {
                var sizes = new OutputResolution { Codec = VideoEncoderCodec.H264, RatiosCount = count.Count, Ratios = count.Count > 0 ? r : null };
                // With no ratios to report, the list has to be null.
                if (Check(FeatureVideo.EncoderOutputResolution, ref sizes) < 0 || sizes.IsSupported == 0)
                    throw new NotSupportedException("the GPU's encoder does not say what sizes it takes");
                _codedWidth = Align(width, (int)Math.Max(1, sizes.WidthMultiple));
                _codedHeight = Align(height, (int)Math.Max(1, sizes.HeightMultiple));
                if (_codedWidth > sizes.Max.Width || _codedHeight > sizes.Max.Height || width < sizes.Min.Width || height < sizes.Min.Height)
                    throw new NotSupportedException($"the GPU's encoder does not take {width}x{height}");
            }
            var level = LevelFor(width, height, fps, _profile == VideoEncoderProfileH264.High);
            if (level.Level > maxLevel) throw new NotSupportedException($"the GPU's encoder does not reach level {level.Idc / 10.0:0.0}");
            var lvl = level.Level;

            // Everything together. A GOP of a minute as Media Foundation has; picture order from the
            // frame number where the driver allows, it saves a field in every slice.
            var res = new Resolution { Width = (uint)_codedWidth, Height = (uint)_codedHeight };
            _rate = new Vbr { TargetBitRate = (ulong)bitrate, PeakBitRate = (ulong)bitrate * 5 / 4 };
            EncoderSupport support = default;
            foreach (byte pocType in new byte[] { 2, 0 })
            {
                _gop = new H264Gop { GopLength = _fps * 60, PPicturePeriod = 1, PocType = pocType, Log2MaxFrameNumMinus4 = Log2MaxFrameNumMinus4, Log2MaxPocLsbMinus4 = 4 };
                support = Support(&res);
                if ((support.Validation & VideoEncoderValidationFlags.GopStructureNotSupported) == 0) break;
            }
            if ((support.Support & VideoEncoderSupportFlags.GeneralSupportOk) == 0)
                throw new NotSupportedException($"the GPU's encoder does not take this setup ({support.Validation})");
            // A cap on the size of a frame lets the buffer for it be small: a third of the
            // largest possible frame, and still several times a sharp keyframe at full bitrate.
            int frameCap = Math.Max(512 << 10, _codedWidth * _codedHeight / 2);
            if ((support.Support & VideoEncoderSupportFlags.RateControlMaxFrameSizeAvailable) != 0)
            {
                _rateFlags = VideoEncoderRateControlFlags.EnableMaxFrameSize;
                _rate.MaxFrameBitSize = (ulong)frameCap * 8 * 9 / 10;
                var capped = Support(&res);
                if ((capped.Support & VideoEncoderSupportFlags.GeneralSupportOk) != 0) support = capped;
                else (_rateFlags, _rate.MaxFrameBitSize) = (0, 0);
            }
            _canChangeRate = (support.Support & VideoEncoderSupportFlags.RateControlReconfigurationAvailable) != 0;
            _reconArray = (support.Support & VideoEncoderSupportFlags.ReconstructedFramesRequireTextureArrays) != 0;

            var needs = new ResourceRequirements { Codec = VideoEncoderCodec.H264, Profile = Of(&prof), InputFormat = Format.NV12, Resolution = res };
            if (Check(FeatureVideo.EncoderResourceRequirements, ref needs) < 0 || needs.IsSupported == 0)
                throw new NotSupportedException("the GPU's encoder does not say what memory it needs");

            _encoder = NewEncoder();
            var heapDesc = new HeapDesc { Codec = VideoEncoderCodec.H264, Profile = Of(&prof), Level = Of(&lvl), ResolutionsCount = 1, Resolutions = &res };
            var iid = typeof(ID3D12VideoEncoderHeap).GUID;
            void* made;
            Call(_video, 15, &heapDesc, &iid, &made, "CreateVideoEncoderHeap");
            _heap = new ID3D12VideoEncoderHeap((nint)made);

            _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.VideoEncode));
            _allocator = _device.CreateCommandAllocator(CommandListType.VideoEncode);
            _list = _device.CreateCommandList<ID3D12VideoEncodeCommandList2>(0, CommandListType.VideoEncode, _allocator, null!);
            _list.Close();

            // The texture the capture's converter writes and the encoder reads, made on the
            // capture's device and opened on this one, and the fence both devices signal.
            _input11 = device11.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_codedWidth, Height = (uint)_codedHeight, MipLevels = 1, ArraySize = 1,
                Format = Format.NV12, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget, MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
            });
            using (var shared = _input11.QueryInterface<IDXGIResource1>())
            {
                var handle = shared.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null!);
                try { _input = _device.OpenSharedHandle<ID3D12Resource>(handle); }
                finally { CloseHandle(handle); }
            }
            _fence = _device.CreateFence(0, Vortice.Direct3D12.FenceFlags.Shared);
            var fenceHandle = _device.CreateSharedHandle(_fence, null, null!);
            try
            {
                using var device5 = device11.QueryInterface<ID3D11Device5>();
                _fence11 = device5.OpenSharedFence(fenceHandle);
            }
            finally { CloseHandle(fenceHandle); }
            _context4 = context11.QueryInterface<ID3D11DeviceContext4>();

            // Frames the driver may lay out its own way unless it says they can be read.
            var reconFlags = (support.Support & VideoEncoderSupportFlags.ReadableReconstructedPictureLayoutAvailable) != 0
                ? ResourceFlags.None
                : ResourceFlags.VideoEncodeReferenceOnly | ResourceFlags.DenyShaderResource;
            _recon = _reconArray
                ? new[] { Texture(2, reconFlags) }
                : new[] { Texture(1, reconFlags), Texture(1, reconFlags) };

            // The encoded frame and what the driver says about it land where the processor reads
            // them. The driver takes all of the buffer into memory, hence the cap on frame size.
            var readable = _device.GetCustomHeapProperties(0, HeapType.Readback);
            var cpu = new HeapProperties(readable.CPUPageProperty, readable.MemoryPoolPreference);
            _bitstreamSize = (ulong)Align(_rateFlags != 0 ? frameCap : _codedWidth * _codedHeight * 3 / 2, (int)Math.Max(1, needs.BitstreamAlignment));
            _bitstream = _device.CreateCommittedResource(cpu, HeapFlags.None, ResourceDescription.Buffer(_bitstreamSize), ResourceStates.Common, null);
            _metadata = _device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer((ulong)Align((int)needs.MaxMetadataSize, (int)Math.Max(1, needs.MetadataAlignment))), ResourceStates.Common, null);
            _resolved = _device.CreateCommittedResource(cpu, HeapFlags.None, ResourceDescription.Buffer(4096), ResourceStates.Common, null);
            _bits = _bitstream.Map<byte>(0);
            _result = (OutputMetadata*)_resolved.Map<byte>(0);

            _headers = Headers(level.Idc);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private ID3D12VideoEncoder NewEncoder()
    {
        var prof = _profile;
        var cfg = _config;
        var desc = new EncoderDesc
        {
            Codec = VideoEncoderCodec.H264, Profile = Of(&prof), InputFormat = Format.NV12,
            Configuration = Of(&cfg), MaxMotionPrecision = VideoEncoderMotionEstimationPrecisionMode.Maximum,
        };
        var iid = typeof(ID3D12VideoEncoder).GUID;
        void* made;
        Call(_video, 14, &desc, &iid, &made, "CreateVideoEncoder");
        return new ID3D12VideoEncoder((nint)made);
    }

    private ID3D12Resource Texture(int slices, ResourceFlags flags)
    {
        var desc = ResourceDescription.Texture2D(Format.NV12, (uint)_codedWidth, (uint)_codedHeight, (ushort)slices, 1, 1, 0, flags);
        try { return _device.CreateCommittedResource(HeapType.Default, desc, ResourceStates.Common, null); }
        catch (SharpGenException) when (flags != ResourceFlags.None)
        {
            desc.Flags = flags & ~ResourceFlags.DenyShaderResource;
            return _device.CreateCommittedResource(HeapType.Default, desc, ResourceStates.Common, null);
        }
    }

    private EncoderSupport Support(Resolution* res)
    {
        var cfg = _config;
        var gop = _gop;
        var rate = _rate;
        VideoEncoderProfileH264 suggestedProfile = 0;
        VideoEncoderLevelsH264 suggestedLevel = 0;
        var limits = new ResolutionLimits();
        var q = new EncoderSupport
        {
            Codec = VideoEncoderCodec.H264,
            InputFormat = Format.NV12,
            CodecConfiguration = Of(&cfg),
            Gop = Of(&gop),
            RateControl = new RateControl { Mode = VideoEncoderRateControlMode.Vbr, Flags = _rateFlags, Config = Of(&rate), FpsNumerator = _fps, FpsDenominator = 1 },
            IntraRefresh = VideoEncoderIntraRefreshMode.None,
            Layout = VideoEncoderFrameSubregionLayoutMode.FullFrame,
            ResolutionsCount = 1,
            Resolutions = res,
            MaxReferenceFramesInDpb = 1,
            SuggestedProfile = Of(&suggestedProfile),
            SuggestedLevel = Of(&suggestedLevel),
            Limits = &limits,
        };
        if (Check(FeatureVideo.EncoderSupport, ref q) < 0) throw new NotSupportedException("the GPU's encoder does not answer what it supports");
        return q;
    }

    public void RequestKeyframe() => _forceKey = true;

    public void SetBitrate(int bps)
    {
        if (Math.Abs(bps - _bitrate) * 20 < _bitrate) return;
        _bitrate = bps;
        (_rate.TargetBitRate, _rate.PeakBitRate) = ((ulong)bps, (ulong)bps * 5 / 4);
        _rateChanged = true;
    }

    public void Encode(Nv12Frame frame, long time, long duration) =>
        throw new NotSupportedException("frames come as GPU textures");

    /// <summary>Encodes what the converter just wrote into <see cref="Input"/>.</summary>
    public void Encode(ID3D11Texture2D texture, long time, long duration)
    {
        if (texture.NativePointer != _input11.NativePointer) throw new ArgumentException("frames come in the encoder's own texture");
        try { EncodeFrame(duration); }
        catch
        {
            Broken = true;
            throw;
        }
    }

    private void EncodeFrame(long duration)
    {
        // The encoder starts once the converter is done with the frame.
        _context4.Signal(_fence11, ++_fenceValue);
        _context11.Flush();
        _queue.Wait(_fence, _fenceValue).CheckError();

        // Drivers that cannot change the rate of a running encoder take it on a new one, which
        // goes on from the same references: the rate travels with each frame, the encoder object
        // holds nothing else that changes.
        bool fresh = _rateChanged && !_canChangeRate;
        if (fresh)
        {
            _encoder.Dispose();
            _encoder = NewEncoder();
        }
        bool key = _forceKey || _reference < 0 || _sinceKey >= _gop.GopLength;
        if (key)
        {
            _frameNum = 0;
            _poc = 0;
            _sinceKey = 0;
        }

        _allocator.Reset();
        _list.Reset(_allocator);

        var barriers = stackalloc Barrier[16];
        int n = 0;
        Transition(barriers, ref n, _input, AllSubresources, StateCommon, StateEncodeRead);
        ReconTransition(barriers, ref n, _target, StateCommon, StateEncodeWrite);
        if (!key) ReconTransition(barriers, ref n, _reference, StateCommon, StateEncodeRead);
        Transition(barriers, ref n, _bitstream, AllSubresources, StateCommon, StateEncodeWrite);
        Transition(barriers, ref n, _metadata, AllSubresources, StateCommon, StateEncodeWrite);
        Barriers(n, barriers);

        var cfgProfile = _profile;
        var gop = _gop;
        var rate = _rate;
        // One reference: the frame before, by its slot in the textures handed over.
        uint list0 = 0;
        var refDesc = new H264Reference { ReconIndex = 0, Poc = _refPoc, FrameNum = _refFrameNum };
        var picture = new H264Picture
        {
            FrameType = key ? VideoEncoderFrameTypeH264.IdrFrame : VideoEncoderFrameTypeH264.PFrame,
            IdrPicId = _idrId,
            Poc = _poc,
            FrameNum = _frameNum,
            List0Count = key ? 0u : 1u,
            List0 = key ? null : &list0,
            DescriptorCount = key ? 0u : 1u,
            Descriptors = key ? null : &refDesc,
        };
        nint refTexture = _recon[_reconArray ? 0 : Math.Max(0, _reference)].NativePointer;
        uint refSub = _reconArray ? (uint)Math.Max(0, _reference) : 0;
        var input = new EncodeInput
        {
            Sequence = new SequenceControl
            {
                Flags = _rateChanged && !fresh ? VideoEncoderSequenceControlFlags.RateControlChange : 0,
                RateControl = new RateControl { Mode = VideoEncoderRateControlMode.Vbr, Flags = _rateFlags, Config = Of(&rate), FpsNumerator = _fps, FpsDenominator = 1 },
                Resolution = new Resolution { Width = (uint)_codedWidth, Height = (uint)_codedHeight },
                Layout = VideoEncoderFrameSubregionLayoutMode.FullFrame,
                Gop = Of(&gop),
            },
            Picture = new PictureControl
            {
                Flags = VideoEncoderPictureControlFlags.UsedAsReferencePicture,
                CodecData = Of(&picture),
                References = key ? default : new ReferenceFrames { Count = 1, Textures = &refTexture, Subresources = _reconArray ? &refSub : null },
            },
            InputFrame = _input.NativePointer,
        };
        var output = new EncodeOutput
        {
            Bitstream = _bitstream.NativePointer,
            Recon = _recon[_reconArray ? 0 : _target].NativePointer,
            ReconSubresource = _reconArray ? (uint)_target : 0,
            Metadata = _metadata.NativePointer,
        };
        var vtbl = *(void***)_list.NativePointer;
        ((delegate* unmanaged<nint, nint, nint, EncodeInput*, EncodeOutput*, void>)vtbl[27])(
            _list.NativePointer, _encoder.NativePointer, _heap.NativePointer, &input, &output);

        n = 0;
        Transition(barriers, ref n, _metadata, AllSubresources, StateEncodeWrite, StateEncodeRead);
        Transition(barriers, ref n, _resolved, AllSubresources, StateCommon, StateEncodeWrite);
        Barriers(n, barriers);
        var resolveIn = new ResolveInput
        {
            Codec = VideoEncoderCodec.H264, Profile = Of(&cfgProfile), InputFormat = Format.NV12,
            Resolution = new Resolution { Width = (uint)_codedWidth, Height = (uint)_codedHeight },
            Metadata = _metadata.NativePointer,
        };
        var resolveOut = new ResolveOutput { Resolved = _resolved.NativePointer };
        ((delegate* unmanaged<nint, ResolveInput*, ResolveOutput*, void>)vtbl[28])(_list.NativePointer, &resolveIn, &resolveOut);

        // Everything back to common, where the other device and the processor may use it.
        n = 0;
        Transition(barriers, ref n, _input, AllSubresources, StateEncodeRead, StateCommon);
        ReconTransition(barriers, ref n, _target, StateEncodeWrite, StateCommon);
        if (!key) ReconTransition(barriers, ref n, _reference, StateEncodeRead, StateCommon);
        Transition(barriers, ref n, _bitstream, AllSubresources, StateEncodeWrite, StateCommon);
        Transition(barriers, ref n, _metadata, AllSubresources, StateEncodeRead, StateCommon);
        Transition(barriers, ref n, _resolved, AllSubresources, StateEncodeWrite, StateCommon);
        Barriers(n, barriers);
        _list.Close();

        _queue.ExecuteCommandList(_list);
        _queue.Signal(_fence, ++_fenceValue).CheckError();
        if (_fence.CompletedValue < _fenceValue)
        {
            _fence.SetEventOnCompletion(_fenceValue, _done).CheckError();
            if (!_done.WaitOne(2000)) throw new TimeoutException("the GPU's encoder did not finish a frame in 2 s");
        }
        _rateChanged = false;

        if (_result->ErrorFlags != 0)
        {
            // Start over from a keyframe, nothing of this frame is used.
            _forceKey = true;
            Logger.Instance.Warning($"Screen sharing: the GPU's encoder failed a frame ({(VideoEncoderEncodeErrorFlags)_result->ErrorFlags})");
            return;
        }
        WriteOut(key, duration);

        // What was built is the reference of the next frame.
        _forceKey = false;
        _refFrameNum = _frameNum;
        _refPoc = _poc;
        _reference = _target;
        _target = 1 - _target;
        _frameNum = (_frameNum + 1) & ((1u << (Log2MaxFrameNumMinus4 + 4)) - 1);
        _poc += 2;
        _sinceKey++;
        if (key) _idrId = (_idrId + 1) & 0xFFFF;
    }

    /// <summary>The frame as one access unit: the headers before a keyframe, then the slices with their start codes.</summary>
    private void WriteOut(bool key, long duration)
    {
        _au.ResetWrittenCount();
        if (key) _au.Write(_headers);
        var regions = (SubregionMetadata*)(_result + 1);
        ulong at = 0, total = _result->WrittenBytes;
        int count = (int)Math.Min(_result->Subregions, (4096UL - (ulong)sizeof(OutputMetadata)) / (ulong)sizeof(SubregionMetadata));
        for (int i = 0; i < count; i++)
        {
            at += regions[i].StartOffset;
            ulong size = regions[i].Size;
            if (at + size > _bitstreamSize) break;
            var slice = new ReadOnlySpan<byte>(_bits + at, (int)size);
            // Drivers write the slices as NAL units with start codes; one that leaves them out gets them.
            if (!(slice.Length > 3 && slice[0] == 0 && slice[1] == 0 && (slice[2] == 1 || slice[2] == 0 && slice[3] == 1)))
                _au.Write(StartCode);
            _au.Write(slice);
            at += size;
        }
        if (count == 0 && total > 0 && total <= _bitstreamSize) _au.Write(new ReadOnlySpan<byte>(_bits, (int)total));
        Output?.Invoke(_au.WrittenMemory, duration, key);
    }

    private void ReconTransition(Barrier* b, ref int n, int slot, uint before, uint after)
    {
        if (!_reconArray)
        {
            Transition(b, ref n, _recon[slot], AllSubresources, before, after);
            return;
        }
        // NV12 in an array of two: plane 0 of a slice is subresource slice, plane 1 is slice + 2.
        Transition(b, ref n, _recon[0], (uint)slot, before, after);
        Transition(b, ref n, _recon[0], (uint)slot + 2, before, after);
    }

    private void Barriers(int count, Barrier* barriers)
    {
        var vtbl = *(void***)_list.NativePointer;
        ((delegate* unmanaged<nint, uint, Barrier*, void>)vtbl[12])(_list.NativePointer, (uint)count, barriers);
    }

    private static void Transition(Barrier* b, ref int n, ID3D12Resource resource, uint subresource, uint before, uint after) =>
        b[n++] = new Barrier { Resource = resource.NativePointer, Subresource = subresource, Before = before, After = after };

    // ---- H.264 headers ----

    /// <summary>
    /// The sequence and picture parameter sets for what the driver encodes: the fields the slices
    /// depend on follow the configuration the encoder was made with, as Direct3D 12 lays down.
    /// The video usability part says BT.709 at video range, and that each frame may be shown at
    /// once and needs one frame kept, which keeps a decoder's latency and memory low.
    /// </summary>
    private byte[] Headers(byte levelIdc)
    {
        bool high = _profile == VideoEncoderProfileH264.High;
        int mbWidth = (Width + 15) / 16, mbHeight = (Height + 15) / 16;
        int cropRight = (mbWidth * 16 - Width) / 2, cropBottom = (mbHeight * 16 - Height) / 2;

        var sps = new BitWriter();
        sps.Put(8, high ? 100u : 77u);
        sps.Put(8, high ? 0u : 0x40u); // constraint_set1 for Main
        sps.Put(8, levelIdc);
        sps.Ue(0); // seq_parameter_set_id
        if (high)
        {
            sps.Ue(1); // 4:2:0
            sps.Ue(0); // 8 bit luma
            sps.Ue(0); // 8 bit chroma
            sps.Put(1, 0); // qpprime_y_zero_transform_bypass_flag
            sps.Put(1, 0); // no scaling matrices
        }
        sps.Ue(_gop.Log2MaxFrameNumMinus4);
        sps.Ue(_gop.PocType);
        if (_gop.PocType == 0) sps.Ue(_gop.Log2MaxPocLsbMinus4);
        sps.Ue(1); // max_num_ref_frames
        sps.Put(1, 0); // gaps_in_frame_num_value_allowed_flag
        sps.Ue((uint)mbWidth - 1);
        sps.Ue((uint)mbHeight - 1);
        sps.Put(1, 1); // frame_mbs_only_flag
        sps.Put(1, 1); // direct_8x8_inference_flag
        bool crop = cropRight > 0 || cropBottom > 0;
        sps.Put(1, crop ? 1u : 0u);
        if (crop)
        {
            sps.Ue(0);
            sps.Ue((uint)cropRight);
            sps.Ue(0);
            sps.Ue((uint)cropBottom);
        }
        sps.Put(1, 1); // vui_parameters_present_flag
        sps.Put(1, 0); // aspect_ratio_info_present_flag
        sps.Put(1, 0); // overscan_info_present_flag
        sps.Put(1, 1); // video_signal_type_present_flag
        sps.Put(3, 5); // video_format: unspecified
        sps.Put(1, 0); // video_full_range_flag
        sps.Put(1, 1); // colour_description_present_flag
        sps.Put(8, 1); // BT.709 primaries
        sps.Put(8, 1); // BT.709 transfer
        sps.Put(8, 1); // BT.709 matrix
        sps.Put(1, 0); // chroma_loc_info_present_flag
        sps.Put(1, 0); // timing_info_present_flag: frames come when the screen changes
        sps.Put(1, 0); // nal_hrd_parameters_present_flag
        sps.Put(1, 0); // vcl_hrd_parameters_present_flag
        sps.Put(1, 0); // pic_struct_present_flag
        sps.Put(1, 1); // bitstream_restriction_flag
        sps.Put(1, 1); // motion_vectors_over_pic_boundaries_flag
        sps.Ue(0); // max_bytes_per_pic_denom
        sps.Ue(0); // max_bits_per_mb_denom
        sps.Ue(15); // log2_max_mv_length_horizontal
        sps.Ue(15); // log2_max_mv_length_vertical
        sps.Ue(0); // max_num_reorder_frames
        sps.Ue(1); // max_dec_frame_buffering

        var pps = new BitWriter();
        pps.Ue(0); // pic_parameter_set_id
        pps.Ue(0); // seq_parameter_set_id
        pps.Put(1, (_config.Flags & VideoEncoderCodecConfigurationH264Flags.EnableCabacEncoding) != 0 ? 1u : 0u);
        pps.Put(1, 0); // bottom_field_pic_order_in_frame_present_flag
        pps.Ue(0); // num_slice_groups_minus1
        pps.Ue(0); // num_ref_idx_l0_default_active_minus1
        pps.Ue(0); // num_ref_idx_l1_default_active_minus1
        pps.Put(1, 0); // weighted_pred_flag
        pps.Put(2, 0); // weighted_bipred_idc
        pps.Se(0); // pic_init_qp_minus26
        pps.Se(0); // pic_init_qs_minus26
        pps.Se(0); // chroma_qp_index_offset
        pps.Put(1, 1); // deblocking_filter_control_present_flag
        pps.Put(1, (_config.Flags & VideoEncoderCodecConfigurationH264Flags.UseConstrainedIntraprediction) != 0 ? 1u : 0u);
        pps.Put(1, 0); // redundant_pic_cnt_present_flag
        if (high)
        {
            pps.Put(1, (_config.Flags & VideoEncoderCodecConfigurationH264Flags.UseAdaptive8x8Transform) != 0 ? 1u : 0u);
            pps.Put(1, 0); // pic_scaling_matrix_present_flag
            pps.Se(0); // second_chroma_qp_index_offset
        }
        return [.. sps.Nal(0x67), .. pps.Nal(0x68)];
    }

    private static readonly (VideoEncoderLevelsH264 Level, byte Idc, int MaxFs, int MaxMbps, int MaxKbps)[] Levels =
    {
        (VideoEncoderLevelsH264.Level31, 31, 3600, 108_000, 14_000),
        (VideoEncoderLevelsH264.Level32, 32, 5120, 216_000, 20_000),
        (VideoEncoderLevelsH264.Level4, 40, 8192, 245_760, 20_000),
        (VideoEncoderLevelsH264.Level41, 41, 8192, 245_760, 50_000),
        (VideoEncoderLevelsH264.Level42, 42, 8704, 522_240, 50_000),
        (VideoEncoderLevelsH264.Level5, 50, 22_080, 589_824, 135_000),
        (VideoEncoderLevelsH264.Level51, 51, 36_864, 983_040, 240_000),
        (VideoEncoderLevelsH264.Level52, 52, 36_864, 2_073_600, 240_000),
        (VideoEncoderLevelsH264.Level6, 60, 139_264, 4_177_920, 240_000),
        (VideoEncoderLevelsH264.Level61, 61, 139_264, 8_355_840, 480_000),
        (VideoEncoderLevelsH264.Level62, 62, 139_264, 16_711_680, 800_000),
    };

    /// <summary>The lowest level that holds this size and rate at the most the link may give, 40 Mbit/s; High profile may go a quarter over.</summary>
    private static (VideoEncoderLevelsH264 Level, byte Idc) LevelFor(int width, int height, int fps, bool high)
    {
        int mbw = (width + 15) / 16, mbh = (height + 15) / 16, mbs = mbw * mbh;
        foreach (var l in Levels)
            if (mbs <= l.MaxFs && mbw * mbw <= l.MaxFs * 8 && mbh * mbh <= l.MaxFs * 8
                && (long)mbs * fps <= l.MaxMbps && l.MaxKbps * (high ? 1250L : 1000L) >= 40_000_000)
                return (l.Level, l.Idc);
        return (VideoEncoderLevelsH264.Level62, 62);
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private uint _current;
        private int _count;

        public void Put(int bits, uint value)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _current = (_current << 1) | ((value >> i) & 1);
                if (++_count == 8)
                {
                    _bytes.Add((byte)_current);
                    (_current, _count) = (0, 0);
                }
            }
        }

        public void Ue(uint value)
        {
            uint v = value + 1;
            int length = 32 - BitOperations.LeadingZeroCount(v);
            Put(length - 1, 0);
            Put(length, v);
        }

        public void Se(int value) => Ue(value <= 0 ? (uint)(-2 * value) : (uint)(2 * value - 1));

        /// <summary>The NAL unit with a start code: trailing bits, and emulation prevention where needed.</summary>
        public byte[] Nal(byte header)
        {
            Put(1, 1);
            while (_count != 0) Put(1, 0);
            var nal = new List<byte> { 0, 0, 0, 1, header };
            int zeros = 0;
            foreach (var b in _bytes)
            {
                if (zeros >= 2 && b <= 3)
                {
                    nal.Add(3);
                    zeros = 0;
                }
                nal.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }
            return nal.ToArray();
        }
    }

    // ---- calls the bindings get wrong: their structs with unions have the wrong size ----

    private int Check<T>(FeatureVideo feature, ref T data) where T : unmanaged
    {
        fixed (T* p = &data)
        {
            var vtbl = *(void***)_video.NativePointer;
            return ((delegate* unmanaged<nint, FeatureVideo, void*, uint, int>)vtbl[3])(_video.NativePointer, feature, p, (uint)sizeof(T));
        }
    }

    private static void Call(ComObject target, int slot, void* desc, Guid* iid, void** made, string what)
    {
        var vtbl = *(void***)target.NativePointer;
        int hr = ((delegate* unmanaged<nint, void*, Guid*, void**, int>)vtbl[slot])(target.NativePointer, desc, iid, made);
        if (hr < 0) throw new NotSupportedException($"{what} failed: 0x{hr:X8}");
    }

    private static Blob Of<T>(T* data) where T : unmanaged => new() { DataSize = (uint)sizeof(T), Data = data };

    private static int Align(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    // d3d12video.h, x64 layouts.
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint DataSize; public void* Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Resolution { public uint Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct CodecSupport { public uint NodeIndex; public VideoEncoderCodec Codec; public int IsSupported; }
    [StructLayout(LayoutKind.Sequential)] private struct ProfileLevelSupport { public uint NodeIndex; public VideoEncoderCodec Codec; public Blob Profile; public int IsSupported; public Blob MinLevel, MaxLevel; }
    [StructLayout(LayoutKind.Sequential)] private struct InputFormatSupport { public uint NodeIndex; public VideoEncoderCodec Codec; public Blob Profile; public Format Format; public int IsSupported; }
    [StructLayout(LayoutKind.Sequential)] private struct RateControlSupport { public uint NodeIndex; public VideoEncoderCodec Codec; public VideoEncoderRateControlMode Mode; public int IsSupported; }
    [StructLayout(LayoutKind.Sequential)] private struct ConfigSupport { public uint NodeIndex; public VideoEncoderCodec Codec; public Blob Profile; public int IsSupported; public Blob Limits; }
    [StructLayout(LayoutKind.Sequential)] private struct H264ConfigSupport { public VideoEncoderCodecConfigurationSupportH264Flags Flags; public uint DeblockingModes; }
    [StructLayout(LayoutKind.Sequential)] private struct H264Config { public VideoEncoderCodecConfigurationH264Flags Flags; public VideoEncoderCodecConfigurationH264DirectModes DirectMode; public VideoEncoderCodecConfigurationH264SlicesDeblockingModes Deblocking; }
    [StructLayout(LayoutKind.Sequential)] private struct H264Gop { public uint GopLength, PPicturePeriod; public byte PocType, Log2MaxFrameNumMinus4, Log2MaxPocLsbMinus4; }

    [StructLayout(LayoutKind.Sequential)] private struct Vbr { public uint InitialQP, MinQP, MaxQP; public ulong MaxFrameBitSize, TargetBitRate, PeakBitRate, VbvCapacity, InitialVbvFullness; }
    [StructLayout(LayoutKind.Sequential)] private struct RateControl { public VideoEncoderRateControlMode Mode; public VideoEncoderRateControlFlags Flags; public Blob Config; public uint FpsNumerator, FpsDenominator; }
    [StructLayout(LayoutKind.Sequential)] private struct RatiosCount { public uint NodeIndex; public VideoEncoderCodec Codec; public uint Count; }
    [StructLayout(LayoutKind.Sequential)] private struct OutputResolution { public uint NodeIndex; public VideoEncoderCodec Codec; public uint RatiosCount; public int IsSupported; public Resolution Min, Max; public uint WidthMultiple, HeightMultiple; public void* Ratios; }
    [StructLayout(LayoutKind.Sequential)] private struct ResolutionLimits { public uint MaxSubregions, MaxIntraRefreshFrames, SubregionBlockPixels, QpMapRegionPixels; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EncoderSupport
    {
        public uint NodeIndex;
        public VideoEncoderCodec Codec;
        public Format InputFormat;
        public Blob CodecConfiguration;
        public Blob Gop;
        public RateControl RateControl;
        public VideoEncoderIntraRefreshMode IntraRefresh;
        public VideoEncoderFrameSubregionLayoutMode Layout;
        public uint ResolutionsCount;
        public Resolution* Resolutions;
        public uint MaxReferenceFramesInDpb;
        public VideoEncoderValidationFlags Validation;
        public VideoEncoderSupportFlags Support;
        public Blob SuggestedProfile;
        public Blob SuggestedLevel;
        public ResolutionLimits* Limits;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ResourceRequirements { public uint NodeIndex; public VideoEncoderCodec Codec; public Blob Profile; public Format InputFormat; public Resolution Resolution; public int IsSupported; public uint BitstreamAlignment, MetadataAlignment, MaxMetadataSize; }
    [StructLayout(LayoutKind.Sequential)] private struct EncoderDesc { public uint NodeMask, Flags; public VideoEncoderCodec Codec; public Blob Profile; public Format InputFormat; public Blob Configuration; public VideoEncoderMotionEstimationPrecisionMode MaxMotionPrecision; }
    [StructLayout(LayoutKind.Sequential)] private struct HeapDesc { public uint NodeMask, Flags; public VideoEncoderCodec Codec; public Blob Profile; public Blob Level; public uint ResolutionsCount; public Resolution* Resolutions; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SequenceControl
    {
        public VideoEncoderSequenceControlFlags Flags;
        public VideoEncoderIntraRefreshMode IntraRefreshMode;
        public uint IntraRefreshDuration;
        public RateControl RateControl;
        public Resolution Resolution;
        public VideoEncoderFrameSubregionLayoutMode Layout;
        public Blob LayoutData;
        public Blob Gop;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ReferenceFrames { public uint Count; public nint* Textures; public uint* Subresources; }
    [StructLayout(LayoutKind.Sequential)] private struct PictureControl { public uint IntraRefreshFrameIndex; public VideoEncoderPictureControlFlags Flags; public Blob CodecData; public ReferenceFrames References; }
    [StructLayout(LayoutKind.Sequential)] private struct EncodeInput { public SequenceControl Sequence; public PictureControl Picture; public nint InputFrame; public uint InputSubresource, HeaderBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct EncodeOutput { public nint Bitstream; public ulong BitstreamOffset; public nint Recon; public uint ReconSubresource; public nint Metadata; public ulong MetadataOffset; }

    [StructLayout(LayoutKind.Sequential)]
    private struct H264Picture
    {
        public uint Flags;
        public VideoEncoderFrameTypeH264 FrameType;
        public uint PicParameterSetId, IdrPicId, Poc, FrameNum, TemporalLayer;
        public uint List0Count;
        public uint* List0;
        public uint List1Count;
        public uint* List1;
        public uint DescriptorCount;
        public H264Reference* Descriptors;
        public byte AdaptiveRefPicMarking;
        public uint MarkingCount;
        public void* Marking;
        public uint List0ModificationCount;
        public void* List0Modifications;
        public uint List1ModificationCount;
        public void* List1Modifications;
        public uint QpMapCount;
        public sbyte* QpMap;
    }

    [StructLayout(LayoutKind.Sequential)] private struct H264Reference { public uint ReconIndex; public int IsLongTerm; public uint LongTermIndex, Poc, FrameNum, TemporalLayer; }
    [StructLayout(LayoutKind.Sequential)] private struct ResolveInput { public VideoEncoderCodec Codec; public Blob Profile; public Format InputFormat; public Resolution Resolution; public nint Metadata; public ulong MetadataOffset; }
    [StructLayout(LayoutKind.Sequential)] private struct ResolveOutput { public nint Resolved; public ulong Offset; }
    [StructLayout(LayoutKind.Sequential)] private struct OutputMetadata { public ulong ErrorFlags, AverageQp, Intra, Inter, Skip, MotionX, MotionY, WrittenBytes, Subregions; }
    [StructLayout(LayoutKind.Sequential)] private struct SubregionMetadata { public ulong Size, StartOffset, HeaderSize; }
    [StructLayout(LayoutKind.Sequential)] private struct Barrier { public uint Type, Flags; public nint Resource; public uint Subresource, Before, After; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Let the GPU finish what it still does with the textures.
        try
        {
            if (_queue != null && _fence != null)
            {
                _queue.Signal(_fence, ++_fenceValue);
                if (_fence.CompletedValue < _fenceValue)
                {
                    _fence.SetEventOnCompletion(_fenceValue, _done);
                    _done.WaitOne(1000);
                }
            }
        }
        catch (SharpGenException) { }
        _resolved?.Dispose();
        _metadata?.Dispose();
        _bitstream?.Dispose();
        if (_recon != null) foreach (var r in _recon) r?.Dispose();
        _input?.Dispose();
        _input11?.Dispose();
        _context4?.Dispose();
        _fence11?.Dispose();
        _fence?.Dispose();
        _list?.Dispose();
        _allocator?.Dispose();
        _queue?.Dispose();
        _heap?.Dispose();
        _encoder?.Dispose();
        _video?.Dispose();
        _device?.Dispose();
        _done.Dispose();
    }
}
