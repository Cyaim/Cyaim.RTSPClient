using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Decoders
{
    /// <summary>
    /// FFmpeg 音频解码器基类
    ///
    /// 正确性说明（相对旧实现）：
    /// - 解码器的输出格式由编解码器决定（AAC 为 FLTP planar float），
    ///   旧实现按 S16 交织直接拷贝 data[0] 输出的是噪音——现经 libswresample
    ///   统一转换为 16-bit 交织 PCM
    /// - 支持 <see cref="AudioDecoderConfig.ExtraData"/>：RTSP 裸 AAC（RFC 3640）
    ///   必须提供 AudioSpecificConfig 才能初始化解码器
    /// - 一个 packet 可能产出多帧：内部输出队列承接，无帧返回 null
    /// </summary>
    public unsafe abstract class FFmpegAudioDecoder : IAudioDecoder
    {
        protected AVCodecContext* _codecCtx;
        protected AVFrame* _frame;
        protected AVPacket* _packet;
        protected SwrContext* _swrCtx;
        protected bool _disposed;
        protected ProcessorState _state = ProcessorState.Idle;

        private readonly Queue<AudioFrame> _pendingFrames = new();
        private int _swrInFormat = -1;
        private int _swrInRate;
        private int _swrInChannels;

        public abstract string Name { get; }
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _state;
        public abstract AudioCodec[] SupportedCodecs { get; }

        protected FFmpegAudioDecoder() => FFmpegHelper.Initialize();

        public virtual Task InitializeAsync(AudioDecoderConfig config, CancellationToken ct = default)
        {
            var codec = ffmpeg.avcodec_find_decoder(GetCodecId(config.Codec));
            if (codec == null) throw new InvalidOperationException($"Codec not found: {config.Codec}");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null) throw new InvalidOperationException("Failed to allocate codec context");

            _codecCtx->sample_rate = config.SampleRate;
            ffmpeg.av_channel_layout_default(&_codecCtx->ch_layout, Math.Max(1, config.Channels));

            // 带外参数：RTSP 裸 AAC 必须提供 AudioSpecificConfig（SDP fmtp config=）
            if (config.ExtraData is { Length: > 0 })
            {
                _codecCtx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(config.ExtraData.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
                fixed (byte* pSrc = config.ExtraData)
                {
                    Buffer.MemoryCopy(pSrc, _codecCtx->extradata, config.ExtraData.Length, config.ExtraData.Length);
                }
                _codecCtx->extradata_size = config.ExtraData.Length;
            }

            FFmpegHelper.CheckError(ffmpeg.avcodec_open2(_codecCtx, codec, null));
            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public virtual Task<AudioFrame?> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready) throw new InvalidOperationException("Not initialized");
            _state = ProcessorState.Processing;
            try
            {
                SendPacket(input);
                DrainDecodedFrames(input.Timestamp);
                return Task.FromResult(_pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : (AudioFrame?)null);
            }
            finally
            {
                _state = ProcessorState.Ready;
            }
        }

        private void SendPacket(EncodedAudioFrame input)
        {
            fixed (byte* pData = input.Data.Span)
            {
                _packet->data = pData;
                _packet->size = input.Data.Length;
                _packet->pts = input.Timestamp;

                var error = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    DrainDecodedFrames(input.Timestamp);
                    error = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                }
                FFmpegHelper.CheckError(error);
            }

            _packet->data = null;
            _packet->size = 0;
        }

        private void DrainDecodedFrames(long fallbackTimestamp)
        {
            while (true)
            {
                var err = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                if (err == ffmpeg.AVERROR(ffmpeg.EAGAIN) || err == ffmpeg.AVERROR_EOF)
                    break;
                FFmpegHelper.CheckError(err);

                try
                {
                    _pendingFrames.Enqueue(ConvertToS16Interleaved(_frame, fallbackTimestamp));
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }
            }
        }

        /// <summary>
        /// 任意解码输出格式（FLTP/S16P/…）→ 16-bit 交织 PCM
        /// </summary>
        private AudioFrame ConvertToS16Interleaved(AVFrame* frame, long fallbackTimestamp)
        {
            int channels = frame->ch_layout.nb_channels;
            int sampleRate = frame->sample_rate;
            int nbSamples = frame->nb_samples;

            EnsureSwrContext(frame);

            var pcm = new byte[nbSamples * channels * 2];
            fixed (byte* pOut = pcm)
            {
                byte* outPlane = pOut;
                int converted = ffmpeg.swr_convert(_swrCtx, &outPlane, nbSamples,
                    frame->extended_data, nbSamples);
                FFmpegHelper.CheckError(converted);

                if (converted != nbSamples)
                {
                    // 无重采样场景下应等量输出；防御性截断
                    var trimmed = new byte[converted * channels * 2];
                    Array.Copy(pcm, trimmed, trimmed.Length);
                    pcm = trimmed;
                }
            }

            long timestamp = frame->pts != ffmpeg.AV_NOPTS_VALUE ? frame->pts : fallbackTimestamp;

            return new AudioFrame
            {
                Data = pcm,
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = 16,
                Timestamp = timestamp
            };
        }

        private void EnsureSwrContext(AVFrame* frame)
        {
            if (_swrCtx != null &&
                _swrInFormat == frame->format &&
                _swrInRate == frame->sample_rate &&
                _swrInChannels == frame->ch_layout.nb_channels)
            {
                return;
            }

            if (_swrCtx != null)
            {
                fixed (SwrContext** p = &_swrCtx)
                    ffmpeg.swr_free(p);
            }

            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, frame->ch_layout.nb_channels);

            SwrContext* swr = null;
            var error = ffmpeg.swr_alloc_set_opts2(&swr,
                &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, frame->sample_rate,
                &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate,
                0, null);
            FFmpegHelper.CheckError(error);
            FFmpegHelper.CheckError(ffmpeg.swr_init(swr));

            _swrCtx = swr;
            _swrInFormat = frame->format;
            _swrInRate = frame->sample_rate;
            _swrInChannels = frame->ch_layout.nb_channels;
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct))
            {
                SendPacket(frame);
                DrainDecodedFrames(frame.Timestamp);
                while (_pendingFrames.Count > 0)
                    yield return _pendingFrames.Dequeue();
            }
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            if (_codecCtx != null) ffmpeg.avcodec_flush_buffers(_codecCtx);
            _pendingFrames.Clear();
            return Task.CompletedTask;
        }

        protected abstract AVCodecID GetCodecId(AudioCodec codec);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pendingFrames.Clear();
            if (_swrCtx != null) { fixed (SwrContext** p = &_swrCtx) ffmpeg.swr_free(p); }
            if (_packet != null) { fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p); }
            if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); }
            if (_codecCtx != null) { fixed (AVCodecContext** p = &_codecCtx) ffmpeg.avcodec_free_context(p); }
            _state = ProcessorState.Disposed;
        }
    }

    public sealed class AACDecoder : FFmpegAudioDecoder
    {
        public override string Name => "FFmpeg AAC Decoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.AAC };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_AAC;
    }

    public sealed class OpusDecoder : FFmpegAudioDecoder
    {
        public override string Name => "FFmpeg Opus Decoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_OPUS;
    }

    public sealed class AMRDecoder : FFmpegAudioDecoder
    {
        public override string Name => "FFmpeg AMR-NB Decoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.AMR };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_AMR_NB;
    }

    public sealed class AMRWBDecoder : FFmpegAudioDecoder
    {
        public override string Name => "FFmpeg AMR-WB Decoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.AMR_WB };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_AMR_WB;
    }

    public sealed class SpeexDecoder : FFmpegAudioDecoder
    {
        public override string Name => "FFmpeg Speex Decoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.SPEEX };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_SPEEX;
    }

    public sealed class VorbisDecoder : FFmpegAudioDecoder
    {
        public override string Name => "FFmpeg Vorbis Decoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.VORBIS };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_VORBIS;
    }

    public sealed class MP3Decoder : FFmpegAudioDecoder
    {
        public override string Name => "FFmpeg MP3 Decoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.MPA };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_MP3;
    }
}
