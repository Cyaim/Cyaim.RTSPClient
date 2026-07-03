using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Media;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Video.Decoders
{
    /// <summary>
    /// FFmpeg 视频解码器基类 (支持硬件加速)
    ///
    /// 正确性说明（相对旧实现）：
    /// - 一个 packet 可能产出 0..N 帧（B 帧重排/多 slice）：内部用输出队列承接全部帧，
    ///   <see cref="DecodeAsync"/> 无帧时返回 null（不再返回会引发 NRE 的 default）
    /// - pts 透传：输出帧时间戳来自解码器 best_effort_timestamp，而非硬绑输入时间戳
    /// - 硬件解码下载后为 NV12：按帧实际像素格式转换（YUV420P/NV12 直拷，其余 sws_scale 兜底），
    ///   不再按三平面读取 NV12 导致空指针崩溃
    /// - 支持 <see cref="VideoDecoderConfig.ExtraData"/>（SDP 带外 SPS/PPS）
    /// </summary>
    public unsafe abstract class FFmpegVideoDecoder : IVideoDecoder
    {
        protected AVCodecContext* _codecCtx;
        protected AVFrame* _frame;
        protected AVFrame* _swFrame;  // 软件帧 (用于硬件加速时的数据传输)
        protected AVPacket* _packet;
        protected SwsContext* _swsCtx;
        protected bool _disposed;
        protected ProcessorState _state = ProcessorState.Idle;
        protected HardwareAccelerationType _hwType = HardwareAccelerationType.None;

        private readonly Queue<VideoFrame> _pendingFrames = new();
        private int _swsSrcFormat = -1;
        private int _swsWidth;
        private int _swsHeight;

        public abstract string Name { get; }
        public bool IsHardwareAccelerated => _hwType != HardwareAccelerationType.None;
        public ProcessorState State => _state;
        public abstract VideoCodec[] SupportedCodecs { get; }

        public VideoDecoderCapabilities Capabilities => new()
        {
            MaxWidth = 8192,
            MaxHeight = 8192,
            SupportsHardwareAcceleration = _hwType != HardwareAccelerationType.None,
            HardwareVendor = _hwType.ToString(),
            SupportedOutputFormats = new[] { PixelFormat.YUV420P, PixelFormat.NV12 }
        };

        protected FFmpegVideoDecoder() => FFmpegHelper.Initialize();

        public virtual Task InitializeAsync(VideoDecoderConfig config, CancellationToken ct = default)
        {
            var codecId = GetCodecId(config.Codec);
            var codec = ffmpeg.avcodec_find_decoder(codecId);
            if (codec == null)
                throw new InvalidOperationException($"Codec not found: {config.Codec}");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null)
                throw new InvalidOperationException("Failed to allocate codec context");

            _codecCtx->width = config.Width;
            _codecCtx->height = config.Height;
            _codecCtx->thread_count = Math.Min(Environment.ProcessorCount, 16);

            if (config.LowLatency)
            {
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            }

            // 带外参数集（H.264/H.265 Annex-B SPS/PPS，来自 SDP）
            if (config.ExtraData is { Length: > 0 })
            {
                SetExtraData(config.ExtraData);
            }

            // 配置硬件加速
            if (config.EnableHardwareAcceleration)
            {
                var hwConfig = new HardwareAccelerationConfig
                {
                    Type = !string.IsNullOrEmpty(config.HardwareDevice)
                        ? ParseHardwareType(config.HardwareDevice!)
                        : HardwareAccelerationType.None,
                    DownloadToCpu = true
                };

                // 自动选择最佳硬件
                if (hwConfig.Type == HardwareAccelerationType.None)
                {
                    hwConfig.Type = FFmpegHardwareHelper.GetBestType(hwConfig);
                }

                if (hwConfig.Type != HardwareAccelerationType.None)
                {
                    var error = FFmpegHardwareHelper.ConfigureHardwareContext(
                        _codecCtx, codec, hwConfig.Type);

                    if (error >= 0)
                    {
                        _hwType = hwConfig.Type;
                    }
                }
            }

            // 打开解码器
            var openError = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            FFmpegHelper.CheckError(openError);

            // 分配帧
            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();

            // 如果使用硬件加速，分配软件帧用于数据传输
            if (_hwType != HardwareAccelerationType.None)
            {
                _swFrame = ffmpeg.av_frame_alloc();
            }

            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        private void SetExtraData(byte[] extraData)
        {
            _codecCtx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(extraData.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            fixed (byte* pSrc = extraData)
            {
                Buffer.MemoryCopy(pSrc, _codecCtx->extradata, extraData.Length, extraData.Length);
            }
            _codecCtx->extradata_size = extraData.Length;
        }

        public virtual Task<VideoFrame?> DecodeAsync(EncodedVideoFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready)
                throw new InvalidOperationException("Decoder not initialized");

            _state = ProcessorState.Processing;
            try
            {
                SendPacket(input);
                DrainDecodedFrames();
                return Task.FromResult(_pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : (VideoFrame?)null);
            }
            finally
            {
                _state = ProcessorState.Ready;
            }
        }

        public async IAsyncEnumerable<VideoFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedVideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var input in inputStream.WithCancellation(ct))
            {
                SendPacket(input);
                DrainDecodedFrames();
                while (_pendingFrames.Count > 0)
                    yield return _pendingFrames.Dequeue();
            }

            // 输入耗尽：flush 解码器，产出重排缓冲中的尾帧
            SendEofAndDrain();
            while (_pendingFrames.Count > 0)
                yield return _pendingFrames.Dequeue();
        }

        private void SendEofAndDrain()
        {
            ffmpeg.avcodec_send_packet(_codecCtx, null);
            DrainDecodedFrames();
        }

        /// <summary>
        /// 发送一个 packet（EAGAIN 时先取出已解码帧再重发）。
        /// H.264/H.265 输入若为裸 NAL（RTSP 解包器 MediaFrame 的形态，无起始码），
        /// 自动补 Annex-B 起始码——FFmpeg 解码器要求起始码分隔的码流。
        /// </summary>
        private void SendPacket(EncodedVideoFrame input)
        {
            var span = input.Data.Span;
            bool needStartCode = NeedsAnnexBStartCode(span, input.Codec);

            byte[]? rented = null;
            try
            {
                ReadOnlySpan<byte> payload;
                if (needStartCode)
                {
                    rented = System.Buffers.ArrayPool<byte>.Shared.Rent(span.Length + 4);
                    rented[0] = 0; rented[1] = 0; rented[2] = 0; rented[3] = 1;
                    span.CopyTo(rented.AsSpan(4));
                    payload = rented.AsSpan(0, span.Length + 4);
                }
                else
                {
                    payload = span;
                }

                fixed (byte* pData = payload)
                {
                    _packet->data = pData;
                    _packet->size = payload.Length;
                    _packet->pts = input.Timestamp;
                    _packet->dts = ffmpeg.AV_NOPTS_VALUE;

                    var error = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                    if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        // 解码器输出缓冲满：先 drain 再重发
                        DrainDecodedFrames();
                        error = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                    }
                    FFmpegHelper.CheckError(error);
                }
            }
            finally
            {
                if (rented != null)
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);

                // packet 数据已被解码器内部复制/引用，复位裸指针避免悬垂
                _packet->data = null;
                _packet->size = 0;
            }
        }

        /// <summary>
        /// H.264/H.265 数据缺少 Annex-B 起始码时返回 true
        /// </summary>
        private static bool NeedsAnnexBStartCode(ReadOnlySpan<byte> data, VideoCodec codec)
        {
            if (codec != VideoCodec.H264 && codec != VideoCodec.H265)
                return false;
            if (data.Length < 4)
                return false;

            // 00 00 01 或 00 00 00 01 开头则已是 Annex-B
            if (data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1)))
                return false;

            return true;
        }

        /// <summary>
        /// 累计解码错误数（坏帧被跳过而不是终止会话，网络流丢包场景常见）
        /// </summary>
        public long DecodeErrorCount { get; private set; }

        /// <summary>
        /// 取出解码器当前可输出的全部帧到待输出队列。
        /// 解码数据错误（如损坏帧）计数后跳过，不向调用方抛异常——
        /// 尤其 Flush 时残留的坏数据不应炸掉调用方。
        /// </summary>
        private void DrainDecodedFrames()
        {
            while (true)
            {
                var error = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF)
                    break;
                if (error < 0)
                {
                    DecodeErrorCount++;
                    break;
                }

                try
                {
                    AVFrame* outputFrame = _frame;

                    // 硬件帧：下载到软件帧
                    if (_hwType != HardwareAccelerationType.None &&
                        _frame->format == (int)FFmpegHardwareHelper.GetHardwarePixelFormat(_hwType))
                    {
                        ffmpeg.av_frame_unref(_swFrame);
                        var transferError = ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0);
                        FFmpegHelper.CheckError(transferError);
                        _swFrame->pts = _frame->pts;
                        _swFrame->best_effort_timestamp = _frame->best_effort_timestamp;
                        _swFrame->pict_type = _frame->pict_type;
                        outputFrame = _swFrame;
                    }

                    _pendingFrames.Enqueue(ConvertFrame(outputFrame));
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }
            }
        }

        /// <summary>
        /// 按帧实际像素格式转换为托管 VideoFrame
        /// </summary>
        protected virtual VideoFrame ConvertFrame(AVFrame* frame)
        {
            int width = frame->width;
            int height = frame->height;
            long timestamp = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? frame->best_effort_timestamp
                : (frame->pts != ffmpeg.AV_NOPTS_VALUE ? frame->pts : 0);

            var frameType = frame->pict_type switch
            {
                AVPictureType.AV_PICTURE_TYPE_I => FrameType.I,
                AVPictureType.AV_PICTURE_TYPE_P => FrameType.P,
                AVPictureType.AV_PICTURE_TYPE_B => FrameType.B,
                _ => FrameType.Unknown
            };

            var format = (AVPixelFormat)frame->format;

            switch (format)
            {
                case AVPixelFormat.AV_PIX_FMT_YUV420P:
                case AVPixelFormat.AV_PIX_FMT_YUVJ420P:
                    return CopyYuv420p(frame, width, height, timestamp, frameType);

                case AVPixelFormat.AV_PIX_FMT_NV12:
                    return CopyNv12(frame, width, height, timestamp, frameType);

                default:
                    // 其它格式（P010 / YUV422 等）用 sws_scale 统一转 YUV420P
                    return ConvertViaSws(frame, width, height, timestamp, frameType);
            }
        }

        private static VideoFrame CopyYuv420p(AVFrame* frame, int width, int height, long timestamp, FrameType frameType)
        {
            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);
            var data = new byte[ySize + uvSize * 2];

            fixed (byte* pDst = data)
            {
                CopyPlane(frame->data[0], frame->linesize[0], pDst, width, width, height);
                CopyPlane(frame->data[1], frame->linesize[1], pDst + ySize, width / 2, width / 2, height / 2);
                CopyPlane(frame->data[2], frame->linesize[2], pDst + ySize + uvSize, width / 2, width / 2, height / 2);
            }

            return new VideoFrame
            {
                Data = data,
                Width = width,
                Height = height,
                Format = PixelFormat.YUV420P,
                Timestamp = timestamp,
                Type = frameType
            };
        }

        private static VideoFrame CopyNv12(AVFrame* frame, int width, int height, long timestamp, FrameType frameType)
        {
            int ySize = width * height;
            int uvSize = width * (height / 2);   // 交织 UV 平面
            var data = new byte[ySize + uvSize];

            fixed (byte* pDst = data)
            {
                CopyPlane(frame->data[0], frame->linesize[0], pDst, width, width, height);
                CopyPlane(frame->data[1], frame->linesize[1], pDst + ySize, width, width, height / 2);
            }

            return new VideoFrame
            {
                Data = data,
                Width = width,
                Height = height,
                Format = PixelFormat.NV12,
                Timestamp = timestamp,
                Type = frameType
            };
        }

        private VideoFrame ConvertViaSws(AVFrame* frame, int width, int height, long timestamp, FrameType frameType)
        {
            // 缓存 SwsContext（源格式/尺寸变化时重建）
            if (_swsCtx == null || _swsSrcFormat != frame->format || _swsWidth != width || _swsHeight != height)
            {
                if (_swsCtx != null)
                    ffmpeg.sws_freeContext(_swsCtx);

                _swsCtx = ffmpeg.sws_getContext(
                    width, height, (AVPixelFormat)frame->format,
                    width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    ffmpeg.SWS_BILINEAR, null, null, null);
                _swsSrcFormat = frame->format;
                _swsWidth = width;
                _swsHeight = height;

                if (_swsCtx == null)
                    throw new FFmpegException($"sws_getContext failed for pixel format {(AVPixelFormat)frame->format}");
            }

            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);
            var data = new byte[ySize + uvSize * 2];

            fixed (byte* pDst = data)
            {
                var dstData = new byte_ptrArray4();
                dstData[0] = pDst;
                dstData[1] = pDst + ySize;
                dstData[2] = pDst + ySize + uvSize;
                var dstLinesize = new int_array4();
                dstLinesize[0] = width;
                dstLinesize[1] = width / 2;
                dstLinesize[2] = width / 2;

                ffmpeg.sws_scale(_swsCtx, frame->data, frame->linesize, 0, height, dstData, dstLinesize);
            }

            return new VideoFrame
            {
                Data = data,
                Width = width,
                Height = height,
                Format = PixelFormat.YUV420P,
                Timestamp = timestamp,
                Type = frameType
            };
        }

        private static void CopyPlane(byte* src, int srcStride, byte* dst, int dstStride, int rowBytes, int rows)
        {
            if (src == null)
                return;

            for (int y = 0; y < rows; y++)
            {
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, rowBytes, rowBytes);
            }
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            if (_codecCtx != null)
            {
                // EOF drain：把重排缓冲中的尾帧全部取出，随后重置以便继续解码
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                DrainDecodedFrames();
                ffmpeg.avcodec_flush_buffers(_codecCtx);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 取出 Flush 后仍滞留在待输出队列中的帧
        /// </summary>
        public VideoFrame? DequeuePendingFrame()
        {
            return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
        }

        protected abstract AVCodecID GetCodecId(VideoCodec codec);

        private static HardwareAccelerationType ParseHardwareType(string name)
        {
            return name.ToLower() switch
            {
                "cuda" or "nvdec" => HardwareAccelerationType.Cuda,
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

            _pendingFrames.Clear();

            if (_swsCtx != null)
            {
                ffmpeg.sws_freeContext(_swsCtx);
                _swsCtx = null;
            }

            if (_packet != null)
            {
                fixed (AVPacket** p = &_packet)
                    ffmpeg.av_packet_free(p);
            }

            if (_frame != null)
            {
                fixed (AVFrame** p = &_frame)
                    ffmpeg.av_frame_free(p);
            }

            if (_swFrame != null)
            {
                fixed (AVFrame** p = &_swFrame)
                    ffmpeg.av_frame_free(p);
            }

            if (_codecCtx != null)
            {
                fixed (AVCodecContext** p = &_codecCtx)
                    ffmpeg.avcodec_free_context(p);
            }

            _state = ProcessorState.Disposed;
        }
    }

    /// <summary>
    /// FFmpeg H.264 解码器 (支持硬件加速)
    /// </summary>
    public sealed class H264Decoder : FFmpegVideoDecoder
    {
        public override string Name => IsHardwareAccelerated ? $"FFmpeg H.264 Decoder ({_hwType})" : "FFmpeg H.264 Decoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.H264 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_H264;
    }

    /// <summary>
    /// FFmpeg H.265 解码器 (支持硬件加速)
    /// </summary>
    public sealed class H265Decoder : FFmpegVideoDecoder
    {
        public override string Name => IsHardwareAccelerated ? $"FFmpeg H.265 Decoder ({_hwType})" : "FFmpeg H.265 Decoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.H265 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_HEVC;
    }

    /// <summary>
    /// FFmpeg VP8 解码器
    /// </summary>
    public sealed class VP8Decoder : FFmpegVideoDecoder
    {
        public override string Name => "FFmpeg VP8 Decoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.VP8 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_VP8;
    }

    /// <summary>
    /// FFmpeg VP9 解码器 (支持硬件加速)
    /// </summary>
    public sealed class VP9Decoder : FFmpegVideoDecoder
    {
        public override string Name => IsHardwareAccelerated ? $"FFmpeg VP9 Decoder ({_hwType})" : "FFmpeg VP9 Decoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.VP9 };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_VP9;
    }

    /// <summary>
    /// FFmpeg MJPEG 解码器
    /// </summary>
    public sealed class MJPEGDecoder : FFmpegVideoDecoder
    {
        public override string Name => "FFmpeg MJPEG Decoder";
        public override VideoCodec[] SupportedCodecs => new[] { VideoCodec.MJPEG };
        protected override AVCodecID GetCodecId(VideoCodec codec) => AVCodecID.AV_CODEC_ID_MJPEG;
    }
}
