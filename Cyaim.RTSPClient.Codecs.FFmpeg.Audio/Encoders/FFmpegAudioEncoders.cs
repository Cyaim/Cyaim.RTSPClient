using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Encoders
{
    /// <summary>
    /// FFmpeg 音频编码器基类
    ///
    /// 正确性说明（相对旧实现）：
    /// - 采样格式按编码器实际支持协商（AAC 只接受 FLTP，旧实现硬设 S16
    ///   导致 avcodec_open2 直接失败），输入 S16 交织经 libswresample 转换
    /// - 编码器要求固定 frame_size：输入任意长度经 AVAudioFifo 分帧缓冲，
    ///   不足一帧时返回 null（后续输入或 Flush 补齐）
    /// - 修复 avcodec_fill_audio_frame 悬垂指针（fixed 作用域外使用输入指针）
    /// </summary>
    public unsafe abstract class FFmpegAudioEncoder : IAudioEncoder
    {
        protected AVCodecContext* _codecCtx;
        protected AVFrame* _frame;
        protected AVPacket* _packet;
        protected SwrContext* _swrCtx;
        protected AVAudioFifo* _fifo;
        protected bool _disposed;
        protected ProcessorState _state = ProcessorState.Idle;
        protected long _sampleIndex;

        private readonly Queue<EncodedAudioFrame> _pendingPackets = new();
        private AVSampleFormat _encoderFormat;
        private int _sampleRate;
        private int _channels;
        private long _baseTimestamp = long.MinValue;

        public abstract string Name { get; }
        public bool IsHardwareAccelerated => false;
        public ProcessorState State => _state;
        public abstract AudioCodec[] SupportedCodecs { get; }

        protected FFmpegAudioEncoder() => FFmpegHelper.Initialize();

        public virtual Task InitializeAsync(AudioEncoderConfig config, CancellationToken ct = default)
        {
            var codec = ffmpeg.avcodec_find_encoder(GetCodecId(config.Codec));
            if (codec == null) throw new InvalidOperationException($"Encoder not found: {config.Codec}");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null) throw new InvalidOperationException("Failed to allocate encoder context");

            _sampleRate = config.SampleRate > 0 ? config.SampleRate : 48000;
            _channels = Math.Max(1, config.Channels);

            _codecCtx->sample_rate = _sampleRate;
            ffmpeg.av_channel_layout_default(&_codecCtx->ch_layout, _channels);
            _codecCtx->bit_rate = config.Bitrate > 0 ? config.Bitrate : 128000;
            _codecCtx->time_base = new AVRational { num = 1, den = _sampleRate };

            // 采样格式协商：优先 S16（免转换），否则取编码器支持列表首个（AAC=FLTP）
            _encoderFormat = SelectSampleFormat(codec);
            _codecCtx->sample_fmt = _encoderFormat;

            // Vorbis 等原生实验性编码器需要放宽合规级别
            _codecCtx->strict_std_compliance = ffmpeg.FF_COMPLIANCE_EXPERIMENTAL;

            // 参数集置于 extradata（AAC 的 AudioSpecificConfig）：
            // RTP/RTSP 传输为裸载荷，配置经 SDP 带外传递（见 CodecExtraData）
            _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            FFmpegHelper.CheckError(ffmpeg.avcodec_open2(_codecCtx, codec, null));

            // 输入 S16 交织 → 编码器格式
            if (_encoderFormat != AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                AVChannelLayout layout;
                ffmpeg.av_channel_layout_default(&layout, _channels);

                SwrContext* swr = null;
                var error = ffmpeg.swr_alloc_set_opts2(&swr,
                    &layout, _encoderFormat, _sampleRate,
                    &layout, AVSampleFormat.AV_SAMPLE_FMT_S16, _sampleRate,
                    0, null);
                FFmpegHelper.CheckError(error);
                FFmpegHelper.CheckError(ffmpeg.swr_init(swr));
                _swrCtx = swr;
            }

            // 分帧缓冲：编码器要求固定 frame_size 样本（PCM 类编码器 frame_size=0 时用 1024）
            int frameSize = _codecCtx->frame_size > 0 ? _codecCtx->frame_size : 1024;
            _fifo = ffmpeg.av_audio_fifo_alloc(_encoderFormat, _channels, frameSize * 4);
            if (_fifo == null) throw new InvalidOperationException("Failed to allocate audio FIFO");

            _frame = ffmpeg.av_frame_alloc();
            _frame->nb_samples = frameSize;
            _frame->format = (int)_encoderFormat;
            ffmpeg.av_channel_layout_default(&_frame->ch_layout, _channels);
            _frame->sample_rate = _sampleRate;
            FFmpegHelper.CheckError(ffmpeg.av_frame_get_buffer(_frame, 0));

            _packet = ffmpeg.av_packet_alloc();
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        private static AVSampleFormat SelectSampleFormat(AVCodec* codec)
        {
            // sample_fmts 在 FFmpeg 7.x 标记弃用但仍然填充；
            // 新 API avcodec_get_supported_config 在旧版 FFmpeg 上不存在，用旧字段保证兼容
#pragma warning disable CS0618
            var fmts = codec->sample_fmts;
#pragma warning restore CS0618
            if (fmts == null)
                return AVSampleFormat.AV_SAMPLE_FMT_S16;

            AVSampleFormat first = AVSampleFormat.AV_SAMPLE_FMT_NONE;
            for (var p = fmts; *p != AVSampleFormat.AV_SAMPLE_FMT_NONE; p++)
            {
                if (*p == AVSampleFormat.AV_SAMPLE_FMT_S16)
                    return AVSampleFormat.AV_SAMPLE_FMT_S16;
                if (first == AVSampleFormat.AV_SAMPLE_FMT_NONE)
                    first = *p;
            }

            return first != AVSampleFormat.AV_SAMPLE_FMT_NONE ? first : AVSampleFormat.AV_SAMPLE_FMT_S16;
        }

        public virtual Task<EncodedAudioFrame?> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready) throw new InvalidOperationException("Not initialized");
            _state = ProcessorState.Processing;
            try
            {
                if (_baseTimestamp == long.MinValue)
                    _baseTimestamp = input.Timestamp;

                WriteToFifo(input);
                EncodeBufferedFrames();

                return Task.FromResult(_pendingPackets.Count > 0 ? _pendingPackets.Dequeue() : (EncodedAudioFrame?)null);
            }
            finally
            {
                _state = ProcessorState.Ready;
            }
        }

        /// <summary>
        /// 输入 S16 交织 PCM → （必要时 swr 转换为编码器格式）→ FIFO
        /// </summary>
        private void WriteToFifo(AudioFrame input)
        {
            int samples = input.Data.Length / (_channels * 2);
            if (samples <= 0)
                return;

            fixed (byte* pIn = input.Data.Span)
            {
                if (_swrCtx == null)
                {
                    // 编码器直接吃 S16 交织
                    byte* inPlane = pIn;
                    void* planePtr = inPlane;
                    int written = ffmpeg.av_audio_fifo_write(_fifo, &planePtr, samples);
                    FFmpegHelper.CheckError(written);
                }
                else
                {
                    // 转换到编码器格式（可能为 planar：逐平面缓冲）
                    int bytesPerSample = ffmpeg.av_get_bytes_per_sample(_encoderFormat);
                    bool isPlanar = ffmpeg.av_sample_fmt_is_planar(_encoderFormat) != 0;
                    int planes = isPlanar ? _channels : 1;
                    int planeBytes = samples * bytesPerSample * (isPlanar ? 1 : _channels);

                    // 单块托管缓冲切分为各平面
                    var converted = new byte[planes * planeBytes];
                    fixed (byte* pOut = converted)
                    {
                        var outPlanes = stackalloc byte*[8];
                        var voidPlanes = stackalloc void*[8];
                        for (int i = 0; i < planes; i++)
                        {
                            outPlanes[i] = pOut + (long)i * planeBytes;
                            voidPlanes[i] = outPlanes[i];
                        }

                        byte* inPlane = pIn;
                        int convertedSamples = ffmpeg.swr_convert(_swrCtx, outPlanes, samples, &inPlane, samples);
                        FFmpegHelper.CheckError(convertedSamples);

                        int written = ffmpeg.av_audio_fifo_write(_fifo, voidPlanes, convertedSamples);
                        FFmpegHelper.CheckError(written);
                    }
                }
            }
        }

        /// <summary>
        /// FIFO 中每满一个编码帧就取出编码，产出包进待输出队列
        /// </summary>
        private void EncodeBufferedFrames()
        {
            int frameSize = _frame->nb_samples;
            var dataPlanes = stackalloc void*[8];

            while (ffmpeg.av_audio_fifo_size(_fifo) >= frameSize)
            {
                FFmpegHelper.CheckError(ffmpeg.av_frame_make_writable(_frame));

                for (uint i = 0; i < 8; i++)
                {
                    dataPlanes[i] = _frame->data[i];
                }

                int read = ffmpeg.av_audio_fifo_read(_fifo, dataPlanes, frameSize);
                FFmpegHelper.CheckError(read);

                _frame->pts = _sampleIndex;
                _sampleIndex += frameSize;

                var error = ffmpeg.avcodec_send_frame(_codecCtx, _frame);
                if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    DrainEncodedPackets();
                    error = ffmpeg.avcodec_send_frame(_codecCtx, _frame);
                }
                FFmpegHelper.CheckError(error);

                DrainEncodedPackets();
            }
        }

        private void DrainEncodedPackets()
        {
            while (true)
            {
                var err = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                if (err == ffmpeg.AVERROR(ffmpeg.EAGAIN) || err == ffmpeg.AVERROR_EOF)
                    break;
                FFmpegHelper.CheckError(err);

                try
                {
                    var encoded = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, encoded, 0, _packet->size);

                    long pts = _packet->pts != ffmpeg.AV_NOPTS_VALUE ? _packet->pts : 0;
                    long duration = _packet->duration > 0
                        ? _packet->duration * 1000000L / _sampleRate
                        : (long)_frame->nb_samples * 1000000L / _sampleRate;

                    _pendingPackets.Enqueue(new EncodedAudioFrame
                    {
                        Data = encoded,
                        Codec = SupportedCodecs[0],
                        SampleRate = _sampleRate,
                        Channels = _channels,
                        // 时间戳 = 首帧输入时间戳 + 已编码样本偏移（微秒）
                        Timestamp = (_baseTimestamp == long.MinValue ? 0 : _baseTimestamp) + pts * 1000000L / _sampleRate,
                        Duration = duration
                    });
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct))
            {
                var encoded = await EncodeAsync(frame, ct);
                if (encoded != null)
                    yield return encoded;
                while (_pendingPackets.Count > 0)
                    yield return _pendingPackets.Dequeue();
            }

            SendEofAndDrain();
            while (_pendingPackets.Count > 0)
                yield return _pendingPackets.Dequeue();
        }

        private void SendEofAndDrain()
        {
            if (_codecCtx == null)
                return;
            ffmpeg.avcodec_send_frame(_codecCtx, null);
            DrainEncodedPackets();
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            SendEofAndDrain();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 取出 Flush 后仍滞留在待输出队列中的包
        /// </summary>
        public EncodedAudioFrame? DequeuePendingPacket()
        {
            return _pendingPackets.Count > 0 ? _pendingPackets.Dequeue() : null;
        }

        /// <summary>
        /// 编码器全局参数（AAC 为 AudioSpecificConfig）。
        /// 用于 SDP fmtp config= 生成，或作为对应解码器的 <see cref="AudioDecoderConfig.ExtraData"/>。
        /// </summary>
        public byte[]? CodecExtraData
        {
            get
            {
                if (_codecCtx == null || _codecCtx->extradata == null || _codecCtx->extradata_size <= 0)
                    return null;
                var data = new byte[_codecCtx->extradata_size];
                Marshal.Copy((IntPtr)_codecCtx->extradata, data, 0, data.Length);
                return data;
            }
        }

        protected abstract AVCodecID GetCodecId(AudioCodec codec);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pendingPackets.Clear();
            if (_fifo != null) { ffmpeg.av_audio_fifo_free(_fifo); _fifo = null; }
            if (_swrCtx != null) { fixed (SwrContext** p = &_swrCtx) ffmpeg.swr_free(p); }
            if (_packet != null) { fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p); }
            if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); }
            if (_codecCtx != null) { fixed (AVCodecContext** p = &_codecCtx) ffmpeg.avcodec_free_context(p); }
            _state = ProcessorState.Disposed;
        }
    }

    public sealed class AACEncoder : FFmpegAudioEncoder
    {
        public override string Name => "FFmpeg AAC Encoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.AAC };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_AAC;
    }

    public sealed class OpusEncoder : FFmpegAudioEncoder
    {
        public override string Name => "FFmpeg Opus Encoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.OPUS };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_OPUS;
    }

    public sealed class AMREncoder : FFmpegAudioEncoder
    {
        public override string Name => "FFmpeg AMR-NB Encoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.AMR };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_AMR_NB;
    }

    public sealed class AMRWBEncoder : FFmpegAudioEncoder
    {
        public override string Name => "FFmpeg AMR-WB Encoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.AMR_WB };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_AMR_WB;
    }

    public sealed class SpeexEncoder : FFmpegAudioEncoder
    {
        public override string Name => "FFmpeg Speex Encoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.SPEEX };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_SPEEX;
    }

    public sealed class VorbisEncoder : FFmpegAudioEncoder
    {
        public override string Name => "FFmpeg Vorbis Encoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.VORBIS };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_VORBIS;
    }

    public sealed class MP3Encoder : FFmpegAudioEncoder
    {
        public override string Name => "FFmpeg MP3 Encoder";
        public override AudioCodec[] SupportedCodecs => new[] { AudioCodec.MPA };
        protected override AVCodecID GetCodecId(AudioCodec codec) => AVCodecID.AV_CODEC_ID_MP3;
    }
}
