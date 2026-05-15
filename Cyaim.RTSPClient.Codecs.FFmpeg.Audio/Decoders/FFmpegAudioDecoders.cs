using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Media.Codecs;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Decoders
{
    /// <summary>
    /// FFmpeg 音频解码器基类
    /// </summary>
    public unsafe abstract class FFmpegAudioDecoder : IAudioDecoder
    {
        protected AVCodecContext* _codecCtx;
        protected AVFrame* _frame;
        protected AVPacket* _packet;
        protected bool _disposed;
        protected ProcessorState _state = ProcessorState.Idle;

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
            _codecCtx->sample_rate = config.SampleRate;
            _codecCtx->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
            _codecCtx->ch_layout.nb_channels = config.Channels;
            _codecCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

            FFmpegHelper.CheckError(ffmpeg.avcodec_open2(_codecCtx, codec, null));
            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public virtual Task<AudioFrame> DecodeAsync(EncodedAudioFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready) throw new InvalidOperationException("Not initialized");
            _state = ProcessorState.Processing;
            try
            {
                fixed (byte* pData = input.Data.Span)
                {
                    _packet->data = pData;
                    _packet->size = input.Data.Length;
                    FFmpegHelper.CheckError(ffmpeg.avcodec_send_packet(_codecCtx, _packet));
                    var err = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                    if (err == ffmpeg.AVERROR(ffmpeg.EAGAIN)) return Task.FromResult<AudioFrame>(default);
                    FFmpegHelper.CheckError(err);

                    int ch = _frame->ch_layout.nb_channels;
                    var pcm = new byte[_frame->nb_samples * ch * 2];
                    Marshal.Copy((IntPtr)_frame->data[0], pcm, 0, pcm.Length);
                    return Task.FromResult(new AudioFrame { Data = pcm, SampleRate = _frame->sample_rate, Channels = ch, BitsPerSample = 16, Timestamp = input.Timestamp });
                }
            }
            finally { _state = ProcessorState.Ready; }
        }

        public async IAsyncEnumerable<AudioFrame> DecodeStreamAsync(IAsyncEnumerable<EncodedAudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct))
            {
                var decoded = await DecodeAsync(frame, ct);
                if (decoded.Data.Length > 0) yield return decoded;
            }
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            if (_codecCtx != null) ffmpeg.avcodec_flush_buffers(_codecCtx);
            return Task.CompletedTask;
        }

        protected abstract AVCodecID GetCodecId(AudioCodec codec);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
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
