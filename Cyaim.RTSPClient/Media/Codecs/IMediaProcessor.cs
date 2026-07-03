using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media
{
    /// <summary>
    /// 媒体处理器接口 - 统一的编解码抽象
    /// </summary>
    public interface IMediaProcessor : IDisposable
    {
        /// <summary>
        /// 处理器名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 是否为硬件加速
        /// </summary>
        bool IsHardwareAccelerated { get; }

        /// <summary>
        /// 处理器状态
        /// </summary>
        ProcessorState State { get; }
    }

    /// <summary>
    /// 处理器状态
    /// </summary>
    public enum ProcessorState
    {
        Idle,
        Initializing,
        Ready,
        Processing,
        Error,
        Disposed
    }

    #region 视频编解码

    /// <summary>
    /// 视频解码器接口
    /// </summary>
    public interface IVideoDecoder : IMediaProcessor
    {
        /// <summary>
        /// 支持的视频编码
        /// </summary>
        VideoCodec[] SupportedCodecs { get; }

        /// <summary>
        /// 初始化解码器
        /// </summary>
        Task InitializeAsync(VideoDecoderConfig config, CancellationToken ct = default);

        /// <summary>
        /// 解码一帧。
        /// 返回 null 表示解码器暂未产出帧（如 B 帧重排导致的输出延迟）——这不是错误；
        /// 后续输入或 <see cref="FlushAsync"/> 会补齐输出。流式消费请用 <see cref="DecodeStreamAsync"/>。
        /// </summary>
        Task<VideoFrame?> DecodeAsync(EncodedVideoFrame input, CancellationToken ct = default);

        /// <summary>
        /// 解码连续流
        /// </summary>
        IAsyncEnumerable<VideoFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedVideoFrame> inputStream,
            CancellationToken ct = default);

        /// <summary>
        /// 刷新解码器（清空缓冲区）
        /// </summary>
        Task FlushAsync(CancellationToken ct = default);

        /// <summary>
        /// 解码器能力
        /// </summary>
        VideoDecoderCapabilities Capabilities { get; }
    }

    /// <summary>
    /// 视频编码器接口
    /// </summary>
    public interface IVideoEncoder : IMediaProcessor
    {
        /// <summary>
        /// 支持的视频编码
        /// </summary>
        VideoCodec[] SupportedCodecs { get; }

        /// <summary>
        /// 初始化编码器
        /// </summary>
        Task InitializeAsync(VideoEncoderConfig config, CancellationToken ct = default);

        /// <summary>
        /// 编码一帧。
        /// 返回 null 表示编码器暂未产出包（编码器内部缓冲）——这不是错误；
        /// 后续输入或 <see cref="FlushAsync"/> 会补齐输出。
        /// </summary>
        Task<EncodedVideoFrame?> EncodeAsync(VideoFrame input, CancellationToken ct = default);

        /// <summary>
        /// 编码连续流
        /// </summary>
        IAsyncEnumerable<EncodedVideoFrame> EncodeStreamAsync(
            IAsyncEnumerable<VideoFrame> inputStream,
            CancellationToken ct = default);

        /// <summary>
        /// 刷新编码器（输出剩余帧）
        /// </summary>
        Task FlushAsync(CancellationToken ct = default);

        /// <summary>
        /// 编码器能力
        /// </summary>
        VideoEncoderCapabilities Capabilities { get; }
    }

    /// <summary>
    /// 视频帧
    /// </summary>
    public sealed class VideoFrame : IDisposable
    {
        /// <summary>
        /// 帧数据 (YUV, NV12, RGB 等)
        /// </summary>
        public Memory<byte> Data { get; init; }

        /// <summary>
        /// 宽度
        /// </summary>
        public int Width { get; init; }

        /// <summary>
        /// 高度
        /// </summary>
        public int Height { get; init; }

        /// <summary>
        /// 像素格式
        /// </summary>
        public PixelFormat Format { get; init; }

        /// <summary>
        /// 时间戳 (微秒)
        /// </summary>
        public long Timestamp { get; init; }

        /// <summary>
        /// 帧序号
        /// </summary>
        public long FrameNumber { get; init; }

        /// <summary>
        /// 帧类型
        /// </summary>
        public FrameType Type { get; init; }

        /// <summary>
        /// GPU 表面句柄 (硬件加速时使用)
        /// </summary>
        public IntPtr SurfaceHandle { get; init; }

        /// <summary>
        /// 是否为 GPU 内存
        /// </summary>
        public bool IsGpuMemory => SurfaceHandle != IntPtr.Zero;

        public void Dispose()
        {
            // 释放 GPU 表面等资源
            if (IsGpuMemory)
            {
                ReleaseGpuSurface(SurfaceHandle);
            }
        }

        private static void ReleaseGpuSurface(IntPtr handle)
        {
            // 由具体实现覆盖
        }
    }

    /// <summary>
    /// 编码后的视频帧
    /// </summary>
    public sealed class EncodedVideoFrame : IDisposable
    {
        /// <summary>
        /// 编码数据
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; init; }

        /// <summary>
        /// 视频编码
        /// </summary>
        public VideoCodec Codec { get; init; }

        /// <summary>
        /// 时间戳 (微秒)
        /// </summary>
        public long Timestamp { get; init; }

        /// <summary>
        /// 帧类型
        /// </summary>
        public FrameType Type { get; init; }

        /// <summary>
        /// 是否为关键帧
        /// </summary>
        public bool IsKeyFrame => Type == FrameType.I || Type == FrameType.IDR;

        /// <summary>
        /// 宽度
        /// </summary>
        public int Width { get; init; }

        /// <summary>
        /// 高度
        /// </summary>
        public int Height { get; init; }

        /// <summary>
        /// NAL 单元列表 (H.264/H.265)
        /// </summary>
        public ReadOnlyMemory<byte>[]? NalUnits { get; init; }

        public void Dispose()
        {
            // 释放资源
        }
    }

    /// <summary>
    /// 视频解码器配置
    /// </summary>
    public sealed record VideoDecoderConfig
    {
        public VideoCodec Codec { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public PixelFormat OutputFormat { get; init; } = PixelFormat.NV12;
        public bool LowLatency { get; init; }
        public bool EnableHardwareAcceleration { get; init; } = true;
        public IntPtr DeviceHandle { get; init; } // GPU 设备句柄
        public string? HardwareDevice { get; init; } // "cuda", "dxva2", "qsv", etc.

        /// <summary>
        /// 解码器带外参数（H.264/H.265 可传 Annex-B 格式的 SPS/PPS/VPS，
        /// 来自 SDP sprop-parameter-sets；码流内自带参数集时可不设）
        /// </summary>
        public byte[]? ExtraData { get; init; }
    }

    /// <summary>
    /// 视频编码器配置
    /// </summary>
    public sealed record VideoEncoderConfig
    {
        public VideoCodec Codec { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public PixelFormat InputFormat { get; init; } = PixelFormat.NV12;
        public int Bitrate { get; init; }
        public int Framerate { get; init; } = 30;
        public int GopSize { get; init; } = 30;
        public int BFrames { get; init; }
        public string? Preset { get; init; } // "ultrafast", "fast", "medium", "slow"
        public string? Profile { get; init; } // "baseline", "main", "high"
        public bool EnableHardwareAcceleration { get; init; } = true;
        public IntPtr DeviceHandle { get; init; }
        public string? HardwareDevice { get; init; }
    }

    /// <summary>
    /// 视频解码器能力
    /// </summary>
    public sealed record VideoDecoderCapabilities
    {
        public int MaxWidth { get; init; }
        public int MaxHeight { get; init; }
        public bool SupportsHardwareAcceleration { get; init; }
        public string? HardwareVendor { get; init; }
        public string? HardwareModel { get; init; }
        public int MaxStreams { get; init; }
        public PixelFormat[] SupportedOutputFormats { get; init; } = Array.Empty<PixelFormat>();
    }

    /// <summary>
    /// 视频编码器能力
    /// </summary>
    public sealed record VideoEncoderCapabilities
    {
        public int MaxWidth { get; init; }
        public int MaxHeight { get; init; }
        public bool SupportsHardwareAcceleration { get; init; }
        public string? HardwareVendor { get; init; }
        public string? HardwareModel { get; init; }
        public int MaxBitrate { get; init; }
        public int MaxFramerate { get; init; }
        public string[] SupportedPresets { get; init; } = Array.Empty<string>();
    }

    #endregion

    #region 音频编解码

    /// <summary>
    /// 音频解码器接口
    /// </summary>
    public interface IAudioDecoder : IMediaProcessor
    {
        /// <summary>
        /// 支持的音频编码
        /// </summary>
        AudioCodec[] SupportedCodecs { get; }

        /// <summary>
        /// 初始化解码器
        /// </summary>
        Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default);

        /// <summary>
        /// 解码一帧。
        /// 返回 null 表示解码器暂未产出帧——不是错误，后续输入会补齐输出。
        /// </summary>
        Task<AudioFrame?> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default);

        /// <summary>
        /// 解码连续流
        /// </summary>
        IAsyncEnumerable<AudioFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedAudioFrame> inputStream,
            CancellationToken ct = default);

        /// <summary>
        /// 刷新解码器
        /// </summary>
        Task FlushAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// 音频编码器接口
    /// </summary>
    public interface IAudioEncoder : IMediaProcessor
    {
        /// <summary>
        /// 支持的音频编码
        /// </summary>
        AudioCodec[] SupportedCodecs { get; }

        /// <summary>
        /// 初始化编码器
        /// </summary>
        Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default);

        /// <summary>
        /// 编码一帧。
        /// 返回 null 表示编码器暂未产出包（如分帧缓冲不足一个编码帧）——不是错误。
        /// </summary>
        Task<EncodedAudioFrame?> EncodeAsync(AudioFrame input, CancellationToken ct = default);

        /// <summary>
        /// 编码连续流
        /// </summary>
        IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(
            IAsyncEnumerable<AudioFrame> inputStream,
            CancellationToken ct = default);

        /// <summary>
        /// 刷新编码器
        /// </summary>
        Task FlushAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// 音频帧 (PCM)
    /// </summary>
    public sealed class AudioFrame
    {
        /// <summary>
        /// PCM 数据
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; init; }

        /// <summary>
        /// 采样率
        /// </summary>
        public int SampleRate { get; init; }

        /// <summary>
        /// 声道数
        /// </summary>
        public int Channels { get; init; }

        /// <summary>
        /// 每样本位数
        /// </summary>
        public int BitsPerSample { get; init; } = 16;

        /// <summary>
        /// 时间戳 (微秒)
        /// </summary>
        public long Timestamp { get; init; }

        /// <summary>
        /// 样本数
        /// </summary>
        public int SampleCount => Data.Length / (Channels * BitsPerSample / 8);

        /// <summary>
        /// 时长 (微秒)
        /// </summary>
        public long Duration => SampleCount * 1000000L / SampleRate;
    }

    /// <summary>
    /// 编码后的音频帧
    /// </summary>
    public sealed class EncodedAudioFrame
    {
        /// <summary>
        /// 编码数据
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; init; }

        /// <summary>
        /// 音频编码
        /// </summary>
        public AudioCodec Codec { get; init; }

        /// <summary>
        /// 采样率
        /// </summary>
        public int SampleRate { get; init; }

        /// <summary>
        /// 声道数
        /// </summary>
        public int Channels { get; init; }

        /// <summary>
        /// 时间戳 (微秒)
        /// </summary>
        public long Timestamp { get; init; }

        /// <summary>
        /// 时长 (微秒)
        /// </summary>
        public long Duration { get; init; }
    }

    /// <summary>
    /// 音频解码器配置
    /// </summary>
    public sealed record AudioDecoderConfig
    {
        public AudioCodec Codec { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int BitsPerSample { get; init; } = 16;
        public bool EnableHardwareAcceleration { get; init; }

        /// <summary>
        /// 解码器带外参数。RTSP 裸 AAC（RFC 3640，无 ADTS 头）必须提供
        /// AudioSpecificConfig（即 SDP fmtp 的 config= 十六进制解码后的字节），
        /// 否则 FFmpeg AAC 解码器无法初始化。
        /// </summary>
        public byte[]? ExtraData { get; init; }
    }

    /// <summary>
    /// 音频编码器配置
    /// </summary>
    public sealed record AudioEncoderConfig
    {
        public AudioCodec Codec { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int BitsPerSample { get; init; } = 16;
        public int Bitrate { get; init; }
        public int Complexity { get; init; } // 0-10
    }

    #endregion

    #region 枚举

    /// <summary>
    /// 像素格式
    /// </summary>
    public enum PixelFormat
    {
        Unknown,
        YUV420P,
        NV12,
        NV21,
        YUV422P,
        YUV444P,
        RGB24,
        RGBA32,
        BGR24,
        BGRA32,
        P010,   // 10-bit YUV420
        P016    // 16-bit YUV420
    }

    /// <summary>
    /// 帧类型
    /// </summary>
    public enum FrameType
    {
        Unknown,
        I,      // Intra
        P,      // Predicted
        B,      // Bi-directional
        IDR,    // Instantaneous Decoder Refresh
        S,      // Switching
        SI,     // Switching Intra
        SP      // Switching Predicted
    }

    #endregion
}
