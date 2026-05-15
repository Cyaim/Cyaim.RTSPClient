using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.RTSPClient.Media.Codecs
{
    /// <summary>
    /// 软件视频解码器工厂
    /// </summary>
    internal sealed class SoftwareVideoDecoderFactory : IVideoDecoderFactory
    {
        public string Name => "Software";
        public int Priority => 0; // 最低优先级，作为后备
        public bool IsHardwareAccelerated => false;
        public VideoCodec[] SupportedCodecs => new[] { VideoCodec.H264, VideoCodec.H265, VideoCodec.MJPEG };

        public bool CanCreate(VideoCodec codec, bool preferHardware = true)
        {
            return Array.Exists(SupportedCodecs, c => c == codec);
        }

        public bool SupportsDevice(string deviceName) => false;

        public IVideoDecoder Create(VideoCodec codec)
        {
            return codec switch
            {
                VideoCodec.H264 => new SoftwareH264Decoder(),
                VideoCodec.H265 => new SoftwareH265Decoder(),
                VideoCodec.MJPEG => new SoftwareMjpegDecoder(),
                _ => throw new NotSupportedException($"Software decoder not available for {codec}")
            };
        }
    }

    /// <summary>
    /// 软件 H.264 解码器
    /// </summary>
    internal sealed class SoftwareH264Decoder : IVideoDecoder
    {
        public string Name => "Software H.264 Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public VideoCodec[] SupportedCodecs => new[] { VideoCodec.H264 };
        public VideoDecoderCapabilities Capabilities => new()
        {
            MaxWidth = 4096,
            MaxHeight = 4096,
            SupportsHardwareAcceleration = false,
            SupportedOutputFormats = new[] { PixelFormat.YUV420P, PixelFormat.NV12 }
        };

        public Task InitializeAsync(VideoDecoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public async Task<VideoFrame> DecodeAsync(EncodedVideoFrame input, CancellationToken ct = default)
        {
            if (State != ProcessorState.Ready)
                throw new InvalidOperationException("Decoder not initialized");

            State = ProcessorState.Processing;

            try
            {
                // TODO: 实现 H.264 软件解码
                // 这里应该调用 FFmpeg 或其他解码库
                await Task.Yield();

                return new VideoFrame
                {
                    Width = input.Width,
                    Height = input.Height,
                    Format = PixelFormat.YUV420P,
                    Timestamp = input.Timestamp,
                    Type = input.Type
                };
            }
            finally
            {
                State = ProcessorState.Ready;
            }
        }

        public async IAsyncEnumerable<VideoFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedVideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await DecodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            State = ProcessorState.Disposed;
        }
    }

    /// <summary>
    /// 软件 H.265 解码器
    /// </summary>
    internal sealed class SoftwareH265Decoder : IVideoDecoder
    {
        public string Name => "Software H.265 Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public VideoCodec[] SupportedCodecs => new[] { VideoCodec.H265 };
        public VideoDecoderCapabilities Capabilities => new()
        {
            MaxWidth = 8192,
            MaxHeight = 8192,
            SupportsHardwareAcceleration = false,
            SupportedOutputFormats = new[] { PixelFormat.YUV420P, PixelFormat.NV12, PixelFormat.P010 }
        };

        public Task InitializeAsync(VideoDecoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public async Task<VideoFrame> DecodeAsync(EncodedVideoFrame input, CancellationToken ct = default)
        {
            if (State != ProcessorState.Ready)
                throw new InvalidOperationException("Decoder not initialized");

            State = ProcessorState.Processing;
            try
            {
                // TODO: 实现 H.265 软件解码
                await Task.Yield();
                return new VideoFrame
                {
                    Width = input.Width,
                    Height = input.Height,
                    Format = PixelFormat.YUV420P,
                    Timestamp = input.Timestamp,
                    Type = input.Type
                };
            }
            finally
            {
                State = ProcessorState.Ready;
            }
        }

        public async IAsyncEnumerable<VideoFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedVideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await DecodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    /// <summary>
    /// 软件 MJPEG 解码器
    /// </summary>
    internal sealed class SoftwareMjpegDecoder : IVideoDecoder
    {
        public string Name => "Software MJPEG Decoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public VideoCodec[] SupportedCodecs => new[] { VideoCodec.MJPEG };
        public VideoDecoderCapabilities Capabilities => new()
        {
            MaxWidth = 4096,
            MaxHeight = 4096,
            SupportsHardwareAcceleration = false,
            SupportedOutputFormats = new[] { PixelFormat.YUV420P, PixelFormat.RGB24 }
        };

        public Task InitializeAsync(VideoDecoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public async Task<VideoFrame> DecodeAsync(EncodedVideoFrame input, CancellationToken ct = default)
        {
            if (State != ProcessorState.Ready)
                throw new InvalidOperationException("Decoder not initialized");

            State = ProcessorState.Processing;
            try
            {
                // TODO: 实现 MJPEG 软件解码
                await Task.Yield();
                return new VideoFrame
                {
                    Width = input.Width,
                    Height = input.Height,
                    Format = PixelFormat.RGB24,
                    Timestamp = input.Timestamp
                };
            }
            finally
            {
                State = ProcessorState.Ready;
            }
        }

        public async IAsyncEnumerable<VideoFrame> DecodeStreamAsync(
            IAsyncEnumerable<EncodedVideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await DecodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    /// <summary>
    /// 软件视频编码器工厂
    /// </summary>
    internal sealed class SoftwareVideoEncoderFactory : IVideoEncoderFactory
    {
        public string Name => "Software";
        public int Priority => 0;
        public bool IsHardwareAccelerated => false;
        public VideoCodec[] SupportedCodecs => new[] { VideoCodec.H264, VideoCodec.H265 };

        public bool CanCreate(VideoCodec codec, bool preferHardware = true)
        {
            return Array.Exists(SupportedCodecs, c => c == codec);
        }

        public bool SupportsDevice(string deviceName) => false;

        public IVideoEncoder Create(VideoCodec codec)
        {
            return codec switch
            {
                VideoCodec.H264 => new SoftwareH264Encoder(),
                VideoCodec.H265 => new SoftwareH265Encoder(),
                _ => throw new NotSupportedException($"Software encoder not available for {codec}")
            };
        }
    }

    internal sealed class SoftwareH264Encoder : IVideoEncoder
    {
        public string Name => "Software H.264 Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public VideoCodec[] SupportedCodecs => new[] { VideoCodec.H264 };
        public VideoEncoderCapabilities Capabilities => new()
        {
            MaxWidth = 4096,
            MaxHeight = 4096,
            SupportsHardwareAcceleration = false,
            SupportedPresets = new[] { "ultrafast", "fast", "medium", "slow" }
        };

        public Task InitializeAsync(VideoEncoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public async Task<EncodedVideoFrame> EncodeAsync(VideoFrame input, CancellationToken ct = default)
        {
            State = ProcessorState.Processing;
            try
            {
                // TODO: 实现 H.264 软件编码
                await Task.Yield();
                return new EncodedVideoFrame
                {
                    Width = input.Width,
                    Height = input.Height,
                    Codec = VideoCodec.H264,
                    Timestamp = input.Timestamp,
                    Type = input.Type
                };
            }
            finally
            {
                State = ProcessorState.Ready;
            }
        }

        public async IAsyncEnumerable<EncodedVideoFrame> EncodeStreamAsync(
            IAsyncEnumerable<VideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await EncodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }

    internal sealed class SoftwareH265Encoder : IVideoEncoder
    {
        public string Name => "Software H.265 Encoder";
        public bool IsHardwareAccelerated => false;
        public ProcessorState State { get; private set; } = ProcessorState.Idle;
        public VideoCodec[] SupportedCodecs => new[] { VideoCodec.H265 };
        public VideoEncoderCapabilities Capabilities => new()
        {
            MaxWidth = 8192,
            MaxHeight = 8192,
            SupportsHardwareAcceleration = false,
            SupportedPresets = new[] { "ultrafast", "fast", "medium", "slow" }
        };

        public Task InitializeAsync(VideoEncoderConfig config, CancellationToken ct = default)
        {
            State = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public async Task<EncodedVideoFrame> EncodeAsync(VideoFrame input, CancellationToken ct = default)
        {
            State = ProcessorState.Processing;
            try
            {
                await Task.Yield();
                return new EncodedVideoFrame
                {
                    Width = input.Width,
                    Height = input.Height,
                    Codec = VideoCodec.H265,
                    Timestamp = input.Timestamp,
                    Type = input.Type
                };
            }
            finally
            {
                State = ProcessorState.Ready;
            }
        }

        public async IAsyncEnumerable<EncodedVideoFrame> EncodeStreamAsync(
            IAsyncEnumerable<VideoFrame> inputStream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in inputStream.WithCancellation(ct))
            {
                yield return await EncodeAsync(frame, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => State = ProcessorState.Disposed;
    }
}
