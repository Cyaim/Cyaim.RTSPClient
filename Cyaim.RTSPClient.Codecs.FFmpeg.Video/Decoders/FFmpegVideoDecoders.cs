using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Media.Codecs;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Video.Decoders
{
    /// <summary>
    /// FFmpeg 视频解码器基类 (支持硬件加速)
    /// </summary>
    public unsafe abstract class FFmpegVideoDecoder : IVideoDecoder
    {
        protected AVCodecContext* _codecCtx;
        protected AVFrame* _frame;
        protected AVFrame* _swFrame;  // 软件帧 (用于硬件加速时的数据传输)
        protected AVPacket* _packet;
        protected AVBufferRef* _hwDeviceCtx;
        protected bool _disposed;
        protected ProcessorState _state = ProcessorState.Idle;
        protected HardwareAccelerationType _hwType = HardwareAccelerationType.None;

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
            SupportedOutputFormats = IsHardwareAccelerated
                ? new[] { PixelFormat.NV12, PixelFormat.YUV420P }
                : new[] { PixelFormat.YUV420P, PixelFormat.NV12, PixelFormat.RGB24 }
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
            _codecCtx->thread_count = Environment.ProcessorCount;

            // 配置硬件加速
            if (config.EnableHardwareAcceleration)
            {
                var hwConfig = new HardwareAccelerationConfig
                {
                    Type = !string.IsNullOrEmpty(config.HardwareDevice)
                        ? ParseHardwareType(config.HardwareDevice)
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

        public virtual Task<VideoFrame> DecodeAsync(EncodedVideoFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready)
                throw new InvalidOperationException("Decoder not initialized");

            _state = ProcessorState.Processing;

            try
            {
                var data = input.Data;
                fixed (byte* pData = data.Span)
                {
                    _packet->data = pData;
                    _packet->size = data.Length;

                    var error = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                    FFmpegHelper.CheckError(error);

                    error = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                    if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        return Task.FromResult<VideoFrame>(default);
                    FFmpegHelper.CheckError(error);

                    // 处理硬件帧
                    AVFrame* outputFrame = _frame;
                    if (_frame->format == (int)FFmpegHardwareHelper.GetHardwarePixelFormat(_hwType))
                    {
                        // 从硬件帧下载到软件帧
                        var transferError = FFmpegHardwareHelper.DownloadFrame(_frame, _swFrame);
                        if (transferError < 0)
                        {
                            FFmpegHelper.CheckError(transferError);
                        }
                        outputFrame = _swFrame;
                    }

                    var videoFrame = ConvertFrame(outputFrame, input.Timestamp);
                    return Task.FromResult(videoFrame);
                }
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
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                var decoded = await DecodeAsync(frame, ct);
                if (decoded.Data.Length > 0)
                    yield return decoded;
            }
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            if (_codecCtx != null)
                ffmpeg.avcodec_flush_buffers(_codecCtx);
            return Task.CompletedTask;
        }

        protected virtual VideoFrame ConvertFrame(AVFrame* frame, long timestamp)
        {
            int width = frame->width;
            int height = frame->height;
            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);
            var data = new byte[ySize + uvSize * 2];

            // Y 平面
            for (int y = 0; y < height; y++)
            {
                var src = frame->data[0] + y * frame->linesize[0];
                Marshal.Copy((IntPtr)src, data, y * width, width);
            }

            // U 平面
            for (int y = 0; y < height / 2; y++)
            {
                var src = frame->data[1] + y * frame->linesize[1];
                Marshal.Copy((IntPtr)src, data, ySize + y * (width / 2), width / 2);
            }

            // V 平面
            for (int y = 0; y < height / 2; y++)
            {
                var src = frame->data[2] + y * frame->linesize[2];
                Marshal.Copy((IntPtr)src, data, ySize + uvSize + y * (width / 2), width / 2);
            }

            return new VideoFrame
            {
                Data = data,
                Width = width,
                Height = height,
                Format = PixelFormat.YUV420P,
                Timestamp = timestamp,
                Type = frame->pict_type == AVPictureType.AV_PICTURE_TYPE_I ? FrameType.I :
                       frame->pict_type == AVPictureType.AV_PICTURE_TYPE_P ? FrameType.P :
                       frame->pict_type == AVPictureType.AV_PICTURE_TYPE_B ? FrameType.B : FrameType.Unknown
            };
        }

        protected abstract AVCodecID GetCodecId(VideoCodec codec);

        private static HardwareAccelerationType ParseHardwareType(string name)
        {
            return name.ToLower() switch
            {
                "cuda" => HardwareAccelerationType.Cuda,
                "nvdec" => HardwareAccelerationType.Cuda,
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

            if (_hwDeviceCtx != null)
            {
                fixed (AVBufferRef** p = &_hwDeviceCtx)
                    ffmpeg.av_buffer_unref(p);
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
