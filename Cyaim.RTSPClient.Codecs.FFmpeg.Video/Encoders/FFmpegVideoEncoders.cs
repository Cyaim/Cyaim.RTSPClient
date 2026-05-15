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

            // 配置硬件加速
            if (_hwType != HardwareAccelerationType.None)
            {
                var hwPixFmt = FFmpegHardwareHelper.GetHardwarePixelFormat(_hwType);
                _codecCtx->pix_fmt = hwPixFmt;

                // 创建硬件设备上下文
                var error = FFmpegHardwareHelper.ConfigureHardwareContext(_codecCtx, codec, _hwType);
                if (error < 0)
                {
                    // 硬件配置失败，回退到软件
                    _hwType = HardwareAccelerationType.None;
                    _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                }
            }
            else
            {
                _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            }

            // 设置预设
            if (!string.IsNullOrEmpty(config.Preset))
                ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", config.Preset, 0);

            if (!string.IsNullOrEmpty(config.Profile))
                ffmpeg.av_opt_set(_codecCtx->priv_data, "profile", config.Profile, 0);

            // 打开编码器
            var openError = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            FFmpegHelper.CheckError(openError);

            // 分配帧
            _frame = ffmpeg.av_frame_alloc();
            _frame->width = config.Width;
            _frame->height = config.Height;
            _frame->format = (int)(_hwType != HardwareAccelerationType.None
                ? FFmpegHardwareHelper.GetHardwarePixelFormat(_hwType)
                : AVPixelFormat.AV_PIX_FMT_YUV420P);
            ffmpeg.av_frame_get_buffer(_frame, 0);

            // 硬件编码需要额外的硬件帧
            if (_hwType != HardwareAccelerationType.None)
            {
                _hwFrame = ffmpeg.av_frame_alloc();
            }

            _packet = ffmpeg.av_packet_alloc();
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public virtual Task<EncodedVideoFrame> EncodeAsync(VideoFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready)
                throw new InvalidOperationException("Encoder not initialized");

            _state = ProcessorState.Processing;

            try
            {
                // 填充软件帧
                FillSoftwareFrame(input);

                AVFrame* encodeFrame = _frame;

                // 如果使用硬件加速，需要上传到 GPU
                if (_hwType != HardwareAccelerationType.None)
                {
                    var uploadError = ffmpeg.av_hwframe_transfer_data(_hwFrame, _frame, 0);
                    if (uploadError < 0)
                        FFmpegHelper.CheckError(uploadError);
                    _hwFrame->pts = _frameNumber;
                    encodeFrame = _hwFrame;
                }
                else
                {
                    _frame->pts = _frameNumber;
                }

                _frameNumber++;

                var error = ffmpeg.avcodec_send_frame(_codecCtx, encodeFrame);
                FFmpegHelper.CheckError(error);

                error = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    return Task.FromResult<EncodedVideoFrame>(default);
                FFmpegHelper.CheckError(error);

                var data = new byte[_packet->size];
                Marshal.Copy((IntPtr)_packet->data, data, 0, _packet->size);
                var isKeyFrame = (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                return Task.FromResult(new EncodedVideoFrame
                {
                    Data = data,
                    Codec = SupportedCodecs[0],
                    Timestamp = input.Timestamp,
                    Type = isKeyFrame ? FrameType.IDR : FrameType.P,
                    Width = input.Width,
                    Height = input.Height
                });
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
                _state = ProcessorState.Ready;
            }
        }

        public async IAsyncEnumerable<EncodedVideoFrame> EncodeStreamAsync(
            IAsyncEnumerable<VideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                var encoded = await EncodeAsync(frame, ct);
                if (encoded.Data.Length > 0)
                    yield return encoded;
            }
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            if (_codecCtx != null)
            {
                ffmpeg.avcodec_send_frame(_codecCtx, null);
                while (ffmpeg.avcodec_receive_packet(_codecCtx, _packet) >= 0)
                    ffmpeg.av_packet_unref(_packet);
            }
            return Task.CompletedTask;
        }

        protected virtual void FillSoftwareFrame(VideoFrame input)
        {
            var data = input.Data.Span;
            int w = input.Width, h = input.Height;
            int ySize = w * h, uvSize = (w / 2) * (h / 2);

            // Y
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Slice(y * w, w).ToArray(), 0, (IntPtr)(_frame->data[0] + y * _frame->linesize[0]), w);

            // U
            for (int y = 0; y < h / 2; y++)
                Marshal.Copy(data.Slice(ySize + y * (w / 2), w / 2).ToArray(), 0, (IntPtr)(_frame->data[1] + y * _frame->linesize[1]), w / 2);

            // V
            for (int y = 0; y < h / 2; y++)
                Marshal.Copy(data.Slice(ySize + uvSize + y * (w / 2), w / 2).ToArray(), 0, (IntPtr)(_frame->data[2] + y * _frame->linesize[2]), w / 2);
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
