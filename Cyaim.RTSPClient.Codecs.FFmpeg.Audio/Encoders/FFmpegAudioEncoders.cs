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

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Encoders
{
    /// <summary>
    /// FFmpeg 音频编码器基类
    /// </summary>
    public unsafe abstract class FFmpegAudioEncoder : IAudioEncoder
    {
        protected AVCodecContext* _codecCtx;
        protected AVFrame* _frame;
        protected AVPacket* _packet;
        protected bool _disposed;
        protected ProcessorState _state = ProcessorState.Idle;
        protected long _sampleIndex;

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
            _codecCtx->sample_rate = config.SampleRate;
            _codecCtx->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
            _codecCtx->ch_layout.nb_channels = config.Channels;
            _codecCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            _codecCtx->bit_rate = config.Bitrate > 0 ? config.Bitrate : 128000;

            FFmpegHelper.CheckError(ffmpeg.avcodec_open2(_codecCtx, codec, null));

            _frame = ffmpeg.av_frame_alloc();
            _frame->nb_samples = _codecCtx->frame_size > 0 ? _codecCtx->frame_size : 1024;
            _frame->format = (int)_codecCtx->sample_fmt;
            _frame->ch_layout = _codecCtx->ch_layout;
            ffmpeg.av_frame_get_buffer(_frame, 0);

            _packet = ffmpeg.av_packet_alloc();
            _state = ProcessorState.Ready;
            return Task.CompletedTask;
        }

        public virtual Task<EncodedAudioFrame> EncodeAsync(AudioFrame input, CancellationToken ct = default)
        {
            if (_state != ProcessorState.Ready) throw new InvalidOperationException("Not initialized");
            _state = ProcessorState.Processing;
            try
            {
                int spc = input.Data.Length / (input.Channels * 2);
                _frame->nb_samples = spc;
                _frame->pts = _sampleIndex;
                _sampleIndex += spc;

                fixed (byte* pData = input.Data.Span)
                    ffmpeg.avcodec_fill_audio_frame(_frame, input.Channels, AVSampleFormat.AV_SAMPLE_FMT_S16, pData, input.Data.Length, 0);

                FFmpegHelper.CheckError(ffmpeg.avcodec_send_frame(_codecCtx, _frame));
                var err = ffmpeg.avcodec_receive_packet(_codecCtx, _packet);
                if (err == ffmpeg.AVERROR(ffmpeg.EAGAIN)) return Task.FromResult<EncodedAudioFrame>(default);
                FFmpegHelper.CheckError(err);

                var encoded = new byte[_packet->size];
                Marshal.Copy((IntPtr)_packet->data, encoded, 0, _packet->size);
                return Task.FromResult(new EncodedAudioFrame { Data = encoded, Codec = SupportedCodecs[0], SampleRate = input.SampleRate, Channels = input.Channels, Timestamp = input.Timestamp, Duration = spc * 1000000L / input.SampleRate });
            }
            finally { ffmpeg.av_packet_unref(_packet); _state = ProcessorState.Ready; }
        }

        public async IAsyncEnumerable<EncodedAudioFrame> EncodeStreamAsync(IAsyncEnumerable<AudioFrame> input, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in input.WithCancellation(ct))
            {
                var encoded = await EncodeAsync(frame, ct);
                if (encoded.Data.Length > 0) yield return encoded;
            }
        }

        public virtual Task FlushAsync(CancellationToken ct = default)
        {
            if (_codecCtx != null)
            {
                ffmpeg.avcodec_send_frame(_codecCtx, null);
                while (ffmpeg.avcodec_receive_packet(_codecCtx, _packet) >= 0) ffmpeg.av_packet_unref(_packet);
            }
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
