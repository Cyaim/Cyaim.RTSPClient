using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Media.Codecs;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Video.Encoders
{
    /// <summary>
    /// FFmpeg 视频编码器基类 (支持硬件加速)
    /// </summary>
    public unsafe abstract class FFmpegVideoEncoder : IVideoEncoder
    {
        protected AVCodecContext* _codecCtx;
        protected AVFrame* _frame;
        protected AVFrame* _hwFrame;
        protected AVPacket* _packet;
        protected AVBufferRef* _hwDeviceCtx;
        protected AVBufferRef* _hwFramesCtx;
        protected bool _disposed;
        protected ProcessorState _state = ProcessorState.Idle;
        protected int _frameNumber;
        protected HardwareAccelerationType _hwType = HardwareAccelerationType.None;

        public abstract string Name { get; }
        public bool IsHardwareAccelerated => _hwType != HardwareAccelerationType.None;
        public ProcessorState State => _state;
        public abstract VideoCodec[] SupportedCodecs { get; }

        public VideoEncoderCapabilities Capabilities => new()
        {
            MaxWidth = 8192,
            MaxHeight = 8192,
            SupportsHardwareAcceleration = _hwType != HardwareAccelerationType.None,
            HardwareVendor = _hwType.ToString(),
            SupportedPresets = _hwType switch
            {
                HardwareAccelerationType.Nvenc => new[] { "fast", "medium", "slow", "hq" },
                HardwareAccelerationType.Qsv => new[] { "fast", "medium", "slow" },
                _ => new[] { "ultrafast", "fast", "medium", "slow" }
            }
        };

        protected FFmpegVideoEncoder() => FFmpegHelper.Initialize();

        public virtual Task InitializeAsync(VideoEncoderConfig config, CancellationToken ct = default)
        {
            var codecId = GetCodecId(config.Codec);

            // 硬件编码器名称
            AVCodec* codec = null;
            if (config.EnableHardwareAcceleration)
            {
                var hwConfig = new HardwareAccelerationConfig
                {
                    Type = !string.IsNullOrEmpty(config.HardwareDevice)
                        ? ParseHardwareType(config.HardwareDevice)
                        : HardwareAccelerationType.None
                };

                if (hwConfig.Type == HardwareAccelerationType.None)
                    hwConfig.Type = FFmpegHardwareHelper.GetBestType(hwConfig);

                if (hwConfig.Type != HardwareAccelerationType.None)
                {
                    // 尝试查找硬件编码器
                    var hwEncoderName = GetHardwareEncoderName(config.Codec, hwConfig.Type);
                    if (!string.IsNullOrEmpty(hwEncoderName))
                        codec = ffmpeg.avcodec_find_encoder_by_name(hwEncoderName);
                }

                if (codec != null)
                    _hwType = hwConfig.Type;
            }

            // 回退到软件编码器
            if (codec == null)
                codec = ffmpeg.avcodec_find_encoder(codecId);

            if (codec == null)
                throw new InvalidOperationException($"Encoder not found: {config.Codec}");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null)
                throw new InvalidOperationException("Failed to allocate encoder context");

            _codecCtx->width = config.Width;
            _codecCtx->height = config.Height;
            _codecCtx->bit_rate = config.Bitrate > 0 ? config.Bitrate : 2000000;
            _codecCtx->time_base = new AVRational { num = 1, den = config.Framerate > 0 ? config.Framerate : 30 };
            _codecCtx->framerate = new AVRational { num = config.Framerate > 0 ? config.Framerate : 30, den = 1 };
            _codecCtx->gop_size = config.GopSize > 0 ? config.GopSize : 30;
            _codecCtx->max_b_frames = config.BFrames;

            // 像素格式：硬件编码器（h264_qsv/hevc_qsv/nvenc/amf 等）接受系统内存帧
            // （NV12/YUV420P），由驱动内部上传 GPU——不要设置 AV_PIX_FMT_QSV 等
            // 硬件表面格式（那需要完整的 hw_frames_ctx 配置，旧实现因此打不开）
            _codecCtx->pix_fmt = SelectEncoderPixelFormat(codec);

            // 设置预设
            if (!string.IsNullOrEmpty(config.Preset))
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", config.Preset, 0);

            if (!string.IsNullOrEmpty(config.Profile))
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", config.Profile, 0);

            // 打开编码器（硬件编码器初始化失败时回退软件编码器）
            var openError = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            if (openError < 0 && _hwType != HardwareAccelerationType.None)
            {
                fixed (AVCodecContext** p = &_codecCtx)
                    ffmpeg.avcodec_free_context(p);

                _hwType = HardwareAccelerationType.None;
                codec = ffmpeg.avcodec_find_encoder(codecId);
                if (codec == null)
                    throw new InvalidOperationException($"Encoder not found: {config.Codec}");

                _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                _codecCtx->width = config.Width;
                _codecCtx->height = config.Height;
                _codecCtx->bit_rate = config.Bitrate > 0 ? config.Bitrate : 2000000;
                _codecCtx->time_base = new AVRational { num = 1, den = config.Framerate > 0 ? config.Framerate : 30 };
                _codecCtx->framerate = new AVRational { num = config.Framerate > 0 ? config.Framerate : 30, den = 1 };
                _codecCtx->gop_size = config.GopSize > 0 ? config.GopSize : 30;
                _codecCtx->max_b_frames = config.BFrames;
                _codecCtx->pix_fmt = SelectEncoderPixelFormat(codec);
                if (!string.IsNullOrEmpty(config.Preset))
                    ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", config.Preset, 0);

                openError = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            }
            FFmpegHelper.CheckError(openError);

            // 分配输入帧（编码器实际采用的像素格式）
            _frame = ffmpeg.av_frame_alloc();
            _frame->width = config.Width;
            _frame->height = config.Height;
            _frame->format = (int)_codecCtx->pix_fmt;
            FFmpegHelper.CheckError(ffmpeg.av_frame_get_buffer(_frame, 0));

            _packet = ffmpeg.av_packet_alloc();
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        /// <summary>
        /// 选择编码器可接受的系统内存像素格式（优先 YUV420P，其次 NV12）
        /// </summary>
        private static AVPixelFormat SelectEncoderPixelFormat(AVCodec* codec)
        {
            // pix_fmts 在 FFmpeg 7.x 标记弃用但仍填充；旧字段保证跨版本兼容
#pragma warning disable CS0618
            var fmts = codec->pix_fmts;
#pragma warning restore CS0618
            if (fmts == null)
                return AVPixelFormat.AV_PIX_FMT_YUV420P;

            bool hasNv12 = false;
            AVPixelFormat first = AVPixelFormat.AV_PIX_FMT_NONE;
            for (var p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == AVPixelFormat.AV_PIX_FMT_YUV420P)
                    return AVPixelFormat.AV_PIX_FMT_YUV420P;
                if (*p == AVPixelFormat.AV_PIX_FMT_NV12)
                    hasNv12 = true;
                if (first == AVPixelFormat.AV_PIX_FMT_NONE)
                    first = *p;
            }

            if (hasNv12)
                return AVPixelFormat.AV_PIX_FMT_NV12;
            return first != AVPixelFormat.AV_PIX_FMT_NONE ? first : AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        private readonly Queue<EncodedVideoFrame> _pendingPackets = new();
        private readonly Dictionary<long, long> _ptsToTimestamp = new();

        public virtual Task<EncodedVideoFrame?> EncodeAsync(VideoFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready)
                throw new InvalidOperationException("Encoder not initialized");

            _state = ProcessorState.Processing;

            try
            {
                // 填充输入帧（硬件编码器同样接收系统内存帧，由驱动内部上传）
                FillSoftwareFrame(input);

                // pts=帧号；编码器输出包可能延迟（B 帧/前瞻），用映射把输入时间戳配回正确的输出包
                long pts = _frameNumber++;
                _frame->pts = pts;
                _ptsToTimestamp[pts] = input.Timestamp;

                var error = ffmpeg.avcodec_send_frame(_codecCtx, _frame);
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    DrainEncodedPackets(input.Width, input.Height);
                    error = ffmpeg.avcodec_send_frame(_codecCtx, _frame);
                }
                FFmpegHelper.CheckError(error);

                DrainEncodedPackets(input.Width, input.Height);

                return Task.FromResult(_pendingPackets.Count > 0 ? _pendingPackets.Dequeue() : (EncodedVideoFrame?)null);
            }
            finally
            {
                _state = ProcessorState.Ready;
            }
        }

        /// <summary>
        /// 取出编码器当前可输出的全部包到待输出队列
        /// </summary>
        private void DrainEncodedPackets(int width, int height)
        {
            while (true)
            {
                var error = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF)
                    break;
                FFmpegHelper.CheckError(error);

                try
                {
                    var data = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, data, 0, _packet->size);
                    var isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                    long timestamp = 0;
                    if (_ptsToTimestamp.TryGetValue(_packet->pts, out var ts))
                    {
                        timestamp = ts;
                        _ptsToTimestamp.Remove(_packet->pts);
                    }

                    _pendingPackets.Enqueue(new EncodedVideoFrame
                    {
                        Data = data,
                        Codec = SupportedCodecs[0],
                        Timestamp = timestamp,
                        Type = isKeyFrame ? FrameType.IDR : FrameType.P,
                        Width = width,
                        Height = height
                    });
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }
        }

        public async IAsyncEnumerable<EncodedVideoFrame> EncodeStreamAsync(
            IAsyncEnumerable<VideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            int lastWidth = 0, lastHeight = 0;

            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                lastWidth = frame.Width;
                lastHeight = frame.Height;

                var encoded = await EncodeAsync(frame, ct);
                if (encoded != null)
                    yield return encoded;
                while (_pendingPackets.Count > 0)
                    yield return _pendingPackets.Dequeue();
            }

            // 输入耗尽：flush 编码器，产出缓冲中的尾包
            SendEofAndDrain(lastWidth, lastHeight);
            while (_pendingPackets.Count > 0)
                yield return _pendingPackets.Dequeue();
        }

        private void SendEofAndDrain(int width, int height)
        {
            if (_codecCtx == null)
                return;
            ffmpeg.avcodec_send_frame(_codecCtx, null);
            DrainEncodedPackets(width, height);
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            if (_codecCtx != null)
            {
                ffmpeg.avcodec_send_frame(_codecCtx, null);
                DrainEncodedPackets(_codecCtx->width, _codecCtx->height);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 取出 Flush 后仍滞留在待输出队列中的包
        /// </summary>
        public EncodedVideoFrame? DequeuePendingPacket()
        {
            return _pendingPackets.Count > 0 ? _pendingPackets.Dequeue() : null;
        }

        protected virtual void FillSoftwareFrame(VideoFrame input)
        {
            var writableError = ffmpeg.av_frame_make_writable(_frame);
            FFmpegHelper.CheckError(writableError);

            var data = input.Data.Span;
            int w = input.Width, h = input.Height;
            int ySize = w * h, uvSize = (w / 2) * (h / 2);

            if (data.Length < ySize + uvSize * 2)
                throw new ArgumentException($"YUV420P frame data too small: {data.Length} < {ySize + uvSize * 2}");

            fixed (byte* pSrc = data)
            {
                CopyPlaneToFrame(pSrc, w, _frame->data[0], _frame->linesize[0], w, h);

                if ((AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_NV12)
                {
                    // 编码器要求 NV12（如 QSV）：输入 YUV420P 的 U/V 平面交织写入
                    byte* pU = pSrc + ySize;
                    byte* pV = pSrc + ySize + uvSize;
                    for (int y = 0; y < h / 2; y++)
                    {
                        byte* dst = _frame->data[1] + (long)y * _frame->linesize[1];
                        byte* u = pU + (long)y * (w / 2);
                        byte* v = pV + (long)y * (w / 2);
                        for (int x = 0; x < w / 2; x++)
                        {
                            dst[x * 2] = u[x];
                            dst[x * 2 + 1] = v[x];
                        }
                    }
                }
                else
                {
                    CopyPlaneToFrame(pSrc + ySize, w / 2, _frame->data[1], _frame->linesize[1], w / 2, h / 2);
                    CopyPlaneToFrame(pSrc + ySize + uvSize, w / 2, _frame->data[2], _frame->linesize[2], w / 2, h / 2);
                }
            }
        }

        private static void CopyPlaneToFrame(byte* src, int srcStride, byte* dst, int dstStride, int rowBytes, int rows)
        {
            for (int y = 0; y < rows; y++)
            {
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, rowBytes, rowBytes);
            }
        }

        protected abstract AVCodecID GetCodecId(VideoCodec codec);

        private static string? GetHardwareEncoderName(VideoCodec codec, HardwareAccelerationType hwType)
        {
            return (codec, hwType) switch
            {
                (VideoCodec.H264, HardwareAccelerationType.Cuda) => "h264_nvenc",
                (VideoCodec.H264, HardwareAccelerationType.Qsv) => "h264_qsv",
                (VideoCodec.H264, HardwareAccelerationType.Amf) => "h264_amf",
                (VideoCodec.H264, HardwareAccelerationType.Vaapi) => "h264_vaapi",
                (VideoCodec.H264, HardwareAccelerationType.VideoToolbox) => "h264_videotoolbox",
                (VideoCodec.H265, HardwareAccelerationType.Cuda) => "hevc_nvenc",
                (VideoCodec.H265, HardwareAccelerationType.Qsv) => "hevc_qsv",
                (VideoCodec.H265, HardwareAccelerationType.Amf) => "hevc_amf",
                (VideoCodec.H265, HardwareAccelerationType.Vaapi) => "hevc_vaapi",
                (VideoCodec.H265, HardwareAccelerationType.VideoToolbox) => "hevc_videotoolbox",
                (VideoCodec.VP8, HardwareAccelerationType.Vaapi) => "vp8_vaapi",
                (VideoCodec.VP9, HardwareAccelerationType.Vaapi) => "vp9_vaapi",
                _ => null
            };
        }

        private static HardwareAccelerationType ParseHardwareType(string name)
        {
            return name.ToLower() switch
            {
                "cuda" or "nvenc" => HardwareAccelerationType.Cuda,
                "qsv" => HardwareAccelerationType.Qsv,
                "vaapi" => HardwareAccelerationType.Vaapi,
                "dxva2" => HardwareAccelerationType.Dxva2,
                "d3d11va" => HardwareAccelerationType.D3d11va,
                "videotoolbox" => HardwareAccelerationType.VideoToolbox,
                "amf" => HardwareAccelerationType.Amf,
                "vulkan" => HardwareAccelerationType.Vulkan,
                _ => HardwareAccelerationType.None
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_packet != null) { fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p); }
            if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); }
            if (_hwFrame != null) { fixed (AVFrame** p = &_hwFrame) ffmpeg.av_frame_free(p); }
            if (_hwDeviceCtx != null) { fixed (AVBufferRef** p = &_hwDeviceCtx) ffmpeg.av_buffer_unref(p); }
            if (_hwFramesCtx != null) { fixed (AVBufferRef** p = &_hwFramesCtx) ffmpeg.av_buffer_unref(p); }
            if (_codecCtx != null) { fixed (AVCodecContext** p = &_codecCtx) ffmpeg.avcodec_free_context(p); }

            _state = ProcessorState.Disposed;
        }
    }

    /// <summary>
    /// FFmpeg H.264 编码器 (支持 NVENC/QSV/AMF/VAAPI)
    /// </summary>
    public sealed class H264Encoder : FFmpegVideoEncoder
    {
        public override string Name => IsHardwareAccelerated ? $"FFmpeg H.264 Encoder ({_hwType})" : "FFmpeg H.264 Encoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.H264 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_H264;
    }

    /// <summary>
    /// FFmpeg H.265 编码器 (支持 NVENC/QSV/AMF/VAAPI)
    /// </summary>
    public sealed class H265Encoder : FFmpegVideoEncoder
    {
        public override string Name => IsHardwareAccelerated ? $"FFmpeg H.265 Encoder ({_hwType})" : "FFmpeg H.265 Encoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.H265 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_HEVC;
    }

    /// <summary>
    /// FFmpeg VP8 编码器
    /// </summary>
    public sealed class VP8Encoder : FFmpegVideoEncoder
    {
        public override string Name => "FFmpeg VP8 Encoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.VP8 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_VP8;
    }

    /// <summary>
    /// FFmpeg VP9 编码器 (支持 VAAPI)
    /// </summary>
    public sealed class VP9Encoder : FFmpegVideoEncoder
    {
        public override string Name => IsHardwareAccelerated ? $"FFmpeg VP9 Encoder ({_hwType})" : "FFmpeg VP9 Encoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.VP9 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_VP9;
    }
}
