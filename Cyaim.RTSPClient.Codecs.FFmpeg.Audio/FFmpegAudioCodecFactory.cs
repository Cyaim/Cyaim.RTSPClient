using System;
using System.Linq;
using Cyaim.RTSPClient.Media;
using Cyaim.RTSPClient.Media.Codecs;
using Cyaim.RTSPClient.Codecs.FFmpeg.Video;
using Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Decoders;
using Cyaim.RTSPClient.Codecs.FFmpeg.Audio.Encoders;
using FFmpeg.AutoGen;

namespace Cyaim.RTSPClient.Codecs.FFmpeg.Audio
{
    internal static class FFmpegAudioCodecProbe
    {
        public static AVCodecID ToCodecId(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC => AVCodecID.AV_CODEC_ID_AAC,
            AudioCodec.OPUS => AVCodecID.AV_CODEC_ID_OPUS,
            AudioCodec.AMR => AVCodecID.AV_CODEC_ID_AMR_NB,
            AudioCodec.AMR_WB => AVCodecID.AV_CODEC_ID_AMR_WB,
            AudioCodec.SPEEX => AVCodecID.AV_CODEC_ID_SPEEX,
            AudioCodec.VORBIS => AVCodecID.AV_CODEC_ID_VORBIS,
            AudioCodec.MPA => AVCodecID.AV_CODEC_ID_MP3,
            _ => AVCodecID.AV_CODEC_ID_NONE
        };

        /// <summary>
        /// 当前 FFmpeg 构建是否真的带该解码器（如 Speex/AMR 常缺）
        /// </summary>
        public static unsafe bool HasDecoder(AudioCodec codec)
        {
            var id = ToCodecId(codec);
            return id != AVCodecID.AV_CODEC_ID_NONE && ffmpeg.avcodec_find_decoder(id) != null;
        }

        /// <summary>
        /// 当前 FFmpeg 构建是否真的带该编码器（AMR/Speex 编码器需第三方库，多数构建没有）
        /// </summary>
        public static unsafe bool HasEncoder(AudioCodec codec)
        {
            var id = ToCodecId(codec);
            return id != AVCodecID.AV_CODEC_ID_NONE && ffmpeg.avcodec_find_encoder(id) != null;
        }
    }

    /// <summary>
    /// FFmpeg 音频解码器工厂
    /// </summary>
    public class FFmpegAudioDecoderFactory : IAudioDecoderFactory
    {
        public string Name => "FFmpeg Audio";
        public int Priority => 100;
        public bool IsHardwareAccelerated => false;

        public AudioCodec[] SupportedCodecs => new[]
        {
            AudioCodec.AAC, AudioCodec.OPUS, AudioCodec.AMR, AudioCodec.AMR_WB,
            AudioCodec.SPEEX, AudioCodec.VORBIS, AudioCodec.MPA
        };

        public bool CanCreate(AudioCodec codec, bool preferHardware = true)
        {
            return SupportedCodecs.Contains(codec) && FFmpegHelper.IsAvailable()
                && FFmpegAudioCodecProbe.HasDecoder(codec);
        }

        public IAudioDecoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.AAC => new AACDecoder(),
                AudioCodec.OPUS => new OpusDecoder(),
                AudioCodec.AMR => new AMRDecoder(),
                AudioCodec.AMR_WB => new AMRWBDecoder(),
                AudioCodec.SPEEX => new SpeexDecoder(),
                AudioCodec.VORBIS => new VorbisDecoder(),
                AudioCodec.MPA => new MP3Decoder(),
                _ => throw new NotSupportedException($"FFmpeg audio decoder not available for {codec}")
            };
        }
    }

    /// <summary>
    /// FFmpeg 音频编码器工厂
    /// </summary>
    public class FFmpegAudioEncoderFactory : IAudioEncoderFactory
    {
        public string Name => "FFmpeg Audio";
        public int Priority => 100;
        public bool IsHardwareAccelerated => false;

        public AudioCodec[] SupportedCodecs => new[]
        {
            AudioCodec.AAC, AudioCodec.OPUS, AudioCodec.AMR, AudioCodec.AMR_WB,
            AudioCodec.SPEEX, AudioCodec.VORBIS, AudioCodec.MPA
        };

        public bool CanCreate(AudioCodec codec, bool preferHardware = true)
        {
            return SupportedCodecs.Contains(codec) && FFmpegHelper.IsAvailable()
                && FFmpegAudioCodecProbe.HasEncoder(codec);
        }

        public IAudioEncoder Create(AudioCodec codec)
        {
            return codec switch
            {
                AudioCodec.AAC => new AACEncoder(),
                AudioCodec.OPUS => new OpusEncoder(),
                AudioCodec.AMR => new AMREncoder(),
                AudioCodec.AMR_WB => new AMRWBEncoder(),
                AudioCodec.SPEEX => new SpeexEncoder(),
                AudioCodec.VORBIS => new VorbisEncoder(),
                AudioCodec.MPA => new MP3Encoder(),
                _ => throw new NotSupportedException($"FFmpeg audio encoder not available for {codec}")
            };
        }
    }
}
