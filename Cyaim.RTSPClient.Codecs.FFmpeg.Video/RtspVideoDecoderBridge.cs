using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Rtp;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Video
{
    /// <summary>
    /// RTSP 拉流 → FFmpeg 解码的桥接器。
    ///
    /// RTSP 解包器输出的 <see cref="MediaFrame"/> 是不带起始码的单个 NAL，
    /// 而 FFmpeg 解码器要求按完整访问单元（AU，一帧的全部 NAL 拼 Annex-B）投喂。
    /// 本桥接器按 <see cref="MediaFrame.IsAccessUnitEnd"/>（RTP marker）与时间戳变化
    /// 聚合 NAL 为 AU 后解码——把 <c>session.GetMediaFrameReader()</c> 的输出
    /// 直接变成可用的视频帧序列。
    ///
    /// 用法：
    /// <code>
    /// await using var session = new RTSPSession(config);
    /// await session.StartAsync();
    /// using var bridge = new RtspVideoDecoderBridge(VideoCodec.H264);
    ///
    /// var nals = session.GetMediaFrameReader(0);
    /// while (await nals.WaitToReadAsync())
    ///     while (nals.TryRead(out var nal))
    ///         foreach (var frame in await bridge.FeedAsync(nal))
    ///             Render(frame);   // YUV420P/NV12 帧
    /// </code>
    /// </summary>
    public sealed class RtspVideoDecoderBridge : IDisposable
    {
        private static readonly byte[] StartCode = { 0, 0, 0, 1 };

        private readonly Decoders.FFmpegVideoDecoder _decoder;
        private readonly VideoCodec _codec;
        private readonly MemoryStream _auBuffer = new();
        private uint _currentTimestamp;
        private bool _hasBufferedNal;
        private bool _initialized;
        private readonly bool _enableHardwareAcceleration;

        /// <param name="codec">视频编码（H264/H265）</param>
        /// <param name="enableHardwareAcceleration">是否启用硬件加速解码（自动探测最佳类型）</param>
        public RtspVideoDecoderBridge(VideoCodec codec, bool enableHardwareAcceleration = true)
        {
            _codec = codec;
            _enableHardwareAcceleration = enableHardwareAcceleration;
            _decoder = codec switch
            {
                VideoCodec.H264 => new Decoders.H264Decoder(),
                VideoCodec.H265 => new Decoders.H265Decoder(),
                _ => throw new NotSupportedException($"RtspVideoDecoderBridge supports H264/H265, got {codec}")
            };
        }

        /// <summary>
        /// 底层解码器（读取 Name/IsHardwareAccelerated/DecodeErrorCount 等）
        /// </summary>
        public Decoders.FFmpegVideoDecoder Decoder => _decoder;

        /// <summary>
        /// 投喂一个 RTSP 解包 NAL；AU 完成时返回解出的帧（0..N 个）。
        /// </summary>
        public async Task<IReadOnlyList<VideoFrame>> FeedAsync(MediaFrame nal, CancellationToken ct = default)
        {
            if (!_initialized)
            {
                await _decoder.InitializeAsync(new VideoDecoderConfig
                {
                    Codec = _codec,
                    EnableHardwareAcceleration = _enableHardwareAcceleration
                }, ct).ConfigureAwait(false);
                _initialized = true;
            }

            var output = new List<VideoFrame>();

            // 时间戳变化 = 新 AU 开始：先解码已缓冲的上一个 AU
            if (_hasBufferedNal && nal.Timestamp != _currentTimestamp)
            {
                await DecodeBufferedAuAsync(output, ct).ConfigureAwait(false);
            }

            // 追加当前 NAL（补起始码）
            _auBuffer.Write(StartCode, 0, StartCode.Length);
            _auBuffer.Write(nal.Data, 0, nal.Data.Length);
            _currentTimestamp = nal.Timestamp;
            _hasBufferedNal = true;

            // marker = AU 结束：立即解码
            if (nal.IsAccessUnitEnd)
            {
                await DecodeBufferedAuAsync(output, ct).ConfigureAwait(false);
            }

            return output;
        }

        /// <summary>
        /// 流结束时调用：解码缓冲中的最后一个 AU 并排空解码器尾帧
        /// </summary>
        public async Task<IReadOnlyList<VideoFrame>> FlushAsync(CancellationToken ct = default)
        {
            var output = new List<VideoFrame>();
            if (_hasBufferedNal)
            {
                await DecodeBufferedAuAsync(output, ct).ConfigureAwait(false);
            }

            if (_initialized)
            {
                await _decoder.FlushAsync(ct).ConfigureAwait(false);
                while (_decoder.DequeuePendingFrame() is { } frame)
                    output.Add(frame);
            }

            return output;
        }

        private async Task DecodeBufferedAuAsync(List<VideoFrame> output, CancellationToken ct)
        {
            if (_auBuffer.Length == 0)
            {
                _hasBufferedNal = false;
                return;
            }

            var au = new EncodedVideoFrame
            {
                Data = _auBuffer.ToArray(),
                Codec = _codec,
                Timestamp = _currentTimestamp
            };
            _auBuffer.SetLength(0);
            _hasBufferedNal = false;

            var frame = await _decoder.DecodeAsync(au, ct).ConfigureAwait(false);
            if (frame != null)
                output.Add(frame);
            while (_decoder.DequeuePendingFrame() is { } extra)
                output.Add(extra);
        }

        public void Dispose()
        {
            _decoder.Dispose();
            _auBuffer.Dispose();
        }
    }
}
